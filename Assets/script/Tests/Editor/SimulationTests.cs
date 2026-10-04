using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Vibe.Core;
using Vibe.Core.Config;
using Vibe.Core.Contracts;

namespace Vibe.Core.Tests
{
    /// <summary>
    /// 模拟内核契约测试：tick 推进与日界换算、被动结算、键投影与效果写回、
    /// 结算倍率通道（DESIGN.md §4.3）、重规划触发（计划耗尽 / 前提失效 / 规则变更）、
    /// 事件总线（事件契约 v1：字段与次序、推送与退订、事件流确定性、与行为日志同源）、
    /// 多 agent 顺序可见性、危机检测（tick 末求值、边沿触发、同 tick 恢复不误报）、
    /// 游戏日调度挂载点（IDayScheduler：日界时序、日脚本经规则通道同 tick 生效）、
    /// 确定性（可复现），以及 M1/M2 验收——内联镜像的 m1/m2-scenario 配置自主生存
    /// （盘上文件的等价性由 SimulationConfigTests 保障）。
    /// </summary>
    public class SimulationTests
    {
        // ── 纯数据测试桩 ─────────────────────────────────────────

        private sealed class StubAction : IAction
        {
            public string Id { get; }
            public float BaseCost { get; }
            public IReadOnlyList<WorldCondition> Preconditions { get; }
            public IReadOnlyList<WorldEffect> Effects { get; }

            public StubAction(string id, float baseCost,
                IReadOnlyList<WorldCondition> preconditions = null,
                IReadOnlyList<WorldEffect> effects = null)
            {
                Id = id;
                BaseCost = baseCost;
                Preconditions = preconditions ?? Array.Empty<WorldCondition>();
                Effects = effects ?? Array.Empty<WorldEffect>();
            }
        }

        private sealed class StubGoal : IGoal
        {
            public string Id { get; }
            public float Priority { get; }
            public IReadOnlyList<WorldCondition> Conditions { get; }

            public StubGoal(string id, float priority, IReadOnlyList<WorldCondition> conditions)
            {
                Id = id;
                Priority = priority;
                Conditions = conditions;
            }
        }

        /// <summary>可变规则桩：测试中随时替换 Current 或触发 RulesChanged。</summary>
        private sealed class MutableRules : IRuleProvider
        {
            public IRuleSet Current { get; set; } = new Set(null, null);
#pragma warning disable 67
            public event Action<IRuleSet> RulesChanged;
#pragma warning restore 67

            public void Raise() => RulesChanged?.Invoke(Current);

            internal sealed class Set : IRuleSet
            {
                public IReadOnlyDictionary<string, float> ActionCostMultipliers { get; } =
                    new Dictionary<string, float>();
                public IReadOnlyDictionary<string, float> ProductionMultipliers { get; }
                public IReadOnlyDictionary<string, float> ConsumptionMultipliers { get; }

                public Set(IReadOnlyDictionary<string, float> production,
                    IReadOnlyDictionary<string, float> consumption)
                {
                    ProductionMultipliers = production ?? new Dictionary<string, float>();
                    ConsumptionMultipliers = consumption ?? new Dictionary<string, float>();
                }
            }
        }

        /// <summary>记录型日界调度桩：记录调用时的 day/tick/观察值，可与 Emitted 观察者
        /// 共用一条 trace 证明相对次序，并可注入额外行为。</summary>
        private sealed class RecordingScheduler : IDayScheduler
        {
            private readonly Simulation _sim;
            private readonly List<string> _trace;
            public readonly List<int> Days = new List<int>();
            public readonly List<int> TicksAtCall = new List<int>();
            public readonly List<double> HungerAtCall = new List<double>();
            public Action<int> Body;

            public RecordingScheduler(Simulation sim, List<string> trace)
            {
                _sim = sim;
                _trace = trace;
            }

            public void OnDayBegin(int day)
            {
                Days.Add(day);
                TicksAtCall.Add(_sim.Tick);
                HungerAtCall.Add(_sim.Current.Get("npc.n1.hunger"));
                _trace.Add("sched:" + day);
                Body?.Invoke(day);
            }
        }

        /// <summary>回调型日界调度桩：把调用转发给任意委托。</summary>
        private sealed class CallbackScheduler : IDayScheduler
        {
            private readonly Action<int> _body;
            public CallbackScheduler(Action<int> body) => _body = body;
            public void OnDayBegin(int day) => _body(day);
        }

        // ── 构造帮助 ─────────────────────────────────────────────

        private const string AgentN1 =
            "{\"id\":\"n1\",\"localKeys\":[],\"goalIds\":[\"g\"]}";
        private const string AgentN1WithHunger =
            "{\"id\":\"n1\",\"localKeys\":[\"hunger\",\"at_forest\"],\"goalIds\":[\"g\"]}";

        private static SimulationConfig MakeConfig(string passiveJson, string agentsJson, int tph = 1,
            string crisesJson = null) =>
            ContentLoader.LoadSimulation(
                "{\"version\":1,\"ticksPerGameHour\":" + tph +
                ",\"passiveEffects\":" + passiveJson + ",\"agents\":[" + agentsJson + "]" +
                (crisesJson == null ? "" : ",\"crises\":" + crisesJson) + "}");

        private static WorldState World(IDictionary<string, double> values, int tick = 0, int day = 1) =>
            new WorldState(tick, day, values ?? new Dictionary<string, double>());

        // ── 构造与生命周期 ───────────────────────────────────────

        [Test]
        public void Ctor_UnknownGoalReference_Throws()
        {
            var goals = new List<IGoal> { new StubGoal("other", 1f, new[] { WorldCondition.AtLeast("food", 1) }) };
            Assert.Throws<ArgumentException>(() => new Simulation(World(null), new List<IAction>(),
                goals, MakeConfig("[]", AgentN1)));
        }

        [Test]
        public void Ctor_NullArguments_Throw()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 1) }) };
            var cfg = MakeConfig("[]", AgentN1);
            Assert.Throws<ArgumentNullException>(
                () => new Simulation(null, new List<IAction>(), goals, cfg));
            Assert.Throws<ArgumentNullException>(
                () => new Simulation(World(null), null, goals, cfg));
            Assert.Throws<ArgumentNullException>(
                () => new Simulation(World(null), new List<IAction>(), null, cfg));
            Assert.Throws<ArgumentNullException>(
                () => new Simulation(World(null), new List<IAction>(), goals, null));
        }

        [Test]
        public void Ctor_ClonesInitialWorld()
        {
            var initial = World(new Dictionary<string, double> { ["food"] = 2 });
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(initial, new List<IAction>(), goals, MakeConfig("[]", AgentN1));

            initial.Set("food", 99); // 调用方继续改不影响模拟
            Assert.AreEqual(2d, sim.Current.Get("food"));
        }

        [Test]
        public void Run_NegativeTicks_Throws()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 1) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1));
            Assert.Throws<ArgumentOutOfRangeException>(() => sim.Run(-1));
        }

        // ── 时间模型：tick → 游戏小时 / 游戏日 ───────────────────

        [Test]
        public void Step_AdvancesTickHourDay()
        {
            // ticksPerGameHour=2 → 48 tick/日
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1, tph: 2));

            sim.Run(2);
            Assert.AreEqual(2, sim.Tick);
            Assert.AreEqual(1, sim.Day);
            Assert.AreEqual(1, sim.GameHour);

            sim.Run(46); // t=48：日界
            Assert.AreEqual(48, sim.Tick);
            Assert.AreEqual(2, sim.Day);
            Assert.AreEqual(0, sim.GameHour);
            Assert.IsTrue(sim.Log.Any(l => l.Contains("day 2 begins")), "日界应留痕");

            sim.Run(2); // t=50：小时数随 tick 换算（2 tick/小时）
            Assert.AreEqual(2, sim.Day);
            Assert.AreEqual(1, sim.GameHour);
        }

        [Test]
        public void PassiveEffects_AppliedEveryTick()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals,
                MakeConfig("[{\"key\":\"npc.n1.hunger\",\"amount\":1}]", AgentN1WithHunger));

            sim.Run(5);
            Assert.AreEqual(5d, sim.Current.Get("npc.n1.hunger"));
        }

        // ── 结算倍率通道（DESIGN.md §4.3）──────────────────────

        [Test]
        public void Settlement_ProductionMultiplier_ScalesPositiveAdd()
        {
            var actions = new List<IAction> { new StubAction("add_food", 1f, null, new[] { WorldEffect.Gain("food", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 1) }) };
            var rules = new MutableRules();
            rules.Current = new MutableRules.Set(new Dictionary<string, float> { ["food"] = 1.5f }, null);

            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1), rules: rules);
            sim.Step();

            Assert.AreEqual(1.5d, sim.Current.Get("food"), "产出类效果 × 生产倍率");
        }

        [Test]
        public void Settlement_ConsumptionMultiplier_ScalesNegativeAdd()
        {
            var actions = new List<IAction>
            {
                new StubAction("spend", 1f, null, new[]
                    { WorldEffect.Gain("wood", 1), WorldEffect.Gain("food", -1) }),
            };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("wood", 1) }) };
            var rules = new MutableRules();
            rules.Current = new MutableRules.Set(null, new Dictionary<string, float> { ["food"] = 2f });

            var sim = new Simulation(World(new Dictionary<string, double> { ["food"] = 5 }), actions, goals,
                MakeConfig("[]", AgentN1), rules: rules);
            sim.Step();

            Assert.AreEqual(1d, sim.Current.Get("wood"));
            Assert.AreEqual(3d, sim.Current.Get("food"), "消耗类效果 × 消耗倍率（5 - 1×2）");
        }

        [Test]
        public void Settlement_SetOp_BypassesChannels()
        {
            var actions = new List<IAction>
            {
                new StubAction("set_x", 1f, null, new[] { new WorldEffect("x", EffectOp.Set, 7) }),
            };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("x", 5) }) };
            var rules = new MutableRules();
            rules.Current = new MutableRules.Set(new Dictionary<string, float> { ["x"] = 100f }, null);

            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1), rules: rules);
            sim.Step();

            Assert.AreEqual(7d, sim.Current.Get("x"), "Set 是绝对设定，不经生产/消耗通道");
        }

        [Test]
        public void Settlement_NegativeMultiplier_ClampedToZero()
        {
            var actions = new List<IAction> { new StubAction("add_food", 1f, null, new[] { WorldEffect.Gain("food", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 1) }) };
            var rules = new MutableRules();
            rules.Current = new MutableRules.Set(new Dictionary<string, float> { ["food"] = -3f }, null);

            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1), rules: rules);
            sim.Run(2);

            Assert.AreEqual(2, sim.Log.Count(l => l.EndsWith("exec add_food")), "行动确实执行了两次");
            Assert.AreEqual(0d, sim.Current.Get("food"), "负倍率按 0 截断（不倒贴、不产出）");
        }

        // ── 键投影与效果写回 ─────────────────────────────────────

        [Test]
        public void WriteBack_LocalKeysGoToNpcNamespace_SharedStayBare()
        {
            var actions = new List<IAction>
            {
                new StubAction("snapshot", 1f, null, new[]
                    { new WorldEffect("hunger", EffectOp.Set, 5), WorldEffect.Gain("wood", 1) }),
            };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("hunger", 5) }) };

            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1WithHunger));
            sim.Step();

            Assert.AreEqual(5d, sim.Current.Get("npc.n1.hunger"), "hunger ∈ localKeys → 写回个体命名空间");
            Assert.AreEqual(1d, sim.Current.Get("wood"), "wood ∉ localKeys → 落共享裸键");
            Assert.IsFalse(sim.Current.TryGet("hunger", out _), "共享命名空间不应出现裸 hunger");
        }

        [Test]
        public void Projection_GoalReadsNpcKeyThroughLocalKey()
        {
            // 目标条件写在裸键 hunger 上，实际读的是投影后的 npc.n1.hunger
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("hunger", 3) }) };
            var sim = new Simulation(World(new Dictionary<string, double> { ["npc.n1.hunger"] = 4 }),
                new List<IAction>(), goals, MakeConfig("[]", AgentN1WithHunger));
            sim.Step(); // 无行动：全部满足 → 闲置

            StringAssert.Contains("idle(goal_met g)", sim.Log[0]);
        }

        // ── 重规划触发 ───────────────────────────────────────────

        [Test]
        public void Replan_WhenPlanExhausted_AndRecordsInLog()
        {
            var actions = new List<IAction> { new StubAction("add", 1f, null, new[] { WorldEffect.Gain("flag", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("flag", 1) }) };

            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1));
            sim.Step();

            StringAssert.Contains("replan(goal=g steps=1 cost=1) exec add", sim.Log[0]);
        }

        [Test]
        public void Replan_WhenNextStepPreconditionInvalidated()
        {
            // 被动 food 流失使计划第二步前提失效 → 实时重规划（GOAP.md §3 失效→重规划循环）
            var actions = new List<IAction>
            {
                new StubAction("mark", 1f,
                    new[] { WorldCondition.AtLeast("food", 1) },
                    new[] { WorldEffect.Gain("wood", 1) }),
            };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("wood", 2) }) };

            var sim = new Simulation(World(new Dictionary<string, double> { ["food"] = 3.5 }), actions, goals,
                MakeConfig("[{\"key\":\"food\",\"amount\":-1.5}]", AgentN1));
            sim.Step(); // t1：被动流失后 food=2.0 ≥ 1，计划 [mark, mark]，执行第一刀
            Assert.AreEqual(1d, sim.Current.Get("wood"));

            sim.Step(); // t2：被动流失后 food=0.5 < 1 → 第二刀前提失效 → 重规划失败 → 闲置
            Assert.AreEqual("[t=2 d=1 h=2] n1 idle(unplannable)", sim.Log[1]);
            Assert.AreEqual(1d, sim.Current.Get("wood"), "前提失效后不得继续执行旧计划");
        }

        [Test]
        public void Replan_WhenRulesChanged()
        {
            var actions = new List<IAction> { new StubAction("add", 1f, null, new[] { WorldEffect.Gain("flag", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("flag", 2) }) };
            var rules = new MutableRules();

            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1), rules: rules);
            sim.Step(); // t1：计划 [add, add]，执行第一个
            rules.Raise(); // tick 间规则替换 → 计划整体作废
            sim.Step(); // t2：作废留痕 + 重规划 + 继续执行

            Assert.IsTrue(sim.Log[1].Contains("rules changed → all plans invalidated"), "规则变更应留痕");
            StringAssert.Contains("replan(goal=g steps=1 cost=1) exec add", sim.Log[2], "作废后应重规划再执行");
            Assert.AreEqual(2d, sim.Current.Get("flag"));
        }

        [Test]
        public void Idle_WhenAllGoalsMet_ThenResumesWhenPressureBuilds()
        {
            var actions = new List<IAction> { new StubAction("add", 1f, null, new[] { WorldEffect.Gain("flag", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("flag", 1) }) };

            var sim = new Simulation(World(new Dictionary<string, double> { ["flag"] = 2 }), actions, goals,
                MakeConfig("[{\"key\":\"flag\",\"amount\":-1}]", AgentN1));
            sim.Step(); // t1：passive 后 flag=1 ≥1 → 全满足 → 闲置（不留存旧计划）
            sim.Step(); // t2：flag=0 → 重规划执行补回

            StringAssert.Contains("idle(goal_met g)", sim.Log[0]);
            StringAssert.Contains("exec add", sim.Log[1]);
            Assert.AreEqual(1d, sim.Current.Get("flag"));
        }

        [Test]
        public void Unplannable_IdlesEveryTick()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("gold", 1) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1));

            sim.Run(3);
            Assert.AreEqual(3, sim.Log.Count(l => l.Contains("idle(unplannable)")));
        }

        // ── 多 agent 顺序可见性（M2 预演，M1 验证结构就绪）──────

        [Test]
        public void AgentsActInConfigOrder_LaterSeesEarlierEffects()
        {
            var actions = new List<IAction> { new StubAction("chop", 1f, null, new[] { WorldEffect.Gain("wood", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("wood", 1) }) };
            string twoAgents = "{\"id\":\"n1\",\"localKeys\":[],\"goalIds\":[\"g\"]}," +
                               "{\"id\":\"n2\",\"localKeys\":[],\"goalIds\":[\"g\"]}";

            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", twoAgents));
            sim.Step();

            StringAssert.Contains("n1 replan(goal=g steps=1 cost=1) exec chop", sim.Log[0]);
            StringAssert.Contains("n2 idle(goal_met g)", sim.Log[1], "n2 看到 n1 已产出的 wood，目标已满足");
            Assert.AreEqual(1d, sim.Current.Get("wood"));
        }

        // ── 确定性（DESIGN.md §4.3 可复现）──────────────────────

        [Test]
        public void SameInput_ProducesIdenticalTrace()
        {
            var a = NewM1Simulation();
            var b = NewM1Simulation();
            a.Run(240);
            b.Run(240);

            Assert.IsTrue(a.Log.SequenceEqual(b.Log), "同输入同轨迹（无随机源）");
        }

        [Test]
        public void Dispose_UnsubscribesFromRules()
        {
            var rules = new MutableRules();
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1), rules: rules);
            sim.Dispose();

            Assert.DoesNotThrow(() => rules.Raise(), "退订后触发不应触及已释放的实例");
        }

        // ── 事件总线（事件契约 v1，DESIGN.md §4.4）──────────────

        [Test]
        public void Events_ReplanThenExecute_CarriesFieldsInOrder()
        {
            var actions = new List<IAction> { new StubAction("add", 1f, null, new[] { WorldEffect.Gain("flag", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("flag", 1) }) };
            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1));
            sim.Step();

            Assert.AreEqual(2, sim.Events.Count, "重规划 + 执行 = 2 条");
            var replan = sim.Events[0];
            Assert.AreEqual(SimEventType.AgentReplanned, replan.Type);
            Assert.AreEqual(1, replan.Tick);
            Assert.AreEqual(1, replan.Day);
            Assert.AreEqual("n1", replan.AgentId);
            Assert.AreEqual("g", replan.GoalId);
            Assert.AreEqual(1, replan.PlanSteps);
            Assert.AreEqual(1f, replan.PlanCost);
            Assert.IsNull(replan.ActionId, "ActionId 仅 AgentExecuted 有值");
            Assert.AreEqual(IdleReason.None, replan.Idle);

            var exec = sim.Events[1];
            Assert.AreEqual(SimEventType.AgentExecuted, exec.Type);
            Assert.AreEqual("n1", exec.AgentId);
            Assert.AreEqual("add", exec.ActionId);
            Assert.IsNull(exec.GoalId, "GoalId 仅 Replanned/GoalMet 有值");
        }

        [Test]
        public void Events_IdleGoalMet_CarriesMetGoalId()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 1) }) };
            var sim = new Simulation(World(new Dictionary<string, double> { ["food"] = 5 }),
                new List<IAction>(), goals, MakeConfig("[]", AgentN1));
            sim.Step();

            var idle = sim.Events.Single();
            Assert.AreEqual(SimEventType.AgentIdle, idle.Type);
            Assert.AreEqual(IdleReason.GoalMet, idle.Idle);
            Assert.AreEqual("g", idle.GoalId, "GoalMet 附已满足的目标 id");
            Assert.IsNull(idle.ActionId);
        }

        [Test]
        public void Events_IdleUnplannable_HasNoGoal()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("gold", 1) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1));
            sim.Step();

            var idle = sim.Events.Single();
            Assert.AreEqual(SimEventType.AgentIdle, idle.Type);
            Assert.AreEqual(IdleReason.Unplannable, idle.Idle);
            Assert.IsNull(idle.GoalId, "重规划失败无目标可附");
        }

        [Test]
        public void Events_DayBegin_AtBoundary_PrecedesAgentEventsOfThatTick()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1, tph: 2));
            sim.Run(48);

            var dayEvents = sim.Events.Where(e => e.Type == SimEventType.DayBegin).ToList();
            Assert.AreEqual(1, dayEvents.Count);
            Assert.AreEqual(48, dayEvents[0].Tick, "日界事件记新 tick");
            Assert.AreEqual(2, dayEvents[0].Day);

            int idx = sim.Events.ToList().IndexOf(dayEvents[0]);
            Assert.AreEqual(SimEventType.AgentIdle, sim.Events[idx + 1].Type,
                "日界事件先于当日行为事件（次序与 Step 固定次序一致）");
        }

        [Test]
        public void Events_PlansInvalidated_OnRulesChanged_BeforeReplan()
        {
            var actions = new List<IAction> { new StubAction("add", 1f, null, new[] { WorldEffect.Gain("flag", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("flag", 2) }) };
            var rules = new MutableRules();
            var sim = new Simulation(World(null), actions, goals, MakeConfig("[]", AgentN1), rules: rules);
            sim.Step();
            rules.Raise();
            sim.Step();

            var t2 = sim.Events.Where(e => e.Tick == 2).ToList();
            Assert.AreEqual(3, t2.Count, "作废 + 重规划 + 执行");
            Assert.AreEqual(SimEventType.PlansInvalidated, t2[0].Type, "作废事件先于重规划");
            Assert.AreEqual(SimEventType.AgentReplanned, t2[1].Type);
            Assert.AreEqual(SimEventType.AgentExecuted, t2[2].Type);
        }

        [Test]
        public void Emitted_PushesInAppendOrder_AndUnsubscribeStops()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1));

            var pushed = new List<SimEvent>();
            sim.Emitted += pushed.Add;
            sim.Run(3);
            Assert.AreEqual(3, sim.Events.Count);
            Assert.IsTrue(pushed.SequenceEqual(sim.Events), "推送序 == 留存序");

            sim.Emitted -= pushed.Add;
            sim.Step();
            Assert.AreEqual(4, sim.Events.Count, "留存继续增长");
            Assert.AreEqual(3, pushed.Count, "退订后不再推送");
        }

        [Test]
        public void SameInput_ProducesIdenticalEventStream()
        {
            var a = NewM1Simulation();
            var b = NewM1Simulation();
            a.Run(240);
            b.Run(240);

            Assert.IsTrue(a.Events.SequenceEqual(b.Events), "同输入同事件流（含 PlanCost 浮点，可复现）");
        }

        [Test]
        public void M1Scenario_EventCounts_MatchBehaviorLog()
        {
            var sim = NewM1Simulation();
            sim.Run(240);

            Assert.AreEqual(1, sim.Events.Count(e => e.Type == SimEventType.DayBegin));
            Assert.AreEqual(0, sim.Events.Count(e => e.Type == SimEventType.PlansInvalidated), "M1 无规则变更");
            Assert.AreEqual(3, sim.Events.Count(e => e.Type == SimEventType.AgentExecuted && e.ActionId == "eat"));
            Assert.AreEqual(4, sim.Events.Count(e => e.Type == SimEventType.AgentExecuted && e.ActionId == "gather_berries"));
            Assert.AreEqual(3, sim.Events.Count(e => e.Type == SimEventType.AgentExecuted && e.ActionId == "chop_wood"));
            Assert.AreEqual(230, sim.Events.Count(e => e.Type == SimEventType.AgentIdle));
            Assert.AreEqual(240, sim.Events.Count(e =>
                e.Type == SimEventType.AgentExecuted || e.Type == SimEventType.AgentIdle),
                "每 NPC 每 tick 恰一个行为结局（执行或闲置）");
        }

        // ── M1 验收：1 NPC、3 Action、2 Goal 自主生存 24 游戏小时 ──

        /// <summary>Docs/schemas/examples/m1-scenario/ 四件套的内联镜像（盘上文件另有测试）。</summary>
        private const string M1Actions =
            @"{""version"":1,""actions"":[
                {""id"":""gather_berries"",""baseCost"":4,
                 ""preconditions"":[{""key"":""at_forest"",""threshold"":1}],
                 ""effects"":[{""key"":""food"",""amount"":1}]},
                {""id"":""chop_wood"",""baseCost"":6,
                 ""preconditions"":[{""key"":""at_forest"",""threshold"":1}],
                 ""effects"":[{""key"":""wood"",""amount"":1}]},
                {""id"":""eat"",""baseCost"":1,
                 ""preconditions"":[{""key"":""food"",""threshold"":1}],
                 ""effects"":[{""key"":""food"",""amount"":-1},{""key"":""hunger"",""op"":""Set"",""amount"":0}]}]}";

        private const string M1Goals =
            @"{""version"":1,""goals"":[
                {""id"":""stay_alive"",""priority"":10,
                 ""conditions"":[{""key"":""hunger"",""op"":""LessOrEqual"",""threshold"":60}]},
                {""id"":""stock_up"",""priority"":5,
                 ""conditions"":[{""key"":""food"",""threshold"":3},{""key"":""wood"",""threshold"":3}]}]}";

        private const string M1SimConfig =
            @"{""version"":1,""ticksPerGameHour"":10,
                ""passiveEffects"":[{""key"":""npc.n1.hunger"",""amount"":1}],
                ""agents"":[{""id"":""n1"",""localKeys"":[""hunger"",""at_forest""],
                             ""goalIds"":[""stay_alive"",""stock_up""]}]}";

        private static Simulation NewM1Simulation(string simConfigJson = M1SimConfig)
        {
            var world = ContentLoader.LoadWorldState(
                @"{""version"":1,""tick"":0,""day"":1,
                    ""values"":{""food"":2,""wood"":0,""npc.n1.hunger"":0,""npc.n1.at_forest"":1}}");
            return new Simulation(world, ContentLoader.LoadActions(M1Actions),
                ContentLoader.LoadGoals(M1Goals), ContentLoader.LoadSimulation(simConfigJson));
        }

        [Test]
        public void M1Scenario_Survives24GameHours()
        {
            var sim = NewM1Simulation();
            double maxHunger = 0d;
            for (int i = 0; i < 240; i++) // 24 游戏小时 = 240 tick（10 tick/小时）
            {
                sim.Step();
                maxHunger = Math.Max(maxHunger, sim.Current.Get("npc.n1.hunger"));
            }

            // 时间：恰满一天，日界留痕
            Assert.AreEqual(240, sim.Tick);
            Assert.AreEqual(2, sim.Day);
            Assert.AreEqual(0, sim.GameHour);
            Assert.IsTrue(sim.Log.Any(l => l.Contains("day 2 begins")));

            // 生存：饥饿从不失控（过 60 当 tick 即吃回 0），资源不枯竭
            Assert.LessOrEqual(maxHunger, 61d, "饥饿越过阈值当 tick 必须进餐");
            Assert.AreEqual(3d, sim.Current.Get("food"), "末期食物存量");
            Assert.AreEqual(3d, sim.Current.Get("wood"));
            Assert.AreEqual(57d, sim.Current.Get("npc.n1.hunger"), "末期饥饿（t183 进餐清零后被动累加 240-183）");
            Assert.IsFalse(sim.Current.TryGet("hunger", out _), "个体键不得泄漏到共享命名空间");

            // 行为确实发生且成环：进食 3 次、采集 4 次、伐木 3 次、其余闲置
            Assert.AreEqual(3, sim.Log.Count(l => l.EndsWith("exec eat")), "t61/122/183 各吃一次");
            Assert.AreEqual(4, sim.Log.Count(l => l.EndsWith("exec gather_berries")), "开局 1 次 + 每次进食后补 1 次");
            Assert.AreEqual(3, sim.Log.Count(l => l.EndsWith("exec chop_wood")), "开局囤木 3 次");
            Assert.AreEqual(230, sim.Log.Count(l => l.Contains("idle(")));
            Assert.AreEqual(241, sim.Log.Count, "240 行行为日志 + 1 行日界");
        }

        // ── M2 卡②：危机检测（tick 末求值、边沿触发、事件留痕）──

        /// <summary>M1 场景配置 + 饥荒危机（食物盈耗尽即触发：阈值取 2——进食后存量恰触底）。</summary>
        private const string M1SimConfigWithFamine =
            @"{""version"":1,""ticksPerGameHour"":10,
                ""passiveEffects"":[{""key"":""npc.n1.hunger"",""amount"":1}],
                ""agents"":[{""id"":""n1"",""localKeys"":[""hunger"",""at_forest""],
                             ""goalIds"":[""stay_alive"",""stock_up""]}],
                ""crises"":[{""id"":""famine"",
                             ""conditions"":[{""key"":""food"",""op"":""LessOrEqual"",""threshold"":2}]}]}";

        [Test]
        public void Crisis_EdgeTriggered_PersistsWithoutRefire()
        {
            // flag 只降不升：进入危机后持续在场，Triggered 只发一次、无 Resolved
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("gold", 1) }) };
            var sim = new Simulation(World(new Dictionary<string, double> { ["flag"] = 3 }), new List<IAction>(),
                goals, MakeConfig("[{\"key\":\"flag\",\"amount\":-1}]", AgentN1,
                    crisesJson: "[{\"id\":\"drain\",\"conditions\":[{\"key\":\"flag\",\"op\":\"LessOrEqual\",\"threshold\":2}]}]"));
            sim.Run(3);

            var triggered = sim.Events.Where(e => e.Type == SimEventType.CrisisTriggered).ToList();
            Assert.AreEqual(1, triggered.Count, "t1 flag=2 触发；t2/t3 持续在场不重发");
            Assert.AreEqual(1, triggered[0].Tick);
            Assert.AreEqual("drain", triggered[0].CrisisId);
            Assert.IsNull(triggered[0].AgentId, "危机事件无行为主体");
            Assert.IsNull(triggered[0].ActionId);
            Assert.AreEqual(0, sim.Events.Count(e => e.Type == SimEventType.CrisisResolved));
            Assert.AreEqual(1, sim.Log.Count(l => l.Contains("crisis drain triggered")), "日志留痕一次");
        }

        [Test]
        public void Crisis_Resolved_WhenConditionClears_AfterAgentEvents()
        {
            // t1 饥饿越界→进食把 food 吃到 0（tick 末触发饥荒）；t2 采集补回（tick 末解除）
            var actions = new List<IAction>
            {
                new StubAction("gather", 4f,
                    new[] { WorldCondition.AtLeast("food", 0) },
                    new[] { WorldEffect.Gain("food", 1) }),
                new StubAction("eat", 1f,
                    new[] { WorldCondition.AtLeast("food", 1) },
                    new[] { WorldEffect.Gain("food", -1), new WorldEffect("hunger", EffectOp.Set, 0) }),
            };
            var goals = new List<IGoal>
            {
                new StubGoal("stay_alive", 10f, new[] { new WorldCondition("hunger", ConditionOp.LessOrEqual, 60) }),
                new StubGoal("stock_up", 5f, new[] { WorldCondition.AtLeast("food", 2) }),
            };
            const string agentWithSurvivalGoals =
                "{\"id\":\"n1\",\"localKeys\":[\"hunger\",\"at_forest\"],\"goalIds\":[\"stay_alive\",\"stock_up\"]}";
            var sim = new Simulation(
                World(new Dictionary<string, double> { ["food"] = 1, ["npc.n1.hunger"] = 60, ["npc.n1.at_forest"] = 1 }),
                actions, goals,
                MakeConfig("[{\"key\":\"npc.n1.hunger\",\"amount\":1}]", agentWithSurvivalGoals,
                    crisesJson: "[{\"id\":\"famine\",\"conditions\":[{\"key\":\"food\",\"op\":\"LessOrEqual\",\"threshold\":0}]}]"));

            sim.Step(); // t1：吃 → food=0 → tick 末触发
            sim.Step(); // t2：采集 → food=1 → tick 末解除

            var crisisEvents = sim.Events.Where(e => e.CrisisId != null).ToList();
            Assert.AreEqual(2, crisisEvents.Count);
            Assert.AreEqual(SimEventType.CrisisTriggered, crisisEvents[0].Type);
            Assert.AreEqual(1, crisisEvents[0].Tick);
            Assert.AreEqual(SimEventType.CrisisResolved, crisisEvents[1].Type);
            Assert.AreEqual(2, crisisEvents[1].Tick);
            // 危机事件在本 tick 行为事件之后（求值的是行动后的定局）：t1 = [Replan, Exec(eat), Triggered]
            Assert.AreEqual(SimEventType.AgentReplanned, sim.Events[0].Type);
            Assert.AreEqual(SimEventType.AgentExecuted, sim.Events[1].Type);
            Assert.AreEqual(SimEventType.CrisisTriggered, sim.Events[2].Type);
            StringAssert.Contains("crisis famine resolved", sim.Log[3]);
        }

        [Test]
        public void Crisis_SameTickRecovery_ByAgentAction_DoesNotTrigger()
        {
            // 被动流失使 food 触底，但 NPC 同 tick 补回——tick 末求值看定局，不误报
            var actions = new List<IAction> { new StubAction("add", 1f, null, new[] { WorldEffect.Gain("food", 3) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 1) }) };
            var sim = new Simulation(World(new Dictionary<string, double> { ["food"] = 2 }), actions, goals,
                MakeConfig("[{\"key\":\"food\",\"amount\":-2}]", AgentN1,
                    crisesJson: "[{\"id\":\"famine\",\"conditions\":[{\"key\":\"food\",\"op\":\"LessOrEqual\",\"threshold\":0}]}]"));

            sim.Run(5);

            Assert.AreEqual(0, sim.Events.Count(e => e.CrisisId != null),
                "每 tick 都被行动补回，危机从未在场（求值点在行动后）");
        }

        [Test]
        public void Crisis_MultipleSimultaneous_EmittedInConfigOrder()
        {
            // 两个危机同 tick 在场：事件按配置序发出（确定性）；条件可引用 npc.<id>.<key> 完整键
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("gold", 1) }) };
            var sim = new Simulation(
                World(new Dictionary<string, double> { ["food"] = 0, ["npc.n1.hunger"] = 2 }),
                new List<IAction>(), goals,
                MakeConfig("[{\"key\":\"npc.n1.hunger\",\"amount\":1}]", AgentN1,
                    crisesJson: "[{\"id\":\"famine\",\"conditions\":[{\"key\":\"food\",\"op\":\"LessOrEqual\",\"threshold\":1}]}," +
                                "{\"id\":\"exhaustion\",\"conditions\":[{\"key\":\"npc.n1.hunger\",\"op\":\"Greater\",\"threshold\":2}]}]"));

            sim.Step(); // t1：hunger=3、food=0 → 两危机同时在场

            var triggered = sim.Events.Where(e => e.Type == SimEventType.CrisisTriggered).ToList();
            Assert.AreEqual(2, triggered.Count);
            Assert.AreEqual("famine", triggered[0].CrisisId, "配置序即事件序");
            Assert.AreEqual("exhaustion", triggered[1].CrisisId);
            Assert.AreEqual(1, triggered[0].Tick);
            Assert.AreEqual(1, triggered[0].Day);
        }

        [Test]
        public void Crisis_M1Scenario_FamineCycle_TriggersAndRearms()
        {
            // M1 全程 + food≤2 危机：每次进食触底触发、次 tick 采集补回解除——3 轮触发/解除，tick 精确
            var sim = NewM1Simulation(M1SimConfigWithFamine);
            sim.Run(240);

            var triggered = sim.Events.Where(e => e.Type == SimEventType.CrisisTriggered).ToList();
            Assert.AreEqual(3, triggered.Count, "t61/122/183 进食后 food=2 触发");
            CollectionAssert.AreEqual(new[] { 61, 122, 183 }, triggered.Select(e => e.Tick).ToList());
            Assert.IsTrue(triggered.All(e => e.CrisisId == "famine"));

            var resolved = sim.Events.Where(e => e.Type == SimEventType.CrisisResolved).ToList();
            Assert.AreEqual(3, resolved.Count, "t62/123/184 采集补回后解除");
            CollectionAssert.AreEqual(new[] { 62, 123, 184 }, resolved.Select(e => e.Tick).ToList());
        }

        // ── M2 卡③：多 NPC 资源竞争/协作，自主运转 3 游戏日（DoD-1 预演）──

        /// <summary>m2-scenario 的目标库：囤积目标按 NPC 分叉（n1→stock_food、n2→stock_wood）制造分工。</summary>
        private const string M2Goals =
            @"{""version"":1,""goals"":[
                {""id"":""stay_alive"",""priority"":10,
                 ""conditions"":[{""key"":""hunger"",""op"":""LessOrEqual"",""threshold"":60}]},
                {""id"":""stock_food"",""priority"":5,
                 ""conditions"":[{""key"":""food"",""threshold"":4}]},
                {""id"":""stock_wood"",""priority"":5,
                 ""conditions"":[{""key"":""wood"",""threshold"":4}]}]}";

        private const string M2SimConfig =
            @"{""version"":1,""ticksPerGameHour"":10,
                ""passiveEffects"":[{""key"":""npc.n1.hunger"",""amount"":1},{""key"":""npc.n2.hunger"",""amount"":1}],
                ""agents"":[{""id"":""n1"",""localKeys"":[""hunger"",""at_forest""],
                             ""goalIds"":[""stay_alive"",""stock_food""]},
                            {""id"":""n2"",""localKeys"":[""hunger"",""at_forest""],
                             ""goalIds"":[""stay_alive"",""stock_wood""]}],
                ""crises"":[{""id"":""famine"",
                             ""conditions"":[{""key"":""food"",""op"":""LessOrEqual"",""threshold"":1}]}]}";

        private static Simulation NewM2Simulation()
        {
            var world = ContentLoader.LoadWorldState(
                @"{""version"":1,""tick"":0,""day"":1,
                    ""values"":{""food"":2,""wood"":0,
                                ""npc.n1.hunger"":0,""npc.n2.hunger"":0,
                                ""npc.n1.at_forest"":1,""npc.n2.at_forest"":1}}");
            return new Simulation(world, ContentLoader.LoadActions(M1Actions), // 行动库与 M1 相同：竞争/分工全靠目标分叉
                ContentLoader.LoadGoals(M2Goals), ContentLoader.LoadSimulation(M2SimConfig));
        }

        [Test]
        public void M2Scenario_TwoNpcDivisionOfLabour_Survives3GameDays()
        {
            var sim = NewM2Simulation();
            double maxHunger1 = 0d, maxHunger2 = 0d, minFood = double.MaxValue;
            for (int i = 0; i < 720; i++) // 3 游戏日 = 720 tick
            {
                sim.Step();
                maxHunger1 = Math.Max(maxHunger1, sim.Current.Get("npc.n1.hunger"));
                maxHunger2 = Math.Max(maxHunger2, sim.Current.Get("npc.n2.hunger"));
                minFood = Math.Min(minFood, sim.Current.Get("food"));
            }

            // 自主运转 3 游戏日，时间与日界正确
            Assert.AreEqual(720, sim.Tick);
            Assert.AreEqual(4, sim.Day);
            Assert.AreEqual(0, sim.GameHour);
            Assert.AreEqual(3, sim.Events.Count(e => e.Type == SimEventType.DayBegin));

            // 生存：无饿死（从未 unplannable）、饥饿不失控（越 60 当 tick 进食）
            Assert.AreEqual(0, sim.Events.Count(e => e.Idle == IdleReason.Unplannable), "无 NPC 陷入不可规划");
            Assert.LessOrEqual(maxHunger1, 61d);
            Assert.LessOrEqual(maxHunger2, 61d);

            // 分工（目标分叉的涌现行为）：n1 只采集、n2 只伐木
            Assert.AreEqual(24, sim.Events.Count(e => e.AgentId == "n1" && e.ActionId == "gather_berries"));
            Assert.AreEqual(0, sim.Events.Count(e => e.AgentId == "n1" && e.ActionId == "chop_wood"));
            Assert.AreEqual(4, sim.Events.Count(e => e.AgentId == "n2" && e.ActionId == "chop_wood"));
            Assert.AreEqual(0, sim.Events.Count(e => e.AgentId == "n2" && e.ActionId == "gather_berries"));

            // 竞争：两人吃同一份食物（各 11 次、同 tick 相位同步——顺序结算下后者看到前者吃后的余量）
            var eat1 = sim.Events.Where(e => e.AgentId == "n1" && e.ActionId == "eat").Select(e => e.Tick).ToList();
            var eat2 = sim.Events.Where(e => e.AgentId == "n2" && e.ActionId == "eat").Select(e => e.Tick).ToList();
            Assert.AreEqual(11, eat1.Count);
            CollectionAssert.AreEqual(eat1, eat2, "hunger 相位同步 → 同 tick 双吃（对食物的直接竞争）");

            // 协作 + 资源守恒：n1 采 24 供两人吃 22 并囤至 4（2 + 24 - 22 = 4）；n2 囤木 4
            Assert.AreEqual(4d, sim.Current.Get("food"));
            Assert.AreEqual(4d, sim.Current.Get("wood"));
            Assert.AreEqual(49d, sim.Current.Get("npc.n1.hunger"), "末期饥饿（t671 进食清零后累加 720-671）");
            Assert.AreEqual(49d, sim.Current.Get("npc.n2.hunger"));

            // 危机监测在岗不误报：协作良好时 food 谷值不破饥荒线
            Assert.GreaterOrEqual(minFood, 2d, "食物谷值（危机阈值 food≤1 从未触及）");
            Assert.AreEqual(0, sim.Events.Count(e => e.CrisisId != null));
        }

        [Test]
        public void M2Scenario_SameInput_ProducesIdenticalEventStream()
        {
            var a = NewM2Simulation();
            var b = NewM2Simulation();
            a.Run(720);
            b.Run(720);

            Assert.IsTrue(a.Events.SequenceEqual(b.Events), "多 NPC 同输入同事件流（可复现）");
        }

        // ── 游戏日推进调度（M2 收尾）：日界挂载点 IDayScheduler ──

        [Test]
        public void DayScheduler_FiresOncePerBoundary_AfterDayBeginEvent_BeforeSettlement()
        {
            // tph=2 → 48 tick/日；Run(120) 恰跨两个日界（t=48→day2、t=96→day3），初始日不调用
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(World(new Dictionary<string, double> { ["npc.n1.hunger"] = 0 }),
                new List<IAction>(), goals,
                MakeConfig("[{\"key\":\"npc.n1.hunger\",\"amount\":1}]", AgentN1, tph: 2));

            var trace = new List<string>();
            sim.Emitted += e => trace.Add("evt:" + e.Type);
            var sched = new RecordingScheduler(sim, trace);
            sim.DayScheduler = sched;

            sim.Run(120);

            CollectionAssert.AreEqual(new[] { 2, 3 }, sched.Days,
                "每个日界恰一次；初始日不调用（与 DayBegin 事件语义一致）");
            Assert.AreEqual(48, sched.TicksAtCall[0], "回调时时间戳已推进到新日首个 tick");
            Assert.AreEqual(47d, sched.HungerAtCall[0],
                "回调先于被动结算：所见是昨日收盘值（此前 47 tick 各 +1）");
            int i = trace.IndexOf("evt:DayBegin");
            Assert.GreaterOrEqual(i, 0);
            Assert.AreEqual("sched:2", trace[i + 1], "挂载点紧随 DayBegin 事件，两者之间无任何事件");
        }

        [Test]
        public void DayScheduler_DayScriptViaRulesChannel_AppliesFromFirstTickOfNewDay()
        {
            // 日界回调内替换规则（M3 每日世界脚本的生效通道）：同 tick 失效全部计划 → NPC 立即
            // 按新规则重规划——新日从第一个 tick 就受新规则约束（次序由 Step 固定次序保证）
            var actions = new List<IAction> { new StubAction("add", 1f, null, new[] { WorldEffect.Gain("flag", 1) }) };
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("flag", 2) }) };
            var rules = new MutableRules();
            var sim = new Simulation(World(null), actions, goals,
                MakeConfig("[{\"key\":\"flag\",\"amount\":-1}]", AgentN1, tph: 2), rules: rules);
            sim.DayScheduler = new CallbackScheduler(d => rules.Raise());

            sim.Run(48); // t=48：day 2 首个 tick

            var t48 = sim.Events.Where(e => e.Tick == 48).ToList();
            CollectionAssert.AreEqual(
                new[] { SimEventType.DayBegin, SimEventType.PlansInvalidated,
                    SimEventType.AgentReplanned, SimEventType.AgentExecuted },
                t48.Select(e => e.Type).ToList(),
                "日界 → 规则生效作废 → 重规划 → 执行：全部落在当日首个 tick");
        }

        [Test]
        public void DayScheduler_NoOp_LeavesEventStreamUnchanged()
        {
            var a = NewM1Simulation();
            var b = NewM1Simulation();
            b.DayScheduler = new CallbackScheduler(_ => { });

            a.Run(240);
            b.Run(240);

            Assert.IsTrue(a.Events.SequenceEqual(b.Events), "空挂载点不改变模拟（纯可选扩展）");
        }

        [Test]
        public void DayScheduler_Throws_PropagatesFailFast()
        {
            var goals = new List<IGoal> { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 99) }) };
            var sim = new Simulation(World(null), new List<IAction>(), goals, MakeConfig("[]", AgentN1, tph: 1));
            sim.DayScheduler = new CallbackScheduler(_ => throw new InvalidOperationException("day script missing"));

            sim.Run(23); // 初始日内正常
            Assert.Throws<InvalidOperationException>(() => sim.Run(2),
                "t=24 日界：回调异常不被吞（fail-fast，与 Emitted 观察者一致）");
        }
    }
}
