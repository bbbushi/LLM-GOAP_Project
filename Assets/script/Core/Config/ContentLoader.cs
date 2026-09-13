using System;
using System.Collections.Generic;
using System.Text;
using Vibe.Core.Config.Json;
using Vibe.Core.Contracts;

namespace Vibe.Core.Config
{
    /// <summary>配置内容非法（结构不符、字段缺失、算子未知、id 重复等）时抛出；消息带条目定位。</summary>
    public sealed class ContentLoadException : Exception
    {
        public ContentLoadException(string message) : base($"配置加载失败：{message}") { }
    }

    /// <summary>
    /// 行动/目标配置加载器：JSON 文本 → <see cref="IAction"/>/<see cref="IGoal"/> 列表。
    /// 输入格式由 Docs/schemas/action.schema.json 与 goal.schema.json（v1，冻结契约）定义；
    /// 加载器逐条镜像 Schema 约束做防御性校验——Schema 服务于 LLM 输出的规则验证层，
    /// 加载器保证进入内核的数据必然合法（同一约束两处执行，改契约须两处同步）。
    /// 语法错误抛 <see cref="JsonParseException"/>（带行列）；语义错误抛 <see cref="ContentLoadException"/>。
    /// 只做文本 → 对象，不做文件 IO（路径归调用方，内核不持有文件系统假设）。
    /// </summary>
    public static class ContentLoader
    {
        /// <summary>本加载器理解的契约版本（与 Schema 的 version 字段绑定）。</summary>
        public const int SupportedVersion = 1;

        private static readonly Dictionary<string, ConditionOp> ConditionOps =
            new Dictionary<string, ConditionOp>
            {
                ["GreaterOrEqual"] = ConditionOp.GreaterOrEqual,
                ["Greater"] = ConditionOp.Greater,
                ["Equal"] = ConditionOp.Equal,
                ["Less"] = ConditionOp.Less,
                ["LessOrEqual"] = ConditionOp.LessOrEqual,
                ["NotEqual"] = ConditionOp.NotEqual
            };

        private static readonly Dictionary<string, EffectOp> EffectOps =
            new Dictionary<string, EffectOp>
            {
                ["Add"] = EffectOp.Add,
                ["Set"] = EffectOp.Set,
                ["MultiplyBy"] = EffectOp.MultiplyBy
            };

        private static readonly HashSet<string> ActionFields =
            new HashSet<string> { "id", "baseCost", "preconditions", "effects" };

        private static readonly HashSet<string> GoalFields =
            new HashSet<string> { "id", "priority", "conditions" };

        private static readonly HashSet<string> ConditionFields =
            new HashSet<string> { "key", "op", "threshold" };

        private static readonly HashSet<string> EffectFields =
            new HashSet<string> { "key", "op", "amount" };

        /// <summary>解析一份行动库（{version, actions:[…]}）。返回顺序与配置一致。</summary>
        public static IReadOnlyList<IAction> LoadActions(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var root = JsonValue.Parse(json);

            var items = RequireRoot(root, "actions");
            var actions = new List<IAction>(items.Count);
            var seenIds = new HashSet<string>();

            for (int i = 0; i < items.Count; i++)
            {
                string path = $"actions[{i}]";
                var item = RequireObject(items[i], path);
                CheckKnownFields(item, ActionFields, path);

                string id = RequireNonEmptyString(item, "id", path);
                double cost = RequireNumber(item, "baseCost", path);
                if (cost < 0d)
                    throw Fail($"{path} ('{id}')：baseCost 不能为负（实际 {cost}）");
                float baseCost = (float)cost;

                var preconditions = ParseConditionList(item, "preconditions", $"actions[{i}] ('{id}')");
                var effects = ParseEffectList(item, "effects", $"actions[{i}] ('{id}')");

                if (!seenIds.Add(id))
                    throw Fail($"{path}：id '{id}' 在库内重复（id 必须唯一）");
                actions.Add(new ActionConfig(id, baseCost, preconditions, effects));
            }
            return actions;
        }

        /// <summary>解析一份目标库（{version, goals:[…]}）。返回顺序与配置一致（优先级排序归规划调用方）。</summary>
        public static IReadOnlyList<IGoal> LoadGoals(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var root = JsonValue.Parse(json);

            var items = RequireRoot(root, "goals");
            var goals = new List<IGoal>(items.Count);
            var seenIds = new HashSet<string>();

            for (int i = 0; i < items.Count; i++)
            {
                string path = $"goals[{i}]";
                var item = RequireObject(items[i], path);
                CheckKnownFields(item, GoalFields, path);

                string id = RequireNonEmptyString(item, "id", path);
                float priority = (float)RequireNumber(item, "priority", path);

                var conditions = ParseConditionList(item, "conditions", $"goals[{i}] ('{id}')");
                if (conditions.Count == 0)
                    throw Fail($"{path} ('{id}')：conditions 至少要有一条（空条件的目标恒达成，无意义）");

                if (!seenIds.Add(id))
                    throw Fail($"{path}：id '{id}' 在库内重复（id 必须唯一）");
                goals.Add(new GoalConfig(id, priority, conditions));
            }
            return goals;
        }

        // —— 顶层结构 ——

        /// <summary>校验顶层 {version, <paramref name="field"/>:[…]} 并返回条目列表。</summary>
        private static IReadOnlyList<JsonValue> RequireRoot(JsonValue root, string field)
        {
            if (root.Type != JsonType.Object)
                throw Fail($"顶层必须是对象（实际 {TypeName(root)}）");

            var version = RequireMember(root, "version");
            if (version.Type != JsonType.Number)
                throw Fail($"顶层 version 必须是数值（实际 {TypeName(version)}）");
            if ((int)version.AsNumber != version.AsNumber || (int)version.AsNumber != SupportedVersion)
                throw Fail($"不支持的契约版本 {version.AsNumber}（本加载器支持 version={SupportedVersion}；"
                    + "契约变更见 Docs/schemas/ 与 DESIGN.md §4.4）");

            var collection = RequireMember(root, field);
            if (collection.Type != JsonType.Array)
                throw Fail($"顶层 {field} 必须是数组（实际 {TypeName(collection)}）");
            if (collection.Items.Count == 0)
                throw Fail($"顶层 {field} 不能为空（至少一条配置）");
            return collection.Items;
        }

        // —— 条件 / 效果 ——

        private static IReadOnlyList<WorldCondition> ParseConditionList(JsonValue parent, string field, string ownerPath)
        {
            var list = RequireMember(parent, field);
            if (list.Type != JsonType.Array)
                throw Fail($"{ownerPath} 的 {field} 必须是数组（实际 {TypeName(list)}）");

            var result = new List<WorldCondition>(list.Items.Count);
            for (int i = 0; i < list.Items.Count; i++)
                result.Add(ParseCondition(list.Items[i], $"{ownerPath} 的 {field}[{i}]"));
            return result;
        }

        private static IReadOnlyList<WorldEffect> ParseEffectList(JsonValue parent, string field, string ownerPath)
        {
            var list = RequireMember(parent, field);
            if (list.Type != JsonType.Array)
                throw Fail($"{ownerPath} 的 {field} 必须是数组（实际 {TypeName(list)}）");

            var result = new List<WorldEffect>(list.Items.Count);
            for (int i = 0; i < list.Items.Count; i++)
                result.Add(ParseEffect(list.Items[i], $"{ownerPath} 的 {field}[{i}]"));
            return result;
        }

        private static WorldCondition ParseCondition(JsonValue value, string path)
        {
            var obj = RequireObject(value, path);
            CheckKnownFields(obj, ConditionFields, path);

            string key = RequireNonEmptyString(obj, "key", path);
            double threshold = RequireNumber(obj, "threshold", path);
            ConditionOp op = ConditionOp.GreaterOrEqual; // ≥ 基线（GOAP.md §2.1），配置可显式覆盖
            if (obj.TryGetMember("op", out var opValue))
                op = ParseOperator(opValue, ConditionOps, path, "GreaterOrEqual");
            return new WorldCondition(key, op, threshold);
        }

        private static WorldEffect ParseEffect(JsonValue value, string path)
        {
            var obj = RequireObject(value, path);
            CheckKnownFields(obj, EffectFields, path);

            string key = RequireNonEmptyString(obj, "key", path);
            double amount = RequireNumber(obj, "amount", path);
            EffectOp op = EffectOp.Add; // += 基线（GOAP.md §2.1），配置可显式覆盖
            if (obj.TryGetMember("op", out var opValue))
                op = ParseOperator(opValue, EffectOps, path, "Add");
            return new WorldEffect(key, op, amount);
        }

        private static T ParseOperator<T>(JsonValue value, Dictionary<string, T> table, string path, string baseline)
        {
            if (value.Type != JsonType.String)
                throw Fail($"{path}：op 必须是字符串（实际 {TypeName(value)}；省略则取基线 {baseline}）");
            string name = value.AsString;
            if (!table.TryGetValue(name, out T op))
                throw Fail($"{path}：未知算子 '{name}'（有效值：{JoinKeys(table.Keys)}）");
            return op;
        }

        // —— 字段级校验 ——

        private static JsonValue RequireMember(JsonValue obj, string field)
        {
            if (!obj.TryGetMember(field, out var value))
                throw Fail($"缺少必填字段 '{field}'");
            return value;
        }

        private static JsonValue RequireObject(JsonValue value, string path)
        {
            if (value.Type != JsonType.Object)
                throw Fail($"{path} 必须是对象（实际 {TypeName(value)}）");
            return value;
        }

        private static string RequireNonEmptyString(JsonValue obj, string field, string path)
        {
            var value = RequireMember(obj, field);
            if (value.Type != JsonType.String)
                throw Fail($"{path} 的 {field} 必须是字符串（实际 {TypeName(value)}）");
            if (value.AsString.Length == 0)
                throw Fail($"{path} 的 {field} 不能为空字符串");
            return value.AsString;
        }

        private static double RequireNumber(JsonValue obj, string field, string path)
        {
            var value = RequireMember(obj, field);
            if (value.Type != JsonType.Number)
                throw Fail($"{path} 的 {field} 必须是数值（实际 {TypeName(value)}）");
            return value.AsNumber;
        }

        /// <summary>镜像 Schema 的 additionalProperties:false——未知字段（常见为拼写错误）直接报错，绝不静默忽略。</summary>
        private static void CheckKnownFields(JsonValue obj, HashSet<string> allowed, string path)
        {
            foreach (var key in obj.Members.Keys)
                if (!allowed.Contains(key))
                    throw Fail($"{path}：未知字段 '{key}'（允许的字段：{JoinKeys(allowed)}）");
        }

        private static string TypeName(JsonValue value)
        {
            switch (value.Type)
            {
                case JsonType.Null: return "null";
                case JsonType.Boolean: return "布尔";
                case JsonType.Number: return "数值";
                case JsonType.String: return "字符串";
                case JsonType.Array: return "数组";
                case JsonType.Object: return "对象";
                default: return value.Type.ToString();
            }
        }

        private static string JoinKeys(IEnumerable<string> keys)
        {
            var sb = new StringBuilder();
            foreach (var k in keys)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(k);
            }
            return sb.ToString();
        }

        private static ContentLoadException Fail(string message) => new ContentLoadException(message);
    }
}
