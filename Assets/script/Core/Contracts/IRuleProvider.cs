using System;
using System.Collections.Generic;

namespace Vibe.Core.Contracts
{
    /// <summary>
    /// 一份生效中的规则集：LLM 层对 GOAP 层的全部影响都收敛为三类预定义倍率通道
    /// （DESIGN.md §3.2 铁律 2——外部效果只经通道生效，不直接改状态）。
    /// 规则集由规则验证层校验通过后注入；校验失败则回退内置默认规则集。
    /// 各字典中缺省的键一律视为 1（无修正）。
    /// </summary>
    public interface IRuleSet
    {
        /// <summary>行动成本乘数：行动 id → 乘数。
        /// 例：暴雨日「收集浆果」×2 → 浆果链变贵 → NPC 计划自动翻转为买面包（GOAP.md §5）。</summary>
        IReadOnlyDictionary<string, float> ActionCostMultipliers { get; }

        /// <summary>资源生产倍率：资源键 → 乘率（结算时作用于产出类行动效果）。</summary>
        IReadOnlyDictionary<string, float> ProductionMultipliers { get; }

        /// <summary>资源消耗倍率：资源键 → 乘率（结算时作用于消耗）。</summary>
        IReadOnlyDictionary<string, float> ConsumptionMultipliers { get; }
    }

    /// <summary>
    /// 规则提供者：向 GOAP 层供应「当前生效的规则集」。
    /// 规则被替换（如新游戏日的天气脚本生效、玩家指令翻译出的调整过审）时，
    /// 通过 RulesChanged 通知，触发 NPC 重规划。
    /// </summary>
    public interface IRuleProvider
    {
        /// <summary>当前生效的规则集（永不为 null；无修正时为全空字典的默认集）。</summary>
        IRuleSet Current { get; }

        /// <summary>当前规则集被整体替换时触发，参数为新规则集。</summary>
        event Action<IRuleSet> RulesChanged;
    }
}
