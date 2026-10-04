using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Vibe.Core;
using Vibe.Core.Config;
using Vibe.Core.Contracts;
using Vibe.Core.Rules;

namespace Vibe.Core.Tests
{
    /// <summary>
    /// 规则验证层契约测试（M3 卡①）：规则提案加载器逐条镜像 rule-proposal.schema.json v1 约束
    /// （未知字段、版本、非负乘数、非数值、通道形状）；RuleSet/MutableRuleProvider（应用/回退通道）；
    /// RuleValidator 三段管线——结构校验失败先于沙盒、幻觉键整体拒绝、沙盒裁决（零不可规划、
    /// 逐 tick 末不变式、乘数确实在沙盒生效、种子隔离、确定性）。
    /// </summary>
    public class RuleValidationTests
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
                Preconditions = preconditions ?? new WorldCondition[0];
                Effects = effects ?? new WorldEffect[0];
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

        // ── 构造帮助 ─────────────────────────────────────────────

        private const string AgentN1 = "{\"id\":\"n1\",\"localKeys\":[],\"goalIds\":[\"g\"]}";

        private static SimulationConfig MakeConfig(string passiveJson, string agentsJson) =>
            ContentLoader.LoadSimulation(
                "{\"version\":1,\"ticksPerGameHour\":1,\"passiveEffects\":" + passiveJson +
                ",\"agents\":[" + agentsJson + "]}");

        private static WorldState World(IDictionary<string, double> values, int tick = 0, int day = 1) =>
            new WorldState(tick, day, values ?? new Dictionary<string, double>());

        // ── 提案加载器（镜像 rule-proposal.schema.json v1）────────

        [Test]
        public void LoadRuleProposal_ParsesAllChannels_InJsonOrder()
        {
            var proposal = ContentLoader.LoadRuleProposal(
                @"{""version"":1,
                    ""actionCostMultipliers"":{""gather_berries"":1.5,""chop_wood"":2},
                    ""productionMultipliers"":{""food"":0.5},
                    ""consumptionMultipliers"":{""food"":2,""wood"":1.25}}");

            Assert.AreEqual(2, proposal.ActionCostMultipliers.Count);
            Assert.AreEqual(1.5f, proposal.ActionCostMultipliers["gather_berries"]);
            Assert.AreEqual(2f, proposal.ActionCostMultipliers["chop_wood"]);
            Assert.AreEqual("gather_berries", proposal.ActionCostMultipliers.Keys.First(), "顺序与 JSON 一致（首错定位确定性）");
            Assert.AreEqual(1, proposal.ProductionMultipliers.Count);
            Assert.AreEqual(0.5f, proposal.ProductionMultipliers["food"]);
            Assert.AreEqual(2, proposal.ConsumptionMultipliers.Count);
            Assert.AreEqual(1.25f, proposal.ConsumptionMultipliers["wood"]);
        }

        [Test]
        public void LoadRuleProposal_VersionOnly_AllChannelsEmpty()
        {
            var proposal = ContentLoader.LoadRuleProposal(@"{""version"":1}");
            Assert.AreEqual(0, proposal.ActionCostMultipliers.Count, "通道缺省 = 空 = 无修正（合法的空提案）");
            Assert.AreEqual(0, proposal.ProductionMultipliers.Count);
            Assert.AreEqual(0, proposal.ConsumptionMultipliers.Count);
        }

        [Test]
        public void LoadRuleProposal_UnknownField_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadRuleProposal(@"{""version"":1,""severity"":3}"));
            StringAssert.Contains("未知字段 'severity'", ex.Message);
        }

        [Test]
        public void LoadRuleProposal_WrongVersion_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadRuleProposal(@"{""version"":2}"));
            StringAssert.Contains("不支持的契约版本", ex.Message);
        }

        [Test]
        public void LoadRuleProposal_NegativeMultiplier_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadRuleProposal(
                    @"{""version"":1,""actionCostMultipliers"":{""gather_berries"":-1}}"));
            StringAssert.Contains("不能为负", ex.Message);
        }

        [Test]
        public void LoadRuleProposal_BadEntryValues_Throw()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadRuleProposal(
                    @"{""version"":1,""actionCostMultipliers"":{""gather_berries"":""1.5""}}"));
            StringAssert.Contains("必须是数值", ex.Message);

            ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadRuleProposal(
                    @"{""version"":1,""productionMultipliers"":{"""":1}}"));
            StringAssert.Contains("键不能为空字符串", ex.Message);
        }

        [Test]
        public void LoadRuleProposal_ChannelNotObject_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadRuleProposal(@"{""version"":1,""actionCostMultipliers"":[]}"));
            StringAssert.Contains("必须是对象", ex.Message);
        }

        [Test]
        public void LoadRuleProposal_NullInput_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ContentLoader.LoadRuleProposal(null));
        }

        // ── RuleSet / MutableRuleProvider（应用与回退通道）────────

        [Test]
        public void RuleSet_NullChannels_NormalizedToEmpty_DefaultIsAllEmpty()
        {
            var rs = new RuleSet(null, null, null);
            Assert.IsNotNull(rs.ActionCostMultipliers);
            Assert.IsNotNull(rs.ProductionMultipliers);
            Assert.IsNotNull(rs.ConsumptionMultipliers);
            Assert.AreEqual(0, rs.ActionCostMultipliers.Count);

            Assert.AreEqual(0, RuleSet.Default.ActionCostMultipliers.Count, "内置默认 = 全空通道（全 1）");
            Assert.AreEqual(0, RuleSet.Default.ProductionMultipliers.Count);
            Assert.AreEqual(0, RuleSet.Default.ConsumptionMultipliers.Count);
        }

        [Test]
        public void MutableRuleProvider_Swap_UpdatesCurrentAndRaisesChanged()
        {
            var provider = new MutableRuleProvider();
            Assert.AreSame(RuleSet.Default, provider.Current, "缺省构造 = 内置默认规则集");

            IRuleSet received = null;
            int raised = 0;
            provider.RulesChanged += r => { received = r; raised++; };

            var next = new RuleSet(new Dictionary<string, float> { ["gather"] = 2f }, null, null);
            provider.Swap(next);
            Assert.AreSame(next, provider.Current);
            Assert.AreSame(next, received);
            Assert.AreEqual(1, raised);

            Assert.Throws<ArgumentNullException>(() => provider.Swap(null),
                "null 拒绝——回退请显式传 RuleSet.Default");
        }

        // ── RuleValidator（三段管线）─────────────────────────────

        /// <summary>良性配方：gather(+1 food) 服务 g(food≥3)，种子 food=2——提案通过后沙盒应跑出确定性轨迹。</summary>
        private static readonly IReadOnlyList<IAction> GatherActions =
            new[] { new StubAction("gather", 4f, null, new[] { WorldEffect.Gain("food", 1) }) };

        private static readonly IReadOnlyList<IGoal> FoodGoals =
            new[] { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 3) }) };

        [Test]
        public void Validate_GoodProposal_Accepted_MultipliersApplied_SeedUntouched()
        {
            var seed = World(new Dictionary<string, double> { ["food"] = 2 });
            var verdict = RuleValidator.Validate(
                @"{""version"":1,""actionCostMultipliers"":{""gather"":1.5},""productionMultipliers"":{""food"":0.5}}",
                seed, GatherActions, FoodGoals, MakeConfig("[]", AgentN1), new SandboxOptions(5));

            Assert.IsTrue(verdict.Accepted);
            Assert.AreEqual(RuleRejectReason.None, verdict.Reason);
            Assert.AreEqual(0, verdict.Tick);
            // 乘数确实在沙盒生效：成本 4×1.5=6 进重规划事件；产出 2 + 0.5 + 0.5 = 3（名义应为 4）
            Assert.AreEqual(6f, verdict.SandboxEvents[0].PlanCost, "actionCostMultipliers 作用于规划代价");
            Assert.AreEqual(3d, verdict.SandboxFinalWorld.Get("food"), "productionMultipliers 作用于结算");
            Assert.AreEqual(7, verdict.SandboxEvents.Count, "t1/t2 重规划+执行、t3–t5 闲置");
            Assert.AreEqual(2d, seed.Get("food"), "种子世界不被沙盒触碰（内核构造即克隆）");
        }

        [Test]
        public void Validate_SchemaInvalid_RejectedBeforeSandbox()
        {
            var verdict = RuleValidator.Validate(
                @"{""version"":1,""severity"":3}",
                World(new Dictionary<string, double> { ["food"] = 2 }), GatherActions, FoodGoals,
                MakeConfig("[]", AgentN1), new SandboxOptions(5));

            Assert.IsFalse(verdict.Accepted);
            Assert.AreEqual(RuleRejectReason.SchemaInvalid, verdict.Reason);
            StringAssert.Contains("未知字段", verdict.Detail);
            Assert.AreEqual(0, verdict.Tick);
            Assert.AreEqual(0, verdict.SandboxEvents.Count, "沙盒未运行");
            Assert.IsNull(verdict.SandboxFinalWorld);
        }

        [Test]
        public void Validate_UnknownActionKey_Rejected()
        {
            var verdict = RuleValidator.Validate(
                @"{""version"":1,""actionCostMultipliers"":{""gather"":1,""fly"":2}}",
                World(new Dictionary<string, double> { ["food"] = 2 }), GatherActions, FoodGoals,
                MakeConfig("[]", AgentN1), new SandboxOptions(5));

            Assert.AreEqual(RuleRejectReason.UnknownKey, verdict.Reason);
            Assert.AreEqual("fly", verdict.Subject, "幻觉键整体拒绝，绝不静默无操作");
            StringAssert.Contains("未知行动", verdict.Detail);
        }

        [Test]
        public void Validate_UnknownResourceKey_Rejected()
        {
            var verdict = RuleValidator.Validate(
                @"{""version"":1,""productionMultipliers"":{""gold"":0.5}}",
                World(new Dictionary<string, double> { ["food"] = 2 }), GatherActions, FoodGoals,
                MakeConfig("[]", AgentN1), new SandboxOptions(5));

            Assert.AreEqual(RuleRejectReason.UnknownKey, verdict.Reason);
            Assert.AreEqual("gold", verdict.Subject);
            StringAssert.Contains("未知世界状态键", verdict.Detail);
        }

        [Test]
        public void Validate_DegenerateSeed_Unplannable_Rejected_WithTickAndAgent()
        {
            // 倍率通道不改变可达性（规划用名义效果、对倍率盲——[gather,eat] 名义可达就永远有计划），
            // 不可规划是种子场景的属性。本守卫防御的是退化场景上的提案上线：
            // 目标 gold≥1 而行动库为空 → t=1 即不可规划，任何提案都拒绝
            var goals = new[] { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("gold", 1) }) };
            var verdict = RuleValidator.Validate(
                @"{""version"":1,""productionMultipliers"":{""food"":0.5}}",
                World(new Dictionary<string, double> { ["food"] = 2 }),
                new IAction[0], goals, MakeConfig("[]", AgentN1), new SandboxOptions(5));

            Assert.AreEqual(RuleRejectReason.SandboxUnplannable, verdict.Reason);
            Assert.AreEqual(1, verdict.Tick);
            Assert.AreEqual("n1", verdict.Subject);
            Assert.Greater(verdict.SandboxEvents.Count, 0, "沙盒留痕照常携带（失败方式是实验数据）");
        }

        [Test]
        public void Validate_InvariantViolated_Rejected_WithTickAndKey()
        {
            // 被动 food 流失、gather 名义 +1 补回（净 0，不变式 food≥1 恒成立）；
            // 提案产出 ×0 后净 −1/tick → t=2 末 food=0 破坏不变式
            var actions = new[] { new StubAction("gather", 4f, null, new[] { WorldEffect.Gain("food", 1) }) };
            var goals = new[] { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 5) }) };
            var seed = World(new Dictionary<string, double> { ["food"] = 2 });
            var options = new SandboxOptions(10,
                new[] { WorldCondition.AtLeast("food", 1) });

            var verdict = RuleValidator.Validate(
                @"{""version"":1,""productionMultipliers"":{""food"":0}}",
                seed, actions, goals, MakeConfig("[{\"key\":\"food\",\"amount\":-1}]", AgentN1), options);

            Assert.AreEqual(RuleRejectReason.InvariantViolated, verdict.Reason);
            Assert.AreEqual(2, verdict.Tick, "t1 末 food=1 尚可、t2 末 food=0 破坏");
            Assert.AreEqual("food", verdict.Subject);
            Assert.AreEqual(0d, verdict.SandboxFinalWorld.Get("food"));
        }

        [Test]
        public void Validate_SameInput_ProducesIdenticalVerdictAndTrace()
        {
            var actions = new[] { new StubAction("gather", 4f, null, new[] { WorldEffect.Gain("food", 1) }) };
            var goals = new[] { new StubGoal("g", 1f, new[] { WorldCondition.AtLeast("food", 5) }) };
            var options = new SandboxOptions(10, new[] { WorldCondition.AtLeast("food", 1) });
            const string json = @"{""version"":1,""productionMultipliers"":{""food"":0}}";

            var a = RuleValidator.Validate(json, World(new Dictionary<string, double> { ["food"] = 2 }),
                actions, goals, MakeConfig("[{\"key\":\"food\",\"amount\":-1}]", AgentN1), options);
            var b = RuleValidator.Validate(json, World(new Dictionary<string, double> { ["food"] = 2 }),
                actions, goals, MakeConfig("[{\"key\":\"food\",\"amount\":-1}]", AgentN1), options);

            Assert.AreEqual(a.Reason, b.Reason);
            Assert.AreEqual(a.Tick, b.Tick);
            Assert.AreEqual(a.Subject, b.Subject);
            Assert.IsTrue(a.SandboxEvents.SequenceEqual(b.SandboxEvents), "同输入同沙盒事件流（可复现）");
            Assert.AreEqual(a.SandboxFinalWorld.Get("food"), b.SandboxFinalWorld.Get("food"));
        }

        [Test]
        public void Validate_NullArguments_Throw_AndSandboxOptionsValidatesTicks()
        {
            var seed = World(new Dictionary<string, double> { ["food"] = 2 });
            var cfg = MakeConfig("[]", AgentN1);
            var options = new SandboxOptions(1);

            Assert.Throws<ArgumentNullException>(
                () => RuleValidator.Validate((string)null, seed, GatherActions, FoodGoals, cfg, options));
            Assert.Throws<ArgumentNullException>(
                () => RuleValidator.Validate((RuleProposalSpec)null, seed, GatherActions, FoodGoals, cfg, options));
            Assert.Throws<ArgumentNullException>(
                () => RuleValidator.Validate(@"{""version"":1}", null, GatherActions, FoodGoals, cfg, options));
            Assert.Throws<ArgumentNullException>(
                () => RuleValidator.Validate(@"{""version"":1}", seed, GatherActions, FoodGoals, cfg, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SandboxOptions(0));
        }
    }
}
