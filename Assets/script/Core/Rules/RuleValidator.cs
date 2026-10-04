using System;
using System.Collections.Generic;
using Vibe.Core.Config;
using Vibe.Core.Config.Json;
using Vibe.Core.Contracts;

namespace Vibe.Core.Rules
{
    /// <summary>规则拒绝原因（机器可读，进留痕与研究点①的通过率/回退率统计）。</summary>
    public enum RuleRejectReason
    {
        /// <summary>通过（非拒绝的占位值）。</summary>
        None,

        /// <summary>结构校验失败：JSON 语法错误或加载器镜像拒绝（未知字段、版本不符、负乘数、非数值等）。</summary>
        SchemaInvalid,

        /// <summary>乘数引用了不存在的行动 id 或世界状态键（幻觉键）——整体拒绝，绝不静默无操作。</summary>
        UnknownKey,

        /// <summary>沙盒内出现不可规划的 NPC tick（<see cref="SandboxOptions.RequireZeroUnplannable"/>）。</summary>
        SandboxUnplannable,

        /// <summary>沙盒内不变式被破坏（每 tick 末对定局求值）。</summary>
        InvariantViolated
    }

    /// <summary>
    /// 一次规则验证的裁决（DESIGN.md §4.3 规则验证层的输出）：通过 / 拒绝 + 机器可读原因
    /// + 沙盒留痕。拒绝时沙盒事件流与终态照常携带（可观测性优先——失败方式本身是研究点①的
    /// 实验数据）；结构校验/键校验阶段即拒绝时沙盒未运行，两者为空。
    /// </summary>
    public sealed class RuleVerdict
    {
        private static readonly IReadOnlyList<SimEvent> NoEvents = new SimEvent[0];

        /// <summary>是否通过（Reason == None）。</summary>
        public bool Accepted => Reason == RuleRejectReason.None;

        /// <summary>拒绝原因；通过时为 None。</summary>
        public RuleRejectReason Reason { get; }

        /// <summary>定位详情（字段路径 / 键名 / 渠道 / 实际值；通过时为空字符串）。</summary>
        public string Detail { get; }

        /// <summary>沙盒内首个违规 tick；沙盒未运行（SchemaInvalid / UnknownKey）或通过时为 0。</summary>
        public int Tick { get; }

        /// <summary>违规主体：SandboxUnplannable 为 agent id，InvariantViolated 为失败条件的键，
        /// UnknownKey 为幻觉键；其余 null。</summary>
        public string Subject { get; }

        /// <summary>沙盒全程事件流（事件契约 v1；沙盒未运行时为空列表）。</summary>
        public IReadOnlyList<SimEvent> SandboxEvents { get; }

        /// <summary>沙盒终态世界（拒绝时为违规瞬间的状态；沙盒未运行时为 null）。</summary>
        public IWorldState SandboxFinalWorld { get; }

        private RuleVerdict(RuleRejectReason reason, string detail, int tick, string subject,
            IReadOnlyList<SimEvent> sandboxEvents, IWorldState sandboxFinalWorld)
        {
            Reason = reason;
            Detail = detail ?? "";
            Tick = tick;
            Subject = subject;
            SandboxEvents = sandboxEvents ?? NoEvents;
            SandboxFinalWorld = sandboxFinalWorld;
        }

        /// <summary>裁决通过（携带沙盒留痕）。</summary>
        public static RuleVerdict Accept(IReadOnlyList<SimEvent> sandboxEvents, IWorldState sandboxFinalWorld)
            => new RuleVerdict(RuleRejectReason.None, "", 0, null, sandboxEvents, sandboxFinalWorld);

        /// <summary>裁决拒绝。</summary>
        public static RuleVerdict Reject(RuleRejectReason reason, string detail, int tick, string subject,
            IReadOnlyList<SimEvent> sandboxEvents, IWorldState sandboxFinalWorld)
            => new RuleVerdict(reason, detail, tick, subject, sandboxEvents, sandboxFinalWorld);
    }

    /// <summary>
    /// 沙盒裁决参数（DESIGN.md §4.3「沙盒模拟 N tick 验证可达性/资源守恒」的参数化）：
    /// N tick、逐 tick 末必须成立的不变式（六算子，语义同行动/目标条件——求值的是定局，
    /// 是提案质量的主要信号：倍率失衡最终表现为不变式破坏）、以及是否要求沙盒内零不可规划。
    /// 后者的语义边界：规划用名义效果、对倍率盲（名义可达则永远有计划），倍率提案**不会制造**
    /// 不可规划——它是种子场景的属性；此检查（v1 默认开启）防御的是在退化场景上上线提案。
    /// 严格度本身是研究点①的实验变量，后续松紧演进属兼容扩展。
    /// </summary>
    public sealed class SandboxOptions
    {
        /// <summary>沙盒运行的 tick 数（≥ 1）。</summary>
        public int Ticks { get; }

        /// <summary>逐 tick 末必须全部成立的不变式（空 = 不检查资源面）。</summary>
        public IReadOnlyList<WorldCondition> Invariants { get; }

        /// <summary>要求沙盒内零不可规划 tick（缺省 true）。</summary>
        public bool RequireZeroUnplannable { get; }

        public SandboxOptions(int ticks, IReadOnlyList<WorldCondition> invariants = null,
            bool requireZeroUnplannable = true)
        {
            if (ticks < 1)
                throw new ArgumentOutOfRangeException(nameof(ticks), "沙盒 tick 数至少为 1");
            Ticks = ticks;
            Invariants = invariants ?? new WorldCondition[0];
            RequireZeroUnplannable = requireZeroUnplannable;
        }
    }

    /// <summary>
    /// 规则验证层内核（DESIGN.md §4.2「规则验证层」/§4.3，最大风险点的对策）：LLM 输出的
    /// 规则提案在此经过「结构校验 → 幻觉键拒绝 → 沙盒模拟 N tick」三段管线，产出
    /// <see cref="RuleVerdict"/>；裁决通过后由持有者经 <see cref="MutableRuleProvider.Swap"/>
    /// 注入（或回退 <see cref="RuleSet.Default"/>）——本类型只裁决，不触碰在跑的模拟。
    ///
    /// 沙盒以种子世界 + 行动/目标库 + 运行配置在旁路重建一次模拟（内核构造即克隆，种子与
    /// 在跑模拟不受影响），规则提案作为唯一规则生效，逐 tick 推进并检查：任何 NPC 不可规划
    /// （默认）或不变式破坏即拒绝。无随机源——同输入同裁决（可复现）。纯同步、纯数据进数据出，
    /// 无 IO：LLM 调用与缓存在上游（ILLMProvider），沙盒不是 API 调用点。
    /// </summary>
    public static class RuleValidator
    {
        /// <summary>
        /// 验证一份规则提案的原始 JSON 文本（<see cref="Contracts.ILLMProvider"/> 的 RawContent
        /// 直接入口）：解析失败与结构违规都归为 SchemaInvalid 拒绝（携带加载器的定位信息）。
        /// </summary>
        public static RuleVerdict Validate(string proposalJson, IWorldState seedWorld,
            IReadOnlyList<IAction> actions, IReadOnlyList<IGoal> goals, SimulationConfig config,
            SandboxOptions options)
        {
            if (proposalJson == null) throw new ArgumentNullException(nameof(proposalJson));
            RuleProposalSpec proposal;
            try
            {
                proposal = ContentLoader.LoadRuleProposal(proposalJson);
            }
            catch (Exception e) when (e is ContentLoadException || e is JsonParseException)
            {
                return RuleVerdict.Reject(RuleRejectReason.SchemaInvalid, e.Message, 0, null, null, null);
            }
            return Validate(proposal, seedWorld, actions, goals, config, options);
        }

        /// <summary>验证一份已加载的规则提案（缓存重放/重验入口）。</summary>
        public static RuleVerdict Validate(RuleProposalSpec proposal, IWorldState seedWorld,
            IReadOnlyList<IAction> actions, IReadOnlyList<IGoal> goals, SimulationConfig config,
            SandboxOptions options)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            if (seedWorld == null) throw new ArgumentNullException(nameof(seedWorld));
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            if (goals == null) throw new ArgumentNullException(nameof(goals));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (options == null) throw new ArgumentNullException(nameof(options));

            // 1. 幻觉键拒绝：通道键必须存在于组合现场（行动库 id / 种子世界键空间）。
            //    拼写错误的行动 id 若放行会是静默无操作的规则，违背「绝不静默忽略」。
            var actionIds = new HashSet<string>();
            foreach (var action in actions)
                actionIds.Add(action.Id);
            var worldKeys = new HashSet<string>(seedWorld.Keys);

            foreach (var kv in proposal.ActionCostMultipliers)
                if (!actionIds.Contains(kv.Key))
                    return RuleVerdict.Reject(RuleRejectReason.UnknownKey,
                        $"actionCostMultipliers 引用未知行动 '{kv.Key}'（有效行动：行动库 id）", 0, kv.Key, null, null);
            foreach (var kv in proposal.ProductionMultipliers)
                if (!worldKeys.Contains(kv.Key))
                    return RuleVerdict.Reject(RuleRejectReason.UnknownKey,
                        $"productionMultipliers 引用未知世界状态键 '{kv.Key}'（键空间以种子快照为准）", 0, kv.Key, null, null);
            foreach (var kv in proposal.ConsumptionMultipliers)
                if (!worldKeys.Contains(kv.Key))
                    return RuleVerdict.Reject(RuleRejectReason.UnknownKey,
                        $"consumptionMultipliers 引用未知世界状态键 '{kv.Key}'（键空间以种子快照为准）", 0, kv.Key, null, null);

            // 2. 沙盒裁决：旁路重建模拟（构造即克隆种子），提案作为唯一规则逐 tick 检查
            var rules = new MutableRuleProvider(new RuleSet(proposal.ActionCostMultipliers,
                proposal.ProductionMultipliers, proposal.ConsumptionMultipliers));
            var sandbox = new Simulation(seedWorld, actions, goals, config, rules: rules);

            int seenEvents = 0;
            for (int t = 1; t <= options.Ticks; t++)
            {
                sandbox.Step();

                if (options.RequireZeroUnplannable)
                {
                    for (int i = seenEvents; i < sandbox.Events.Count; i++)
                    {
                        var e = sandbox.Events[i];
                        if (e.Idle == IdleReason.Unplannable)
                            return RuleVerdict.Reject(RuleRejectReason.SandboxUnplannable,
                                $"agent '{e.AgentId}' 在沙盒 t={e.Tick} 不可规划", e.Tick, e.AgentId,
                                sandbox.Events, sandbox.Current);
                    }
                }
                seenEvents = sandbox.Events.Count;

                if (options.Invariants.Count > 0 && !sandbox.Current.Meets(options.Invariants))
                {
                    foreach (var invariant in options.Invariants)
                    {
                        if (!sandbox.Current.Meets(new[] { invariant }))
                        {
                            return RuleVerdict.Reject(RuleRejectReason.InvariantViolated,
                                $"不变式 '{invariant.Key}' 在沙盒 t={t} 被破坏（实际 {sandbox.Current.Get(invariant.Key)}）",
                                t, invariant.Key, sandbox.Events, sandbox.Current);
                        }
                    }
                }
            }
            return RuleVerdict.Accept(sandbox.Events, sandbox.Current);
        }
    }
}
