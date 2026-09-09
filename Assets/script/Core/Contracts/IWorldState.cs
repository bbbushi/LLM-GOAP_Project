using System.Collections.Generic;

namespace Vibe.Core.Contracts
{
    /// <summary>
    /// 世界状态：对世界的量化描述，一个「键 → 数值」集合（食物份数、居民数、电量……）。
    ///
    /// 契约语义（与 Docs/schemas/world-state.schema.json v1 同步冻结，讲解见 Docs/GOAP.md §2.1）：
    ///  - 缺失的键视为 0；
    ///  - 只有 GOAP 引擎（及其授权的结算通道）可以修改状态；LLM 永远只产 JSON 配置，不触碰本接口；
    ///  - 键命名空间约定：共享资源用裸键（food / power / …），
    ///    NPC 个体状态用前缀 npc.&lt;id&gt;.&lt;key&gt;（如 npc.n1.hunger）。
    ///
    /// 规划器搜索时会大量 Clone 出假想状态，实现必须保证克隆体与原状态互不影响。
    /// </summary>
    public interface IWorldState
    {
        /// <summary>快照对应的模拟 tick（构造/快照时设定；效果不会改动它）。</summary>
        int Tick { get; }

        /// <summary>快照对应的游戏日（从 1 起）。</summary>
        int Day { get; }

        /// <summary>全部已写入的键（不含「缺失视为 0」的键）。</summary>
        IEnumerable<string> Keys { get; }

        /// <summary>读取键值；键缺失时返回 0（契约语义，不抛异常）。</summary>
        double Get(string key);

        /// <summary>尝试读取；键缺失时返回 false 且 value 为 0。</summary>
        bool TryGet(string key, out double value);

        /// <summary>写入键值（不存在则创建）。仅限模拟内核与效果通道调用。</summary>
        void Set(string key, double value);

        /// <summary>判断当前状态是否满足一组条件（逐条按算子比较，见 <see cref="WorldCondition"/>）。</summary>
        bool Meets(IReadOnlyList<WorldCondition> conditions);

        /// <summary>按效果算子批量修改状态（见 <see cref="WorldEffect"/>）。</summary>
        void Apply(IReadOnlyList<WorldEffect> effects);

        /// <summary>深拷贝：克隆体与原状态的后续修改互不影响。</summary>
        IWorldState Clone();

        /// <summary>
        /// 状态签名：按键排序的规范化序列化（形如 "food=3|money=10|…"，不含 tick/day）。
        /// 供规划器做 visited 集合去重——GOAP.md §4 教学版简化点 3 的正式解法。
        /// </summary>
        string GetFingerprint();
    }
}
