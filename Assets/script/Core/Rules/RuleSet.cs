using System;
using System.Collections.Generic;
using Vibe.Core.Contracts;

namespace Vibe.Core.Rules
{
    /// <summary>
    /// 不可变规则集（<see cref="IRuleSet"/> 的内核实现）：三类倍率通道，各字典缺省的键视为 1
    /// （无修正）。<see cref="Default"/> 即内置默认规则集——全空通道（全 1），规则验证层
    /// 裁决失败时的回退目标（铁律 3：API 失败/校验失败一律回退内置默认）。
    /// </summary>
    public sealed class RuleSet : IRuleSet
    {
        /// <summary>内置默认规则集：全空通道（一切乘数为 1，无任何修正）。</summary>
        public static readonly RuleSet Default = new RuleSet(null, null, null);

        /// <summary>行动成本乘数：行动 id → 乘数（缺省 1；0 = 事实上禁用）。</summary>
        public IReadOnlyDictionary<string, float> ActionCostMultipliers { get; }

        /// <summary>资源生产倍率：资源键 → 乘率（结算时作用于产出类效果）。</summary>
        public IReadOnlyDictionary<string, float> ProductionMultipliers { get; }

        /// <summary>资源消耗倍率：资源键 → 乘率（结算时作用于消耗）。</summary>
        public IReadOnlyDictionary<string, float> ConsumptionMultipliers { get; }

        /// <summary>构造一份规则集；null 通道归一化为空字典（<see cref="IRuleSet"/> 契约：永不为 null）。</summary>
        public RuleSet(IReadOnlyDictionary<string, float> actionCost,
            IReadOnlyDictionary<string, float> production, IReadOnlyDictionary<string, float> consumption)
        {
            ActionCostMultipliers = actionCost ?? new Dictionary<string, float>();
            ProductionMultipliers = production ?? new Dictionary<string, float>();
            ConsumptionMultipliers = consumption ?? new Dictionary<string, float>();
        }
    }

    /// <summary>
    /// 可替换的运行时规则提供者（<see cref="IRuleProvider"/> 的内核实现）：规则验证层裁决通过后，
    /// 由持有者 Swap 注入（或回退 <see cref="RuleSet.Default"/>）；替换即触发 RulesChanged，
    /// 模拟内核在当前/下一个 tick 作废全部计划并按新规则重规划。非线程安全（模拟单线程推进，
    /// Swap 只应发生在 tick 间或日界挂载点回调内）。
    /// </summary>
    public sealed class MutableRuleProvider : IRuleProvider
    {
        private IRuleSet _current;

        /// <summary>当前生效的规则集（永不为 null）。</summary>
        public IRuleSet Current => _current;

        /// <summary>当前规则集被整体替换时触发，参数为新规则集。</summary>
        public event Action<IRuleSet> RulesChanged;

        /// <summary>以初始规则集构造（null 取 <see cref="RuleSet.Default"/>）。</summary>
        public MutableRuleProvider(IRuleSet initial = null)
        {
            _current = initial ?? RuleSet.Default;
        }

        /// <summary>整体替换规则集并通知（null 拒绝——回退请显式传 <see cref="RuleSet.Default"/>）。</summary>
        public void Swap(IRuleSet newRules)
        {
            if (newRules == null) throw new ArgumentNullException(nameof(newRules));
            _current = newRules;
            RulesChanged?.Invoke(newRules);
        }
    }
}
