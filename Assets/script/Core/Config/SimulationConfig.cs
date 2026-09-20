using System.Collections.Generic;
using Vibe.Core.Contracts;

namespace Vibe.Core.Config
{
    /// <summary>
    /// 一个 NPC 的模拟声明（<see cref="SimulationConfig"/> 的 agents 条目，纯数据）。
    /// 仅经 <see cref="ContentLoader"/> 构造——校验（非空 id、localKeys 不带点分前缀、
    /// goalIds 至少一条、库内 id 唯一）是加载流程的一部分。Schema 见 Docs/schemas/simulation.schema.json（v1）。
    /// </summary>
    public sealed class AgentSpec
    {
        /// <summary>NPC 标识；世界状态键命名空间 npc.&lt;id&gt;.&lt;key&gt; 中的 id。</summary>
        public string Id { get; }

        /// <summary>个体键清单（裸键名）。驱动键投影：读时 npc.id.key → key，写回时 key ∈ 此清单 → npc.id.key。</summary>
        public IReadOnlyList<string> LocalKeys { get; }

        /// <summary>该 NPC 追求的目标 id（引用目标库；存在性由内核组合时校验）。</summary>
        public IReadOnlyList<string> GoalIds { get; }

        internal AgentSpec(string id, IReadOnlyList<string> localKeys, IReadOnlyList<string> goalIds)
        {
            Id = id;
            LocalKeys = localKeys;
            GoalIds = goalIds;
        }
    }

    /// <summary>
    /// 模拟运行配置（<see cref="ContentLoader.LoadSimulation"/> 的产物，纯数据）：
    /// tick 与游戏时间的换算、每 tick 被动结算效果、参与模拟的 NPC 声明。
    /// Schema 见 Docs/schemas/simulation.schema.json（v1，冻结契约）。
    /// </summary>
    public sealed class SimulationConfig
    {
        /// <summary>每游戏小时的 tick 数（游戏日 = 24 × 此值）。</summary>
        public int TicksPerGameHour { get; }

        /// <summary>每 tick 开头直接应用到世界状态的效果（完整键名，不经倍率通道）。</summary>
        public IReadOnlyList<WorldEffect> PassiveEffects { get; }

        /// <summary>参与模拟的 NPC 声明（M1 为单 NPC，多 NPC 结构自此就绪）。</summary>
        public IReadOnlyList<AgentSpec> Agents { get; }

        internal SimulationConfig(int ticksPerGameHour,
            IReadOnlyList<WorldEffect> passiveEffects, IReadOnlyList<AgentSpec> agents)
        {
            TicksPerGameHour = ticksPerGameHour;
            PassiveEffects = passiveEffects;
            Agents = agents;
        }
    }
}
