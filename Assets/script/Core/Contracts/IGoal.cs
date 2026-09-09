using System.Collections.Generic;

namespace Vibe.Core.Contracts
{
    /// <summary>
    /// GOAP 目标：NPC 想要让世界满足的一组条件 + 优先级。
    /// 行动是世界「能发生」的事，目标是 NPC「想达成」的事，规划器负责连接两者。
    /// 同样由 JSON 配置加载，纯数据。
    /// </summary>
    public interface IGoal
    {
        /// <summary>稳定标识（JSON 配置的 id）。</summary>
        string Id { get; }

        /// <summary>优先级，越大越先尝试（多目标并存时按降序逐一规划）。</summary>
        float Priority { get; }

        /// <summary>目标条件：全部满足即视为该目标达成。</summary>
        IReadOnlyList<WorldCondition> Conditions { get; }
    }
}
