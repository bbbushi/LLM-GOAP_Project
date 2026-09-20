using System;
using System.Collections.Generic;
using NUnit.Framework;
using Vibe.Core;
using Vibe.Core.Config;
using Vibe.Core.Contracts;

namespace Vibe.Core.Tests
{
    /// <summary>
    /// GoapPlanner 契约测试：A* 规划正确性（最优链选择、目标优先级语义）、规则倍率通道
    /// （DESIGN.md §4.3）、三重防爆手段（二叉堆 / 签名去重 / 迭代与深度预算）、
    /// 输入不变性与确定性（可复现）。场景与数字对齐 Docs/GOAP.md §4–§5 的示例。
    /// 另含 BinaryHeap（internal 工具，经 InternalsVisibleTo 直测）与 ContentLoader 配置的端到端规划。
    /// </summary>
    public class PlannerTests
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

        private sealed class StubRules : IRuleProvider
        {
            public IRuleSet Current { get; }
#pragma warning disable 67 // 事件无订阅者是桩的正常状态
            public event Action<IRuleSet> RulesChanged;
#pragma warning restore 67

            public StubRules(IReadOnlyDictionary<string, float> costMultipliers = null)
            {
                Current = new Set(costMultipliers);
            }

            private sealed class Set : IRuleSet
            {
                public IReadOnlyDictionary<string, float> ActionCostMultipliers { get; }
                public IReadOnlyDictionary<string, float> ProductionMultipliers { get; }
                    = new Dictionary<string, float>();
                public IReadOnlyDictionary<string, float> ConsumptionMultipliers { get; }
                    = new Dictionary<string, float>();

                public Set(IReadOnlyDictionary<string, float> costMultipliers)
                {
                    ActionCostMultipliers = costMultipliers ?? new Dictionary<string, float>();
                }
            }
        }

        // ── 场景库（GOAP.md §4 演示的镜像）───────────────────────

        /// <summary>GOAP.md §4 行动库：买面包(2) / 收集浆果(4) / 前往市场(1) / 前往森林(1)。</summary>
        private static List<IAction> MarketLibrary() => new List<IAction>
        {
            new StubAction("buy_bread", 2f,
                new[] { WorldCondition.AtLeast("money", 5), WorldCondition.AtLeast("at_market", 1) },
                new[] { WorldEffect.Gain("food", 3), WorldEffect.Gain("money", -5) }),
            new StubAction("gather_berries", 4f,
                new[] { WorldCondition.AtLeast("at_forest", 1) },
                new[] { WorldEffect.Gain("food", 1) }),
            new StubAction("go_market", 1f,
                null,
                new[] { WorldEffect.Gain("at_market", 1), WorldEffect.Gain("at_forest", -1) }),
            new StubAction("go_forest", 1f,
                null,
                new[] { WorldEffect.Gain("at_forest", 1), WorldEffect.Gain("at_market", -1) }),
        };

        /// <summary>GOAP.md §5 场景行动库：浆果链 5 对 市场链 6，成本最接近，便于验证倍率翻转。</summary>
        private static List<IAction> WeatherFlipLibrary() => new List<IAction>
        {
            new StubAction("buy_bread", 5f,
                new[] { WorldCondition.AtLeast("money", 5), WorldCondition.AtLeast("at_market", 1) },
                new[] { WorldEffect.Gain("food", 3), WorldEffect.Gain("money", -5) }),
            new StubAction("gather_berries", 2f,
                new[] { WorldCondition.AtLeast("at_forest", 1) },
                new[] { WorldEffect.Gain("food", 1) }),
            new StubAction("go_market", 1f,
                null,
                new[] { WorldEffect.Gain("at_market", 1), WorldEffect.Gain("at_forest", -1) }),
            new StubAction("go_forest", 1f,
                null,
                new[] { WorldEffect.Gain("at_forest", 1), WorldEffect.Gain("at_market", -1) }),
        };

        private static IWorldState Start(int food, int money) =>
            new WorldState(0, 1, new Dictionary<string, double> { ["food"] = food, ["money"] = money });

        private static IReadOnlyList<IGoal> StayFed(float priority = 10f) =>
            new[] { new StubGoal("stay_fed", priority, new[] { WorldCondition.AtLeast("food", 2) }) };

        private static string StepIds(Plan plan) =>
            string.Join(",", System.Linq.Enumerable.Select(plan.Steps, s => s.Id));

        // ── A* 规划正确性（GOAP.md §4 演示）──────────────────────

        [Test]
        public void Plan_MarketScenario_PicksCheapestChain()
        {
            // GOAP.md §4 预期输出：买面包链 1+2=3 便宜过浆果链 1+4+4=9
            var plan = new GoapPlanner().Plan(Start(0, 10), StayFed(), MarketLibrary(), null);

            Assert.IsNotNull(plan);
            Assert.AreEqual("go_market,buy_bread", StepIds(plan));
            Assert.AreEqual(3f, plan.TotalCost, 1e-6f);
            Assert.AreEqual("stay_fed", plan.Goal.Id);
        }

        [Test]
        public void Plan_NoMoney_FlipsToForestChain()
        {
            // GOAP.md §4 动手实验 1：money=0 → 买面包前提失效，翻转为去森林采两次
            var plan = new GoapPlanner().Plan(Start(0, 0), StayFed(), MarketLibrary(), null);

            Assert.IsNotNull(plan);
            Assert.AreEqual("go_forest,gather_berries,gather_berries", StepIds(plan));
            Assert.AreEqual(9f, plan.TotalCost, 1e-6f);
        }

        [Test]
        public void Plan_NoTravelAction_UnreachableReturnsNull()
        {
            // GOAP.md §4 动手实验 2：删掉 go_forest → 浆果不可达（此即规则验证层沙盒可达性检查的原理）
            var library = MarketLibrary();
            library.RemoveAll(a => a.Id == "go_forest");

            Assert.IsNull(new GoapPlanner().Plan(Start(0, 0), StayFed(), library, null));
        }

        [Test]
        public void Plan_ApplyingStepsToClone_ReachesGoal()
        {
            var start = Start(0, 10);
            var goal = StayFed()[0];
            var plan = new GoapPlanner().Plan(start, StayFed(), MarketLibrary(), null);

            var sim = start.Clone();
            foreach (var step in plan.Steps) sim.Apply(step.Effects);
            Assert.IsTrue(sim.Meets(goal.Conditions), "按计划逐步应用效果后应满足目标条件");
        }

        [Test]
        public void Plan_MultiConditionGoal_AllConditionsMet()
        {
            var library = new List<IAction>
            {
                new StubAction("gather", 1f, null, new[] { WorldEffect.Gain("food", 1) }),
                new StubAction("chop", 2f, null, new[] { WorldEffect.Gain("wood", 1) }),
            };
            var goals = new[] { new StubGoal("stock_both", 1f, new[]
            {
                WorldCondition.AtLeast("food", 2), WorldCondition.AtLeast("wood", 1),
            }) };

            var plan = new GoapPlanner().Plan(new WorldState(), goals, library, null);

            Assert.IsNotNull(plan);
            Assert.AreEqual(4f, plan.TotalCost, 1e-6f); // 1+1+2，三步凑齐两条件
            var sim = new WorldState();
            foreach (var step in plan.Steps) sim.Apply(step.Effects);
            Assert.IsTrue(sim.Meets(goals[0].Conditions));
        }

        // ── 目标选择语义 ─────────────────────────────────────────

        [Test]
        public void Plan_GoalAlreadyMet_ReturnsEmptyPlanZeroCost()
        {
            var plan = new GoapPlanner().Plan(Start(5, 0), StayFed(), MarketLibrary(), null);

            Assert.IsNotNull(plan);
            Assert.AreEqual(0, plan.Steps.Count, "目标已满足 → 空步计划（无事可做）");
            Assert.AreEqual(0f, plan.TotalCost, 1e-6f);
        }

        [Test]
        public void Plan_MetHighGoal_WinsOverUnmetLowerGoal()
        {
            var goals = new List<IGoal>
            {
                new StubGoal("stay_fed", 10f, new[] { WorldCondition.AtLeast("food", 2) }),
                new StubGoal("stock_wood", 5f, new[] { WorldCondition.AtLeast("wood", 1) }),
            };
            var library = new List<IAction>
            {
                new StubAction("chop", 2f, null, new[] { WorldEffect.Gain("wood", 1) }),
            };

            var plan = new GoapPlanner().Plan(Start(5, 0), goals, library, null);

            Assert.IsNotNull(plan);
            Assert.AreEqual("stay_fed", plan.Goal.Id, "高优先级目标已满足时选中它（空计划），不落向低目标");
            Assert.AreEqual(0, plan.Steps.Count);
        }

        [Test]
        public void Plan_UnreachableHighGoal_FallsToNextReachable()
        {
            var goals = new List<IGoal>
            {
                new StubGoal("impossible", 10f, new[] { WorldCondition.AtLeast("wood", 1) }),
                new StubGoal("stay_fed", 5f, new[] { WorldCondition.AtLeast("food", 2) }),
            };
            var library = new List<IAction>
            {
                new StubAction("gather", 1f, null, new[] { WorldEffect.Gain("food", 1) }),
            };

            var plan = new GoapPlanner().Plan(new WorldState(), goals, library, null);

            Assert.IsNotNull(plan);
            Assert.AreEqual("stay_fed", plan.Goal.Id, "高优先级不可达应落向次优先级");
            Assert.AreEqual("gather,gather", StepIds(plan));
        }

        [Test]
        public void Plan_AllGoalsUnreachable_ReturnsNull()
        {
            var goals = new List<IGoal>
            {
                new StubGoal("g1", 10f, new[] { WorldCondition.AtLeast("wood", 1) }),
                new StubGoal("g2", 5f, new[] { WorldCondition.AtLeast("gold", 1) }),
            };
            var library = new List<IAction>
            {
                new StubAction("gather", 1f, null, new[] { WorldEffect.Gain("food", 1) }),
            };

            Assert.IsNull(new GoapPlanner().Plan(new WorldState(), goals, library, null));
        }

        [Test]
        public void Plan_EmptyGoalList_ReturnsNull()
        {
            Assert.IsNull(new GoapPlanner().Plan(
                new WorldState(), new List<IGoal>(), MarketLibrary(), null));
        }

        [Test]
        public void Plan_EqualPriorities_TriedInInputOrder()
        {
            var goals = new List<IGoal>
            {
                new StubGoal("first", 5f, new[] { WorldCondition.AtLeast("food", 2) }),
                new StubGoal("second", 5f, new[] { WorldCondition.AtLeast("wood", 1) }),
            };
            var start = Start(3, 0); // first 已满足
            var library = new List<IAction>
            {
                new StubAction("chop", 2f, null, new[] { WorldEffect.Gain("wood", 1) }),
            };

            var plan = new GoapPlanner().Plan(start, goals, library, null);

            Assert.AreEqual("first", plan.Goal.Id, "平级目标按输入序先试第一个");
        }

        // ── 规则倍率通道（GOAP.md §5 / DESIGN.md §4.3）──────────

        [Test]
        public void Plan_CostMultiplier_FlipsPlanToAlternativeChain()
        {
            // GOAP.md §5：平时浆果链 1+2+2=5 < 市场链 1+5=6 → 去森林
            var start = Start(0, 10);
            var planner = new GoapPlanner();
            var dryDay = planner.Plan(start, StayFed(), WeatherFlipLibrary(), new StubRules());
            Assert.AreEqual("go_forest,gather_berries,gather_berries", StepIds(dryDay));
            Assert.AreEqual(5f, dryDay.TotalCost, 1e-6f);

            // 暴雨日「收集浆果」×2：浆果链 1+4+4=9 > 6 → 计划自动翻转为买面包
            var rain = new StubRules(new Dictionary<string, float> { ["gather_berries"] = 2f });
            var wetDay = planner.Plan(start, StayFed(), WeatherFlipLibrary(), rain);
            Assert.AreEqual("go_market,buy_bread", StepIds(wetDay));
            Assert.AreEqual(6f, wetDay.TotalCost, 1e-6f, "总代价应含规则乘数");
        }

        [Test]
        public void Plan_MultiplierMissingKey_TreatedAsOne()
        {
            var rules = new StubRules(new Dictionary<string, float> { ["unrelated"] = 9f });
            var plan = new GoapPlanner().Plan(Start(0, 10), StayFed(), WeatherFlipLibrary(), rules);

            Assert.AreEqual("go_forest,gather_berries,gather_berries", StepIds(plan), "缺键乘数视为 1，行为同无规则");
        }

        [Test]
        public void Plan_NullRules_SamePlanAsDefault()
        {
            var withNull = new GoapPlanner().Plan(Start(0, 10), StayFed(), WeatherFlipLibrary(), null);
            var withDefault = new GoapPlanner().Plan(Start(0, 10), StayFed(), WeatherFlipLibrary(), new StubRules());

            Assert.AreEqual(StepIds(withDefault), StepIds(withNull));
            Assert.AreEqual(withDefault.TotalCost, withNull.TotalCost, 1e-6f);
        }

        [Test]
        public void Plan_NegativeMultiplier_ClampedToZero()
        {
            // A* 要求边权非负：负乘数按 0 截断（防御兜底，正常规则集不该出现）
            var rules = new StubRules(new Dictionary<string, float> { ["buy_bread"] = -3f });
            var plan = new GoapPlanner().Plan(Start(0, 10), StayFed(), WeatherFlipLibrary(), rules);

            Assert.AreEqual("go_market,buy_bread", StepIds(plan), "买面包免费 → 市场链 1+0 最便宜");
            Assert.AreEqual(1f, plan.TotalCost, 1e-6f);
        }

        // ── 输入不变性与确定性（DESIGN.md §4.3 可复现）──────────

        [Test]
        public void Plan_DoesNotMutateStartState()
        {
            var start = Start(0, 10);
            new GoapPlanner().Plan(start, StayFed(), MarketLibrary(), null);

            Assert.AreEqual(0d, start.Get("food"));
            Assert.AreEqual(10d, start.Get("money"));
            Assert.IsFalse(start.TryGet("at_market", out _), "搜索的假想键不应泄入真实状态");
            Assert.IsFalse(start.TryGet("at_forest", out _));
        }

        [Test]
        public void Plan_SameInput_ProducesSamePlan()
        {
            var planner = new GoapPlanner();
            var a = planner.Plan(Start(0, 10), StayFed(), MarketLibrary(), null);
            var b = planner.Plan(Start(0, 10), StayFed(), MarketLibrary(), null);

            Assert.AreEqual(StepIds(a), StepIds(b));
            Assert.AreEqual(a.TotalCost, b.TotalCost, 1e-6f);
        }

        [Test]
        public void Plan_NullArguments_Throw()
        {
            var planner = new GoapPlanner();
            Assert.Throws<ArgumentNullException>(
                () => planner.Plan(null, StayFed(), MarketLibrary(), null));
            Assert.Throws<ArgumentNullException>(
                () => planner.Plan(new WorldState(), null, MarketLibrary(), null));
            Assert.Throws<ArgumentNullException>(
                () => planner.Plan(new WorldState(), StayFed(), null, null));
        }

        // ── 防爆手段：签名去重 / 迭代预算 / 深度上限 ─────────────

        [Test]
        public void Plan_NoOpEffectAction_TerminatesViaFingerprintDedup()
        {
            // 空效果行动不改变状态：签名去重应阻止自环无限扩张，搜索快速穷尽返回 null
            var library = new List<IAction>
            {
                new StubAction("idle", 1f, null, null),
            };

            Assert.IsNull(new GoapPlanner().Plan(new WorldState(), StayFed(), library, null));
        }

        [Test]
        public void Plan_IterationBudgetExhausted_ReturnsNull()
        {
            // 预算 1：弹出根节点后即耗尽，尚未及展开后续层 → 不可达
            var library = new List<IAction>
            {
                new StubAction("gather", 1f, null, new[] { WorldEffect.Gain("food", 1) }),
            };
            var planner = new GoapPlanner(maxIterations: 1);

            Assert.IsNull(planner.Plan(new WorldState(), StayFed(), library, null));
        }

        [Test]
        public void Plan_DepthCap_BlocksPlansBeyondLimit()
        {
            var library = new List<IAction>
            {
                new StubAction("gather", 1f, null, new[] { WorldEffect.Gain("food", 1) }),
            };

            // food≥5 需 5 步，超过深度上限 2 → 不可达
            var capped = new GoapPlanner(maxDepth: 2);
            var goals = new[] { new StubGoal("stock", 1f, new[] { WorldCondition.AtLeast("food", 5) }) };
            Assert.IsNull(capped.Plan(new WorldState(), goals, library, null));

            // 默认深度下同问题可解：5 步、总代价 5
            var plan = new GoapPlanner().Plan(new WorldState(), goals, library, null);
            Assert.AreEqual(5, plan.Steps.Count);
            Assert.AreEqual(5f, plan.TotalCost, 1e-6f);
        }

        [Test]
        public void Plan_ZeroCostAction_DoesNotLoop()
        {
            // 0 代价合法（免费行动）：配合签名去重不得死循环
            var library = new List<IAction>
            {
                new StubAction("breathe", 0f, null, null),
                new StubAction("gather", 1f, null, new[] { WorldEffect.Gain("food", 1) }),
            };

            var plan = new GoapPlanner().Plan(new WorldState(), StayFed(), library, null);
            Assert.IsNotNull(plan);
            Assert.AreEqual("gather,gather", StepIds(plan), "空效果免费行动不应出现在计划里");
        }

        [Test]
        public void Ctor_InvalidBudgets_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GoapPlanner(maxIterations: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GoapPlanner(maxDepth: -1));
        }

        // ── 端到端：ContentLoader 配置 → 规划 ────────────────────

        [Test]
        public void Plan_AgainstLoadedConfigs_EndToEnd()
        {
            // 与 Docs/schemas/examples 同步的正式配置格式：加载 → 喂给规划器
            const string actionsJson = @"{""version"":1,""actions"":[
                {""id"":""gather_berries"",""baseCost"":4,
                 ""preconditions"":[{""key"":""at_forest"",""threshold"":1}],
                 ""effects"":[{""key"":""food"",""amount"":1}]},
                {""id"":""chop_wood"",""baseCost"":6,
                 ""preconditions"":[{""key"":""at_forest"",""threshold"":1}],
                 ""effects"":[{""key"":""wood"",""amount"":1}]}]}";
            const string goalsJson = @"{""version"":1,""goals"":[
                {""id"":""stay_fed"",""priority"":10,""conditions"":[{""key"":""food"",""threshold"":2}]},
                {""id"":""stock_wood"",""priority"":5,""conditions"":[{""key"":""wood"",""threshold"":1}]}]}";

            var actions = ContentLoader.LoadActions(actionsJson);
            var goals = ContentLoader.LoadGoals(goalsJson);
            var world = new WorldState();
            world.Set("at_forest", 1); // NPC 在森林

            var plan = new GoapPlanner().Plan(world, goals, actions, null);

            Assert.IsNotNull(plan);
            Assert.AreEqual("stay_fed", plan.Goal.Id, "优先规划高优先级目标");
            Assert.AreEqual("gather_berries,gather_berries", StepIds(plan));
            Assert.AreEqual(8f, plan.TotalCost, 1e-6f);
        }

        // ── BinaryHeap 直测（internal 工具）──────────────────────

        [Test]
        public void Heap_PushPop_AscendingOrder()
        {
            var heap = new BinaryHeap<int>((a, b) => a.CompareTo(b));
            foreach (var v in new[] { 5, 1, 4, 2, 3 }) heap.Push(v);

            Assert.AreEqual(5, heap.Count);
            for (int expected = 1; expected <= 5; expected++)
            {
                Assert.AreEqual(expected, heap.Pop());
                Assert.AreEqual(5 - expected, heap.Count);
            }
        }

        [Test]
        public void Heap_Peek_DoesNotRemove()
        {
            var heap = new BinaryHeap<int>((a, b) => a.CompareTo(b));
            heap.Push(3);
            heap.Push(1);

            Assert.AreEqual(1, heap.Peek());
            Assert.AreEqual(2, heap.Count);
        }

        [Test]
        public void Heap_PopEmpty_Throws()
        {
            var heap = new BinaryHeap<int>((a, b) => a.CompareTo(b));
            Assert.Throws<InvalidOperationException>(() => heap.Pop());
            Assert.Throws<InvalidOperationException>(() => heap.Peek());
        }

        [Test]
        public void Heap_Duplicates_AllRetained()
        {
            var heap = new BinaryHeap<int>((a, b) => a.CompareTo(b));
            heap.Push(2);
            heap.Push(2);
            heap.Push(2);

            Assert.AreEqual(3, heap.Count);
            Assert.AreEqual(2, heap.Pop());
            Assert.AreEqual(2, heap.Pop());
            Assert.AreEqual(2, heap.Pop());
            Assert.AreEqual(0, heap.Count);
        }

        [Test]
        public void Heap_InterleavedPushPop_AlwaysMinNext()
        {
            var heap = new BinaryHeap<int>((a, b) => a.CompareTo(b));
            heap.Push(3);
            Assert.AreEqual(3, heap.Pop());

            heap.Push(1);
            heap.Push(2);
            Assert.AreEqual(1, heap.Pop());

            heap.Push(0);
            Assert.AreEqual(0, heap.Pop());
            Assert.AreEqual(2, heap.Pop());
            Assert.AreEqual(0, heap.Count);
        }

        [Test]
        public void Heap_CustomComparison_Respected()
        {
            // 按字符串长度定序：短者先
            var heap = new BinaryHeap<string>((a, b) => a.Length.CompareTo(b.Length));
            heap.Push("aaa");
            heap.Push("b");
            heap.Push("cc");

            Assert.AreEqual("b", heap.Pop());
            Assert.AreEqual("cc", heap.Pop());
            Assert.AreEqual("aaa", heap.Pop());
        }

        [Test]
        public void Heap_NullComparison_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new BinaryHeap<int>(null));
        }

        [Test]
        public void Heap_LargeSequence_RemainsOrdered()
        {
            // 固定种子的 LCG 伪随机序列（不用 System.Random，跨运行确定）
            var heap = new BinaryHeap<int>((a, b) => a.CompareTo(b));
            uint x = 123456789u;
            for (int i = 0; i < 200; i++)
            {
                x = x * 1664525u + 1013904223u;
                heap.Push((int)(x % 1000));
            }

            int prev = int.MinValue;
            while (heap.Count > 0)
            {
                int v = heap.Pop();
                Assert.IsTrue(v >= prev, "出堆序列应非降");
                prev = v;
            }
        }
    }
}
