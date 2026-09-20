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
    /// 多 agent 顺序可见性、确定性（可复现），以及 M1 验收——内联镜像的 m1-scenario
    /// 配置跑满 24 游戏小时自主生存（盘上文件的等价性由 SimulationConfigTests 保障）。
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

        // ── 构造帮助 ─────────────────────────────────────────────

        private const string AgentN1 =
            "{\"id\":\"n1\",\"localKeys\":[],\"goalIds\":[\"g\"]}";
        private const string AgentN1WithHunger =
            "{\"id\":\"n1\",\"localKeys\":[\"hunger\",\"at_forest\"],\"goalIds\":[\"g\"]}";

        private static SimulationConfig MakeConfig(string passiveJson, string agentsJson, int tph = 1) =>
            ContentLoader.LoadSimulation(
                "{\"version\":1,\"ticksPerGameHour\":" + tph +
                ",\"passiveEffects\":" + passiveJson + ",\"agents\":[" + agentsJson + "]}");

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

        private static Simulation NewM1Simulation()
        {
            var world = ContentLoader.LoadWorldState(
                @"{""version"":1,""tick"":0,""day"":1,
                    ""values"":{""food"":2,""wood"":0,""npc.n1.hunger"":0,""npc.n1.at_forest"":1}}");
            return new Simulation(world, ContentLoader.LoadActions(M1Actions),
                ContentLoader.LoadGoals(M1Goals), ContentLoader.LoadSimulation(M1SimConfig));
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
    }
}
