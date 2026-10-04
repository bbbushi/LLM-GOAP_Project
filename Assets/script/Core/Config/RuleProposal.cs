using System.Collections.Generic;

namespace Vibe.Core.Config
{
    /// <summary>
    /// 一份规则提案（Docs/schemas/rule-proposal.schema.json v1，冻结契约）：LLM 输出的规则调整
    /// 在通过规则验证层（Vibe.Core.Rules.RuleValidator）之前的形态。三类倍率通道即
    /// <see cref="Contracts.IRuleSet"/> 的三个通道——LLM 对 GOAP 层的全部影响收敛于此
    /// （铁律 2，DESIGN.md §3.2）；通道缺省 = 空字典（无修正，全 1）。
    /// 乘数已在加载器保证 ≥ 0；键的存在性（行动 id / 世界状态键）由验证层在组合时校验
    /// （本类型与加载器只见单文件，不持有行动库与世界键空间）。
    /// </summary>
    public sealed class RuleProposalSpec
    {
        /// <summary>行动成本乘数：行动 id → 乘数（缺省 1；0 = 事实上禁用）。</summary>
        public IReadOnlyDictionary<string, float> ActionCostMultipliers { get; }

        /// <summary>资源生产倍率：资源键 → 乘率（结算时作用于产出类效果）。</summary>
        public IReadOnlyDictionary<string, float> ProductionMultipliers { get; }

        /// <summary>资源消耗倍率：资源键 → 乘率（结算时作用于消耗）。</summary>
        public IReadOnlyDictionary<string, float> ConsumptionMultipliers { get; }

        internal RuleProposalSpec(IReadOnlyDictionary<string, float> actionCost,
            IReadOnlyDictionary<string, float> production, IReadOnlyDictionary<string, float> consumption)
        {
            ActionCostMultipliers = actionCost;
            ProductionMultipliers = production;
            ConsumptionMultipliers = consumption;
        }
    }
}
