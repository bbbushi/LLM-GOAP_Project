namespace Vibe.Core.Contracts
{
    /// <summary>条件比较算子。语义基线是「≥」（Docs/GOAP.md §2.1），其余算子按需使用。</summary>
    public enum ConditionOp
    {
        /// <summary>key ≥ threshold（最常用：有斧头、食物充足……）。</summary>
        GreaterOrEqual,

        /// <summary>key &gt; threshold。</summary>
        Greater,

        /// <summary>key == threshold。用于 0/1 旗标；避免对连续量做浮点直等比较。</summary>
        Equal,

        /// <summary>key &lt; threshold。</summary>
        Less,

        /// <summary>key ≤ threshold（可表达「尚不在某地」等互斥前提）。</summary>
        LessOrEqual,

        /// <summary>key != threshold。</summary>
        NotEqual
    }

    /// <summary>
    /// 一条前提/目标条件：键值与阈值按算子比较，比较前缺失键取 0。
    /// 不可变值类型；行动与目标的条件均由 JSON 配置加载而来（纯数据，不含逻辑）。
    /// </summary>
    public readonly struct WorldCondition
    {
        public string Key { get; }
        public ConditionOp Op { get; }
        public double Threshold { get; }

        public WorldCondition(string key, ConditionOp op, double threshold)
        {
            Key = key;
            Op = op;
            Threshold = threshold;
        }

        /// <summary>惯用糖：「key 至少为 threshold」，即 GOAP.md 的 ≥ 基线语义。</summary>
        public static WorldCondition AtLeast(string key, double threshold)
            => new WorldCondition(key, ConditionOp.GreaterOrEqual, threshold);
    }
}
