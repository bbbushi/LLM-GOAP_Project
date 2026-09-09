using System.Collections.Generic;

namespace Vibe.Core.Contracts
{
    /// <summary>
    /// GOAP 行动：一个原子行为的纯数据描述 = 前提条件 + 执行效果 + 代价。
    /// 行动由 JSON 配置加载（内容扩展靠数据不靠代码，DESIGN.md §4.5）；
    /// LLM 可以生成或调整行动配置，但行动本身不含任何逻辑代码。
    /// </summary>
    public interface IAction
    {
        /// <summary>稳定标识（JSON 配置的 id；规则层的成本乘数以此索引）。</summary>
        string Id { get; }

        /// <summary>基础代价。规划器的有效代价 = BaseCost × 规则乘数（见 <see cref="IRuleSet"/>）。</summary>
        float BaseCost { get; }

        /// <summary>可执行前提：全部满足时，该行动才是当前状态下图搜索的一条可行边。</summary>
        IReadOnlyList<WorldCondition> Preconditions { get; }

        /// <summary>执行效果：行动完成时由 GOAP 引擎应用到世界状态。</summary>
        IReadOnlyList<WorldEffect> Effects { get; }
    }
}
