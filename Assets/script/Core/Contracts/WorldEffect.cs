namespace Vibe.Core.Contracts
{
    /// <summary>效果算子。语义基线是「+=」（Docs/GOAP.md §2.1）。</summary>
    public enum EffectOp
    {
        /// <summary>key += amount（最常用：+1 食物、-5 钱币……缺失键视为 0 后再加）。</summary>
        Add,

        /// <summary>key = amount（设定绝对量）。</summary>
        Set,

        /// <summary>key *= amount（缺失键视为 0，乘后仍为 0；少用）。</summary>
        MultiplyBy
    }

    /// <summary>
    /// 一条执行效果：行动完成时由 GOAP 引擎按算子应用到世界状态。
    /// 不可变值类型；行动效果由 JSON 配置加载而来（纯数据，不含逻辑）。
    /// </summary>
    public readonly struct WorldEffect
    {
        public string Key { get; }
        public EffectOp Op { get; }

        /// <summary>Add 的增量 / Set 的目标值 / MultiplyBy 的乘数。</summary>
        public double Amount { get; }

        public WorldEffect(string key, EffectOp op, double amount)
        {
            Key = key;
            Op = op;
            Amount = amount;
        }

        /// <summary>惯用糖：增量效果「key += amount」（负数即消耗）。</summary>
        public static WorldEffect Gain(string key, double amount)
            => new WorldEffect(key, EffectOp.Add, amount);
    }
}
