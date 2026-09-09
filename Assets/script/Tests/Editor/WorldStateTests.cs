using System.Collections.Generic;
using NUnit.Framework;
using Vibe.Core;
using Vibe.Core.Contracts;

namespace Vibe.Core.Tests
{
    /// <summary>
    /// WorldState 默认实现的契约测试。
    /// 逐条对应 IWorldState 冻结语义：缺失键视为 0、六种条件算子、三种效果算子、
    /// 克隆独立性、状态签名（GOAP.md §2.1 / §4 简化点 3 的正式解法）。
    /// </summary>
    public class WorldStateTests
    {
        // ── 读取语义 ──────────────────────────────────────────────

        [Test]
        public void Get_MissingKey_ReturnsZero()
        {
            var ws = new WorldState();
            Assert.AreEqual(0d, ws.Get("food"));
        }

        [Test]
        public void TryGet_MissingKey_ReturnsFalseAndZero()
        {
            var ws = new WorldState();
            Assert.IsFalse(ws.TryGet("food", out var v));
            Assert.AreEqual(0d, v);
        }

        [Test]
        public void Set_NewKey_Creates_ExistingKey_Overwrites()
        {
            var ws = new WorldState();
            ws.Set("food", 3);
            ws.Set("food", 7.5);
            Assert.IsTrue(ws.TryGet("food", out var v));
            Assert.AreEqual(7.5d, v);
        }

        [Test]
        public void Keys_ReturnsOnlyWrittenKeys()
        {
            var ws = new WorldState();
            ws.Set("food", 1);
            ws.Set("power", 2);
            // 键序无契约保证，排序后比较
            var keys = new List<string>(ws.Keys);
            keys.Sort();
            Assert.AreEqual(new[] { "food", "power" }, keys);
            // 读取过的缺失键不应出现在 Keys 中
            ws.Get("wood");
            keys = new List<string>(ws.Keys);
            keys.Sort();
            Assert.AreEqual(new[] { "food", "power" }, keys);
        }

        [Test]
        public void Constructor_CopiesSourceDictionary()
        {
            var source = new Dictionary<string, double> { ["food"] = 5 };
            var ws = new WorldState(0, 1, source);
            source["food"] = 99; // 外部改动不影响状态
            Assert.AreEqual(5d, ws.Get("food"));
        }

        // ── 条件判定 Meets ────────────────────────────────────────

        [Test]
        public void Meets_EmptyConditions_True()
        {
            Assert.IsTrue(new WorldState().Meets(new List<WorldCondition>()));
        }

        [Test]
        public void Meets_NullConditions_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => new WorldState().Meets(null));
        }

        [TestCase(3.0, ConditionOp.GreaterOrEqual, 3.0, true)]
        [TestCase(2.9, ConditionOp.GreaterOrEqual, 3.0, false)]
        [TestCase(3.0, ConditionOp.Greater, 3.0, false)]
        [TestCase(3.1, ConditionOp.Greater, 3.0, true)]
        [TestCase(1.0, ConditionOp.Equal, 1.0, true)]
        [TestCase(0.0, ConditionOp.Equal, 1.0, false)]
        [TestCase(0.5, ConditionOp.Less, 1.0, true)]
        [TestCase(1.0, ConditionOp.Less, 1.0, false)]
        [TestCase(1.0, ConditionOp.LessOrEqual, 1.0, true)]
        [TestCase(1.1, ConditionOp.LessOrEqual, 1.0, false)]
        [TestCase(2.0, ConditionOp.NotEqual, 1.0, true)]
        [TestCase(1.0, ConditionOp.NotEqual, 1.0, false)]
        public void Meets_EachOperator(double value, ConditionOp op, double threshold, bool expected)
        {
            var ws = new WorldState();
            ws.Set("k", value);
            Assert.AreEqual(expected, ws.Meets(new[] { new WorldCondition("k", op, threshold) }));
        }

        [Test]
        public void Meets_MissingKey_TreatedAsZero()
        {
            var ws = new WorldState(); // "wood" 未写入 → 视为 0
            Assert.IsTrue(ws.Meets(new[] { WorldCondition.AtLeast("wood", 0) }));
            Assert.IsFalse(ws.Meets(new[] { WorldCondition.AtLeast("wood", 1) }));
            Assert.IsTrue(ws.Meets(new[] { new WorldCondition("wood", ConditionOp.Less, 1) }));
        }

        [Test]
        public void Meets_AllConditionsMustHold()
        {
            var ws = new WorldState();
            ws.Set("food", 5);
            ws.Set("money", 0);
            var conditions = new[]
            {
                WorldCondition.AtLeast("food", 3),   // 满足
                WorldCondition.AtLeast("money", 1),  // 不满足
            };
            Assert.IsFalse(ws.Meets(conditions));
        }

        // ── 效果应用 Apply ────────────────────────────────────────

        [Test]
        public void Apply_Add_MissingKeyStartsFromZero_ThenAccumulates()
        {
            var ws = new WorldState();
            ws.Apply(new[] { WorldEffect.Gain("food", 2) });   // 缺失=0 → 2
            ws.Apply(new[] { WorldEffect.Gain("food", 3) });   // 2+3 → 5
            Assert.AreEqual(5d, ws.Get("food"));
        }

        [Test]
        public void Apply_Add_NegativeAmount_Consumes()
        {
            var ws = new WorldState(0, 1, new Dictionary<string, double> { ["food"] = 5 });
            ws.Apply(new[] { WorldEffect.Gain("food", -1) });
            Assert.AreEqual(4d, ws.Get("food"));
        }

        [Test]
        public void Apply_Set_OverwritesAbsolute()
        {
            var ws = new WorldState(0, 1, new Dictionary<string, double> { ["food"] = 9 });
            ws.Apply(new[] { new WorldEffect("food", EffectOp.Set, 1) });
            Assert.AreEqual(1d, ws.Get("food"));
        }

        [Test]
        public void Apply_MultiplyBy_MissingKey_StaysMissing()
        {
            var ws = new WorldState();
            ws.Apply(new[] { new WorldEffect("wood", EffectOp.MultiplyBy, 2) });
            Assert.IsFalse(ws.TryGet("wood", out _)); // 不凭空造键
            Assert.AreEqual(0d, ws.Get("wood"));
        }

        [Test]
        public void Apply_MultiplyBy_ExistingKey_Multiplies()
        {
            var ws = new WorldState(0, 1, new Dictionary<string, double> { ["food"] = 6 });
            ws.Apply(new[] { new WorldEffect("food", EffectOp.MultiplyBy, 0.5) });
            Assert.AreEqual(3d, ws.Get("food"));
        }

        [Test]
        public void Apply_MixedEffects_AppliedInOrder()
        {
            var ws = new WorldState();
            ws.Apply(new[]
            {
                WorldEffect.Gain("food", 4),
                new WorldEffect("food", EffectOp.MultiplyBy, 0.5),
            });
            Assert.AreEqual(2d, ws.Get("food")); // (0+4)*0.5
        }

        // ── 克隆与快照语义 ────────────────────────────────────────

        [Test]
        public void Clone_IsIndependent_BothDirections()
        {
            var original = new WorldState(10, 2);
            original.Set("food", 5);
            var clone = original.Clone();

            clone.Set("food", 99);
            Assert.AreEqual(5d, original.Get("food")); // 改克隆不影响本体

            original.Set("power", 1);
            Assert.AreEqual(0d, clone.Get("power"));   // 改本体不影响克隆
        }

        [Test]
        public void Clone_PreservesTickAndDay()
        {
            var original = new WorldState(42, 3);
            var clone = original.Clone();
            Assert.AreEqual(42, clone.Tick);
            Assert.AreEqual(3, clone.Day);
        }

        // ── 状态签名 Fingerprint ──────────────────────────────────

        [Test]
        public void Fingerprint_IgnoresKeyInsertionOrder()
        {
            var a = new WorldState();
            a.Set("food", 3);
            a.Set("money", 10);

            var b = new WorldState();
            b.Set("money", 10);
            b.Set("food", 3);

            Assert.AreEqual(a.GetFingerprint(), b.GetFingerprint());
        }

        [Test]
        public void Fingerprint_DifferentValues_ProduceDifferentSignatures()
        {
            var a = new WorldState();
            a.Set("food", 3);
            var b = new WorldState();
            b.Set("food", 4);
            Assert.AreNotEqual(a.GetFingerprint(), b.GetFingerprint());
        }

        [Test]
        public void Fingerprint_IgnoresTickAndDay()
        {
            var a = new WorldState(0, 1);
            a.Set("food", 3);
            var b = new WorldState(999, 7);
            b.Set("food", 3);
            Assert.AreEqual(a.GetFingerprint(), b.GetFingerprint());
        }
    }
}
