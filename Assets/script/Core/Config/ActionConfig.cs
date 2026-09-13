using System.Collections.Generic;
using Vibe.Core.Contracts;

namespace Vibe.Core.Config
{
    /// <summary>
    /// JSON 配置加载出的 GOAP 行动（<see cref="IAction"/> 的默认实现，纯数据）。
    /// 仅经 <see cref="ContentLoader"/> 构造——校验（非空 id、非负代价、算子映射、库内 id 唯一）
    /// 是加载流程的一部分，不存在绕过校验的构造路径。Schema 见 Docs/schemas/action.schema.json（v1）。
    /// </summary>
    public sealed class ActionConfig : IAction
    {
        public string Id { get; }
        public float BaseCost { get; }
        public IReadOnlyList<WorldCondition> Preconditions { get; }
        public IReadOnlyList<WorldEffect> Effects { get; }

        internal ActionConfig(string id, float baseCost,
            IReadOnlyList<WorldCondition> preconditions, IReadOnlyList<WorldEffect> effects)
        {
            Id = id;
            BaseCost = baseCost;
            Preconditions = preconditions;
            Effects = effects;
        }

        public override string ToString() =>
            $"Action('{Id}', cost={BaseCost.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
    }
}
