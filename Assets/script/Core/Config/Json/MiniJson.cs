using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vibe.Core.Config.Json
{
    /// <summary>JSON 值的类型。</summary>
    public enum JsonType
    {
        Null,
        Boolean,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// JSON 解析错误：携带 1 起始的行/列定位。配置文本非法（语法错误、尾随垃圾、超深嵌套等）时抛出。
    /// </summary>
    public sealed class JsonParseException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public JsonParseException(string message, int line, int column)
            : base($"JSON 解析错误（行 {line} 列 {column}）：{message}")
        {
            Line = line;
            Column = column;
        }
    }

    /// <summary>
    /// 解析后的一个 JSON 值（不可变）。覆盖 JSON 全集：对象/数组/字符串/数值/布尔/null。
    /// 内核零依赖（Vibe.Core noEngineReferences），故自带此迷你解析器而不用
    /// Newtonsoft/JsonUtility——后者绑 Unity 编辑器环境，无法 headless 独立运行。
    /// 仅服务于低频配置加载，不在帧循环中调用，性能非首要目标。
    /// </summary>
    public sealed class JsonValue
    {
        /// <summary>空数组/空对象的共享只读包装，避免空集合重复分配。</summary>
        private static readonly IReadOnlyList<JsonValue> EmptyList = new JsonValue[0];
        private static readonly IReadOnlyDictionary<string, JsonValue> EmptyMap =
            new Dictionary<string, JsonValue>();

        public JsonType Type { get; }

        private readonly bool _boolean;
        private readonly double _number;
        private readonly string _string;
        private readonly IReadOnlyList<JsonValue> _items;
        private readonly IReadOnlyDictionary<string, JsonValue> _members;

        /// <summary>Type == Boolean 时的值；其他类型访问抛 InvalidOperationException。</summary>
        public bool AsBoolean =>
            Type == JsonType.Boolean ? _boolean : throw WrongType(JsonType.Boolean);

        /// <summary>Type == Number 时的值（double）；其他类型访问抛 InvalidOperationException。</summary>
        public double AsNumber =>
            Type == JsonType.Number ? _number : throw WrongType(JsonType.Number);

        /// <summary>Type == String 时的值；其他类型访问抛 InvalidOperationException。</summary>
        public string AsString =>
            Type == JsonType.String ? _string : throw WrongType(JsonType.String);

        /// <summary>Type == Array 时的元素列表；其他类型访问抛 InvalidOperationException。</summary>
        public IReadOnlyList<JsonValue> Items =>
            Type == JsonType.Array ? _items : throw WrongType(JsonType.Array);

        /// <summary>Type == Object 时的成员字典；其他类型访问抛 InvalidOperationException。</summary>
        public IReadOnlyDictionary<string, JsonValue> Members =>
            Type == JsonType.Object ? _members : throw WrongType(JsonType.Object);

        private InvalidOperationException WrongType(JsonType expected) =>
            new InvalidOperationException(
                $"值类型是 {Type}，不能当 {expected} 访问（先判 Type / TryGetMember 再取值）");

        private JsonValue(JsonType type, bool b, double n, string s,
            IReadOnlyList<JsonValue> items, IReadOnlyDictionary<string, JsonValue> members)
        {
            Type = type; _boolean = b; _number = n; _string = s;
            _items = items; _members = members;
        }

        public static readonly JsonValue Null = new JsonValue(JsonType.Null, false, 0d, null, null, null);

        public static JsonValue Of(bool value) => new JsonValue(JsonType.Boolean, value, 0d, null, null, null);
        public static JsonValue Of(double value) => new JsonValue(JsonType.Number, false, value, null, null, null);
        public static JsonValue Of(string value) => new JsonValue(JsonType.String, false, 0d, value, null, null);
        public static JsonValue Of(IReadOnlyList<JsonValue> items) =>
            new JsonValue(JsonType.Array, false, 0d, null, items ?? EmptyList, null);
        public static JsonValue Of(IReadOnlyDictionary<string, JsonValue> members) =>
            new JsonValue(JsonType.Object, false, 0d, null, null, members ?? EmptyMap);

        /// <summary>取对象成员；键不存在或值非对象时返回 false（值为 null 成员时返回 true + <see cref="Null"/>）。</summary>
        public bool TryGetMember(string key, out JsonValue value)
        {
            if (Type == JsonType.Object && Members.TryGetValue(key, out value)) return true;
            value = null;
            return false;
        }

        /// <summary>解析一段完整 JSON 文档（前后允许空白，禁止尾随垃圾）。</summary>
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var parser = new Parser(text);
            return parser.ParseDocument();
        }

        /// <summary>递归下降解析器：单遍扫描，行列定位报错。</summary>
        private sealed class Parser
        {
            private const int MaxDepth = 64; // 防御深度嵌套爆栈（正常配置远浅于此）

            private readonly string _text;
            private int _pos;
            private int _line = 1;
            private int _col = 1;

            public Parser(string text)
            {
                _text = text;
            }

            public JsonValue ParseDocument()
            {
                SkipUtf8Bom();
                SkipWhitespace();
                var value = ParseValue(0);
                SkipWhitespace();
                if (_pos < _text.Length)
                    Fail($"文档结尾后仍有额外内容（'{Cur()}'）");
                return value;
            }

            private JsonValue ParseValue(int depth)
            {
                if (depth > MaxDepth)
                    Fail($"嵌套深度超过上限 {MaxDepth}");
                if (_pos >= _text.Length)
                    Fail("输入意外结束，期望一个值");

                char c = Cur();
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return JsonValue.Of(ParseString());
                    case 't': return ParseLiteral("true", JsonValue.Of(true));
                    case 'f': return ParseLiteral("false", JsonValue.Of(false));
                    case 'n': return ParseLiteral("null", JsonValue.Null);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return JsonValue.Of(ParseNumber());
                        Fail($"意外的字符 '{c}'，期望一个 JSON 值");
                        return null; // 不可达
                }
            }

            private JsonValue ParseObject(int depth)
            {
                Expect('{');
                var members = new Dictionary<string, JsonValue>();
                SkipWhitespace();
                if (CurIs('}')) { Advance(); return JsonValue.Of((IReadOnlyDictionary<string, JsonValue>)members); }

                while (true)
                {
                    SkipWhitespace();
                    if (!CurIs('"')) Fail("期望对象键（字符串）");
                    string key = ParseString();
                    if (members.ContainsKey(key)) Fail($"对象中重复的键 '{key}'");
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    members[key] = ParseValue(depth + 1);
                    SkipWhitespace();
                    if (CurIs(',')) { Advance(); continue; }
                    if (CurIs('}')) { Advance(); break; }
                    Fail("期望 ',' 或 '}'");
                }
                return JsonValue.Of((IReadOnlyDictionary<string, JsonValue>)members);
            }

            private JsonValue ParseArray(int depth)
            {
                Expect('[');
                var items = new List<JsonValue>();
                SkipWhitespace();
                if (CurIs(']')) { Advance(); return JsonValue.Of((IReadOnlyList<JsonValue>)items); }

                while (true)
                {
                    SkipWhitespace();
                    items.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    if (CurIs(',')) { Advance(); continue; }
                    if (CurIs(']')) { Advance(); break; }
                    Fail("期望 ',' 或 ']'");
                }
                return JsonValue.Of((IReadOnlyList<JsonValue>)items);
            }

            private JsonValue ParseLiteral(string literal, JsonValue value)
            {
                foreach (char expected in literal)
                {
                    if (_pos >= _text.Length || _text[_pos] != expected)
                        Fail($"无效的字面量，期望 '{literal}'");
                    Advance();
                }
                return value;
            }

            /// <summary>严格按 JSON 数字文法（-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?）扫描后转 double。</summary>
            private double ParseNumber()
            {
                int start = _pos;
                if (CurIs('-')) Advance();

                if (_pos >= _text.Length) Fail("数字意外结束");
                char d = Cur();
                if (d == '0')
                {
                    Advance();
                    if (_pos < _text.Length && Cur() >= '0' && Cur() <= '9')
                        Fail("整数部分不允许前导零后的数字（JSON 严格文法）");
                }
                else if (d >= '1' && d <= '9')
                {
                    while (_pos < _text.Length && Cur() >= '0' && Cur() <= '9') Advance();
                }
                else
                {
                    Fail($"意外的字符 '{d}'，期望数字");
                }

                if (CurIs('.'))
                {
                    Advance();
                    if (!CurIsDigit()) Fail("小数点后缺少数字");
                    while (CurIsDigit()) Advance();
                }

                if (CurIs('e') || CurIs('E'))
                {
                    Advance();
                    if (CurIs('+') || CurIs('-')) Advance();
                    if (!CurIsDigit()) Fail("指数缺少数字");
                    while (CurIsDigit()) Advance();
                }

                string token = _text.Substring(start, _pos - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    Fail($"无法解析数字 '{token}'");
                return value;
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_pos >= _text.Length) Fail("字符串意外结束（缺少结尾引号）");
                    char c = Cur();

                    if (c == '"') { Advance(); return sb.ToString(); }

                    if (c == '\\')
                    {
                        Advance();
                        if (_pos >= _text.Length) Fail("转义序列意外结束");
                        char e = Cur();
                        switch (e)
                        {
                            case '"': sb.Append('"'); Advance(); break;
                            case '\\': sb.Append('\\'); Advance(); break;
                            case '/': sb.Append('/'); Advance(); break;
                            case 'b': sb.Append('\b'); Advance(); break;
                            case 'f': sb.Append('\f'); Advance(); break;
                            case 'n': sb.Append('\n'); Advance(); break;
                            case 'r': sb.Append('\r'); Advance(); break;
                            case 't': sb.Append('\t'); Advance(); break;
                            case 'u': Advance(); sb.Append(ParseUnicodeEscape()); break;
                            default:
                                Fail($"无效的转义字符 '\\{e}'");
                                break;
                        }
                        continue;
                    }

                    if (c < 0x20) Fail("字符串中包含未转义的控制字符");
                    sb.Append(c);
                    Advance();
                }
            }

            /// <summary>\uXXXX（4 位十六进制）。代理对由两个转义各自贡献一个 UTF-16 码元，天然拼合。</summary>
            private char ParseUnicodeEscape()
            {
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (_pos >= _text.Length) Fail("\\u 转义意外结束（需要 4 位十六进制）");
                    char h = Cur();
                    int digit;
                    if (h >= '0' && h <= '9') digit = h - '0';
                    else if (h >= 'a' && h <= 'f') digit = h - 'a' + 10;
                    else if (h >= 'A' && h <= 'F') digit = h - 'A' + 10;
                    else { Fail($"\\u 转义中的无效字符 '{h}'"); digit = 0; }
                    value = value * 16 + digit;
                    Advance();
                }
                return (char)value;
            }

            private void SkipUtf8Bom()
            {
                // UTF-8 BOM（EF BB BF）在字符串形态下呈现为此字符；容忍前置 BOM，其余照旧严格
                if (_pos < _text.Length && _text[_pos] == '\uFEFF')
                {
                    Advance();
                    _col = 1; // BOM 不占可见列
                }
            }

            private void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == ' ' || c == '\t') Advance();
                    else if (c == '\n' || c == '\r') AdvanceNewline();
                    else break;
                }
            }

            private char Cur()
            {
                return _text[_pos];
            }

            private bool CurIs(char c)
            {
                return _pos < _text.Length && _text[_pos] == c;
            }

            private bool CurIsDigit()
            {
                return _pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9';
            }

            private void Expect(char c)
            {
                if (!CurIs(c)) Fail($"期望 '{c}'，实际为 '{(_pos < _text.Length ? Cur().ToString() : "输入结束")}'");
                Advance();
            }

            private void Advance()
            {
                _pos++;
                _col++;
            }

            private void AdvanceNewline()
            {
                if (_text[_pos] == '\r' && _pos + 1 < _text.Length && _text[_pos + 1] == '\n')
                    _pos++; // CRLF 记作一次换行
                _pos++;
                _line++;
                _col = 1;
            }

            private void Fail(string message)
            {
                throw new JsonParseException(message, _line, _col);
            }
        }
    }
}
