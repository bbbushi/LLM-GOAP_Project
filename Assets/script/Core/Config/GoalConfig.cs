using System.Collections.Generic;
using Vibe.Core.Contracts;

namespace Vibe.Core.Config
{
    /// <summary>
    /// JSON 配置加载出的 GOAP 目标（<see cref="IGoal"/> 的默认实现，纯数据）。
    /// 仅经 <see cref="ContentLoader"/> 构造——校验（非空 id、条件非空、算子映射、库内 id 唯一）
    /// 是加载流程的一部分。Schema 见 Docs/schemas/goal.schema.json（v1）。
    /// </summary>
    public sealed class GoalConfig : IGoal
    {
        public string Id { get; }
        public float Priority { get; }
        public IReadOnlyList<WorldCondition> Conditions { get; }

        internal GoalConfig(string id, float priority, IReadOnlyList<WorldCondition> conditions)
        {
            Id = id;
            Priority = priority;
            Conditions = conditions;
        }

        public override string ToString() =>
            $"Goal('{Id}', priority={Priority.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
    }
}
