using NUnit.Framework;
using Vibe.Core.Config.Json;

namespace Vibe.Core.Tests
{
    /// <summary>
    /// 迷你 JSON 解析器的契约测试：JSON 全集（对象/数组/字符串/数值/布尔/null）、
    /// 转义与代理对、严格文法（前导零、尾随垃圾、重复键）、行列定位报错、嵌套深度上限。
    /// </summary>
    public class MiniJsonTests
    {
        // ── 标量 ──────────────────────────────────────────────────

        [Test]
        public void Parse_Numbers_IntFractionExponentNegative()
        {
            Assert.AreEqual(42d, JsonValue.Parse("42").AsNumber);
            Assert.AreEqual(-3.5d, JsonValue.Parse("-3.5").AsNumber);
            Assert.AreEqual(0.001d, JsonValue.Parse("1e-3").AsNumber);
            Assert.AreEqual(-250d, JsonValue.Parse("-2.5E+2").AsNumber);
            Assert.AreEqual(0d, JsonValue.Parse("-0").AsNumber);
            Assert.AreEqual(0.5d, JsonValue.Parse("0.5").AsNumber);
        }

        [Test]
        public void Parse_Literals_TrueFalseNull()
        {
            Assert.AreEqual(JsonType.Boolean, JsonValue.Parse("true").Type);
            Assert.IsTrue(JsonValue.Parse("true").AsBoolean);
            Assert.IsFalse(JsonValue.Parse("false").AsBoolean);
            Assert.AreEqual(JsonType.Null, JsonValue.Parse("null").Type);
            Assert.AreSame(JsonValue.Null, JsonValue.Parse("null"));
        }

        [Test]
        public void Parse_String_Escapes()
        {
            Assert.AreEqual("", JsonValue.Parse(@"""""").AsString);
            Assert.AreEqual("a\"b\\c/d", JsonValue.Parse(@"""a\""b\\c/d""").AsString);
            Assert.AreEqual("a\nb\tc", JsonValue.Parse(@"""a\nb\tc""").AsString);
            Assert.AreEqual("\b\f\r", JsonValue.Parse(@"""\b\f\r""").AsString);
        }

        [Test]
        public void Parse_String_UnicodeEscape_And_SurrogatePair()
        {
            // 单个 \uXXXX
            Assert.AreEqual('木', JsonValue.Parse(@"""木""").AsString[0]);
            // 代理对：两个 \uXXXX 各贡献一个 UTF-16 码元，拼成一个增补平面字符
            var emoji = JsonValue.Parse("\"\\ud83d\\ude00\"").AsString;
            Assert.AreEqual(2, emoji.Length);
            Assert.AreEqual(0xD83D, (int)emoji[0]);
            Assert.AreEqual(0xDE00, (int)emoji[1]);
        }

        [Test]
        public void Parse_Whitespace_AroundAndInside_IsSkipped()
        {
            var v = JsonValue.Parse(" \t\r\n { \"a\" : [ 1 , 2 ] } \n ");
            Assert.AreEqual(JsonType.Object, v.Type);
            Assert.AreEqual(2, v.Members["a"].Items.Count);
        }

        // ── 结构访问 ──────────────────────────────────────────────

        [Test]
        public void Parse_Nested_StructureAndOrderPreserved()
        {
            var v = JsonValue.Parse(@"{""goals"":[{""id"":""a""},{""id"":""b""}],""meta"":{""day"":1}}");
            Assert.AreEqual(2, v.Members["goals"].Items.Count);
            Assert.AreEqual("a", v.Members["goals"].Items[0].Members["id"].AsString);
            Assert.AreEqual("b", v.Members["goals"].Items[1].Members["id"].AsString);
            Assert.AreEqual(1d, v.Members["meta"].Members["day"].AsNumber);
        }

        [Test]
        public void TryGetMember_AbsentKeyOrNonObject_ReturnsFalse()
        {
            var v = JsonValue.Parse(@"{""a"":1}");
            Assert.IsTrue(v.TryGetMember("a", out var a));
            Assert.AreEqual(1d, a.AsNumber);
            Assert.IsFalse(v.TryGetMember("b", out var missing));
            Assert.IsNull(missing);
            Assert.IsFalse(JsonValue.Parse("[1]").TryGetMember("a", out _));
            Assert.IsFalse(JsonValue.Parse("null").TryGetMember("a", out _));
        }

        [Test]
        public void TryGetMember_ExplicitNullValue_ReturnsTrueWithNullValue()
        {
            var v = JsonValue.Parse(@"{""a"":null}");
            Assert.IsTrue(v.TryGetMember("a", out var a));
            Assert.AreEqual(JsonType.Null, a.Type);
        }

        [Test]
        public void TypedAccessors_WrongType_Throw()
        {
            Assert.Throws<System.InvalidOperationException>(() => { var _ = JsonValue.Parse("\"s\"").AsNumber; });
            Assert.Throws<System.InvalidOperationException>(() => { var _ = JsonValue.Parse("1").AsString; });
            Assert.Throws<System.InvalidOperationException>(() => { var _ = JsonValue.Parse("true").Items.Count; });
            Assert.Throws<System.InvalidOperationException>(() => { var _ = JsonValue.Parse("[]").AsBoolean; });
        }

        // ── 严格文法：必须拒绝 ────────────────────────────────────

        [Test]
        public void Parse_TrailingGarbage_Throws()
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("{} {}"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("[1] x"));
        }

        [Test]
        public void Parse_EmptyOrWhitespaceOnly_Throws()
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse(""));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("   "));
        }

        [Test]
        public void Parse_LeadingZeroNumber_Throws()
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("01"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("-01"));
            Assert.DoesNotThrow(() => JsonValue.Parse("0"));
        }

        [Test]
        public void Parse_MalformedNumber_Throws()
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("1."));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("1e"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("-"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse(".5"));
        }

        [Test]
        public void Parse_DuplicateObjectKey_Throws()
        {
            var ex = Assert.Throws<JsonParseException>(() => JsonValue.Parse(@"{""a"":1,""a"":2}"));
            StringAssert.Contains("重复", ex.Message);
        }

        [Test]
        public void Parse_BadLiteral_Throws()
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("tru"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("True"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("nul"));
        }

        [Test]
        public void Parse_MalformedStructure_Throws()
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("{"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("[1,]"));
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("{\"a\" 1}")); // 缺冒号
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("{\"a\":1,}")); // 尾逗号
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("[1 2]")); // 缺逗号
        }

        [Test]
        public void Parse_String_Issues_Throw()
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("\"未闭合")); // 缺结尾引号
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("\"a\\qb\"")); // 无效转义
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("\"a\\u12\"")); // \u 位数不足
            Assert.Throws<JsonParseException>(() => JsonValue.Parse("\"a\nb\"")); // 未转义控制字符
        }

        [Test]
        public void Parse_DepthBeyondLimit_Throws()
        {
            var deep = new string('[', 70) + new string(']', 70);
            var ex = Assert.Throws<JsonParseException>(() => JsonValue.Parse(deep));
            StringAssert.Contains("深度", ex.Message);
            // 上限以内的正常嵌套不受影响
            Assert.DoesNotThrow(() => JsonValue.Parse(new string('[', 64) + new string(']', 64)));
        }

        // ── 容错与定位 ────────────────────────────────────────────

        [Test]
        public void Parse_LeadingUtf8Bom_Tolerated()
        {
            var v = JsonValue.Parse("\uFEFF{\"a\":1}");
            Assert.AreEqual(1d, v.Members["a"].AsNumber);
        }

        [Test]
        public void Parse_Error_ReportsLineAndColumn()
        {
            // 第 3 行第 8 列的 '@' 不是合法值
            var ex = Assert.Throws<JsonParseException>(() => JsonValue.Parse("{\n  \"a\": 1,\n  \"b\": @\n}"));
            Assert.AreEqual(3, ex.Line);
            Assert.AreEqual(8, ex.Column);
        }

        [Test]
        public void Parse_CrlfCounted_AsSingleNewline()
        {
            var ex = Assert.Throws<JsonParseException>(() => JsonValue.Parse("{\r\n  \"a\": !\r\n}"));
            Assert.AreEqual(2, ex.Line);
            Assert.AreEqual(8, ex.Column);
        }

        [Test]
        public void Parse_NullInput_ThrowsArgumentNull()
        {
            Assert.Throws<System.ArgumentNullException>(() => JsonValue.Parse(null));
        }
    }
}
