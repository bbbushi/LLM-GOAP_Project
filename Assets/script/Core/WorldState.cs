using System;
using System.Collections.Generic;
using System.Text;
using Vibe.Core.Contracts;

namespace Vibe.Core
{
    /// <summary>
    /// <see cref="IWorldState"/> 的默认实现：平面「键 → double」字典存储。
    /// 契约语义见接口注释与 Docs/schemas/world-state.schema.json（v1）：
    /// 缺失键视为 0；键命名空间：资源裸键、NPC 用 npc.&lt;id&gt;.&lt;key&gt; 前缀。
    /// 非线程安全——模拟内核单线程推进，规划器克隆亦在同一线程进行。
    /// </summary>
    public sealed class WorldState : IWorldState
    {
        private readonly Dictionary<string, double> _values;

        public int Tick { get; }
        public int Day { get; }

        /// <summary>创建空状态（缺失键视为 0，无需显式初始化资源）。</summary>
        public WorldState(int tick = 0, int day = 1)
            : this(tick, day, new Dictionary<string, double>())
        {
        }

        /// <summary>以给定键值创建（内部持有传入字典的拷贝，外部改动不影响本状态）。</summary>
        public WorldState(int tick, int day, IDictionary<string, double> values)
        {
            Tick = tick;
            Day = day;
            _values = new Dictionary<string, double>(values);
        }

        public IEnumerable<string> Keys => _values.Keys;

        public double Get(string key) =>
            _values.TryGetValue(key, out var v) ? v : 0d;

        public bool TryGet(string key, out double value) =>
            _values.TryGetValue(key, out value);

        public void Set(string key, double value) => _values[key] = value;

        public bool Meets(IReadOnlyList<WorldCondition> conditions)
        {
            if (conditions == null) throw new ArgumentNullException(nameof(conditions));
            foreach (var c in conditions)
                if (!MeetsOne(c)) return false;
            return true;
        }

        private bool MeetsOne(in WorldCondition c)
        {
            double v = Get(c.Key);
            switch (c.Op)
            {
                case ConditionOp.GreaterOrEqual: return v >= c.Threshold;
                case ConditionOp.Greater:        return v >  c.Threshold;
                case ConditionOp.Equal:          return v == c.Threshold;
                case ConditionOp.Less:           return v <  c.Threshold;
                case ConditionOp.LessOrEqual:    return v <= c.Threshold;
                case ConditionOp.NotEqual:       return v != c.Threshold;
                default: return false; // 未知算子按不满足处理（防御未来枚举扩展）
            }
        }

        public void Apply(IReadOnlyList<WorldEffect> effects)
        {
            if (effects == null) throw new ArgumentNullException(nameof(effects));
            foreach (var e in effects)
                ApplyOne(e);
        }

        private void ApplyOne(in WorldEffect e)
        {
            switch (e.Op)
            {
                case EffectOp.Add:
                    _values[e.Key] = Get(e.Key) + e.Amount;
                    break;

                case EffectOp.Set:
                    _values[e.Key] = e.Amount;
                    break;

                case EffectOp.MultiplyBy:
                    // 缺失键视为 0：保持缺失（读取即 0），不凭空造键
                    if (_values.TryGetValue(e.Key, out var v))
                        _values[e.Key] = v * e.Amount;
                    break;
            }
        }

        public IWorldState Clone() => new WorldState(Tick, Day, _values);

        public string GetFingerprint()
        {
            // 键排序保证与写入顺序无关；R 格式保证 double 无损往返，签名稳定可比
            var keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (var k in keys)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(k).Append('=')
                  .Append(_values[k].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
