using System.IO;
using NUnit.Framework;
using Vibe.Core;
using Vibe.Core.Config;
using Vibe.Core.Contracts;

namespace Vibe.Core.Tests
{
    /// <summary>
    /// ContentLoader 契约测试：合法配置全量解析（含算子缺省与全集映射）、
    /// 防御性校验逐条镜像 Schema v1 约束（未知字段/算子、重复 id、负代价、版本号、空库）、
    /// 加载结果与 WorldState 引擎的互操作、示例文件在盘可加载。
    /// Schema 单一事实源：Docs/schemas/action.schema.json、goal.schema.json。
    /// </summary>
    public class ContentLoadingTests
    {
        // 与 Docs/schemas/examples/action.example.json 同步的样例（盘上文件另有测试）
        private const string ExampleActions =
            @"{""version"":1,""actions"":[
                {""id"":""gather_berries"",""baseCost"":4,
                 ""preconditions"":[{""key"":""at_forest"",""threshold"":1}],
                 ""effects"":[{""key"":""food"",""amount"":1}]},
                {""id"":""chop_wood"",""baseCost"":6,
                 ""preconditions"":[{""key"":""at_forest"",""threshold"":1}],
                 ""effects"":[{""key"":""wood"",""amount"":1}]},
                {""id"":""eat"",""baseCost"":1,
                 ""preconditions"":[{""key"":""food"",""threshold"":1}],
                 ""effects"":[{""key"":""food"",""amount"":-1},{""key"":""hunger"",""op"":""Set"",""amount"":0}]}
            ]}";

        private const string ExampleGoals =
            @"{""version"":1,""goals"":[
                {""id"":""stay_fed"",""priority"":10,""conditions"":[{""key"":""food"",""threshold"":2}]},
                {""id"":""stock_wood"",""priority"":5,""conditions"":[{""key"":""wood"",""threshold"":3}]}
            ]}";

        // ── 合法配置全量解析 ──────────────────────────────────────

        [Test]
        public void LoadActions_Example_ParsesAllInOrder()
        {
            var actions = ContentLoader.LoadActions(ExampleActions);
            Assert.AreEqual(3, actions.Count);
            Assert.AreEqual("gather_berries", actions[0].Id);
            Assert.AreEqual(4f, actions[0].BaseCost);
            Assert.AreEqual("chop_wood", actions[1].Id);
            Assert.AreEqual(6f, actions[1].BaseCost);
            Assert.AreEqual("eat", actions[2].Id);
            Assert.AreEqual(1f, actions[2].BaseCost);
        }

        [Test]
        public void LoadActions_OperatorsDefaultToBaseline()
        {
            // op 省略：前提取 ≥ 基线、效果取 += 基线（GOAP.md §2.1）
            var actions = ContentLoader.LoadActions(ExampleActions);
            Assert.AreEqual(ConditionOp.GreaterOrEqual, actions[0].Preconditions[0].Op);
            Assert.AreEqual("at_forest", actions[0].Preconditions[0].Key);
            Assert.AreEqual(1d, actions[0].Preconditions[0].Threshold);
            Assert.AreEqual(EffectOp.Add, actions[0].Effects[0].Op);
            Assert.AreEqual("food", actions[0].Effects[0].Key);
            Assert.AreEqual(1d, actions[0].Effects[0].Amount);
        }

        [Test]
        public void LoadActions_OperatorFullSet_MapsToEnums()
        {
            string json = @"{""version"":1,""actions"":[{""id"":""a"",""baseCost"":1,
                ""preconditions"":[
                    {""key"":""k1"",""op"":""Greater"",""threshold"":1},
                    {""key"":""k2"",""op"":""Equal"",""threshold"":2},
                    {""key"":""k3"",""op"":""Less"",""threshold"":3},
                    {""key"":""k4"",""op"":""LessOrEqual"",""threshold"":4},
                    {""key"":""k5"",""op"":""NotEqual"",""threshold"":5}],
                ""effects"":[
                    {""key"":""e1"",""op"":""Set"",""amount"":0},
                    {""key"":""e2"",""op"":""MultiplyBy"",""amount"":0.5}]}]}";
            var action = ContentLoader.LoadActions(json)[0];

            var expected = new[]
            {
                ConditionOp.Greater, ConditionOp.Equal, ConditionOp.Less,
                ConditionOp.LessOrEqual, ConditionOp.NotEqual
            };
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], action.Preconditions[i].Op, $"preconditions[{i}]");

            Assert.AreEqual(EffectOp.Set, action.Effects[0].Op);
            Assert.AreEqual(0d, action.Effects[0].Amount);
            Assert.AreEqual(EffectOp.MultiplyBy, action.Effects[1].Op);
            Assert.AreEqual(0.5d, action.Effects[1].Amount);
        }

        [Test]
        public void LoadActions_EmptyPreconditionsAllowed_EmptyEffectsAllowed()
        {
            string json = @"{""version"":1,""actions"":[{""id"":""idle"",""baseCost"":0,
                ""preconditions"":[],""effects"":[]}]}";
            var action = ContentLoader.LoadActions(json)[0];
            Assert.AreEqual(0, action.Preconditions.Count);
            Assert.AreEqual(0, action.Effects.Count);
            Assert.AreEqual(0f, action.BaseCost); // 0 代价合法（免费行动）
        }

        [Test]
        public void LoadGoals_Example_ParsesAllInOrder()
        {
            var goals = ContentLoader.LoadGoals(ExampleGoals);
            Assert.AreEqual(2, goals.Count);
            Assert.AreEqual("stay_fed", goals[0].Id);
            Assert.AreEqual(10f, goals[0].Priority);
            Assert.AreEqual(2d, goals[0].Conditions[0].Threshold);
            Assert.AreEqual("stock_wood", goals[1].Id);
            Assert.AreEqual(5f, goals[1].Priority);
        }

        [Test]
        public void LoadGoals_NegativePriorityAllowed()
        {
            string json = @"{""version"":1,""goals"":[{""id"":""g"",""priority"":-2.5,
                ""conditions"":[{""key"":""food"",""threshold"":1}]}]}";
            Assert.AreEqual(-2.5f, ContentLoader.LoadGoals(json)[0].Priority);
        }

        // ── 防御性校验（镜像 Schema v1）──────────────────────────

        [Test]
        public void LoadActions_DuplicateId_Throws()
        {
            string json = @"{""version"":1,""actions"":[
                {""id"":""a"",""baseCost"":1,""preconditions"":[],""effects"":[]},
                {""id"":""a"",""baseCost"":2,""preconditions"":[],""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
            StringAssert.Contains("重复", ex.Message);
            StringAssert.Contains("actions[1]", ex.Message);
        }

        [Test]
        public void LoadActions_NegativeBaseCost_Throws()
        {
            string json = @"{""version"":1,""actions"":[{""id"":""a"",""baseCost"":-1,
                ""preconditions"":[],""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
            StringAssert.Contains("不能为负", ex.Message);
        }

        [Test]
        public void LoadActions_UnknownField_Throws()
        {
            // 拼写错误必须显式报错，绝不静默忽略（LLM 生成配置的典型失败模式）
            string json = @"{""version"":1,""actions"":[{""id"":""a"",""basecost"":1,
                ""preconditions"":[],""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
            StringAssert.Contains("未知字段 'basecost'", ex.Message);
        }

        [Test]
        public void LoadActions_MissingRequiredField_Throws()
        {
            string json = @"{""version"":1,""actions"":[{""id"":""a"",
                ""preconditions"":[],""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
            StringAssert.Contains("baseCost", ex.Message);
        }

        [Test]
        public void LoadActions_WrongVersion_Throws()
        {
            string v2 = @"{""version"":2,""actions"":[{""id"":""a"",""baseCost"":1,
                ""preconditions"":[],""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(v2));
            StringAssert.Contains("不支持的契约版本", ex.Message);
        }

        [Test]
        public void LoadActions_MissingVersion_Throws()
        {
            string json = @"{""actions"":[{""id"":""a"",""baseCost"":1,
                ""preconditions"":[],""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
            StringAssert.Contains("version", ex.Message);
        }

        [Test]
        public void LoadActions_EmptyLibrary_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(@"{""version"":1,""actions"":[]}"));
            StringAssert.Contains("不能为空", ex.Message);
        }

        [Test]
        public void LoadActions_UnknownOperator_Throws()
        {
            string json = @"{""version"":1,""actions"":[{""id"":""a"",""baseCost"":1,
                ""preconditions"":[{""key"":""k"",""op"":""at_least"",""threshold"":1}],
                ""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
            StringAssert.Contains("未知算子", ex.Message);
            StringAssert.Contains("GreaterOrEqual", ex.Message); // 消息列出全部有效值
        }

        [Test]
        public void LoadActions_NullOperator_Throws()
        {
            // op 显式为 null 不是省略，必须报错而非取缺省
            string json = @"{""version"":1,""actions"":[{""id"":""a"",""baseCost"":1,
                ""preconditions"":[{""key"":""k"",""op"":null,""threshold"":1}],
                ""effects"":[]}]}";
            Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
        }

        [Test]
        public void LoadActions_WrongFieldType_Throws()
        {
            string json = @"{""version"":1,""actions"":[{""id"":3,""baseCost"":1,
                ""preconditions"":[],""effects"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadActions(json));
            StringAssert.Contains("字符串", ex.Message);
        }

        [Test]
        public void LoadGoals_EmptyConditions_Throws()
        {
            string json = @"{""version"":1,""goals"":[{""id"":""g"",""priority"":1,""conditions"":[]}]}";
            var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadGoals(json));
            StringAssert.Contains("至少", ex.Message);
        }

        [Test]
        public void LoadGoals_DuplicateId_Throws()
        {
            string json = @"{""version"":1,""goals"":[
                {""id"":""g"",""priority"":1,""conditions"":[{""key"":""k"",""threshold"":1}]},
                {""id"":""g"",""priority"":2,""conditions"":[{""key"":""k"",""threshold"":2}]}]}";
            Assert.Throws<ContentLoadException>(() => ContentLoader.LoadGoals(json));
        }

        [Test]
        public void LoadActions_JsonSyntaxError_PropagatesParseException()
        {
            // 语法错误（带行列）不该被吞成语义错误
            Assert.Throws<Vibe.Core.Config.Json.JsonParseException>(
                () => ContentLoader.LoadActions(@"{""version"":1, ""actions"":[}"));
        }

        [Test]
        public void LoadActions_NullInput_ThrowsArgumentNull()
        {
            Assert.Throws<System.ArgumentNullException>(() => ContentLoader.LoadActions(null));
            Assert.Throws<System.ArgumentNullException>(() => ContentLoader.LoadGoals(null));
        }

        // ── 与引擎互操作 ─────────────────────────────────────────

        [Test]
        public void LoadedAction_WorksAgainstWorldState()
        {
            var eat = ContentLoader.LoadActions(ExampleActions)[2];
            var ws = new WorldState();
            ws.Set("food", 1);

            Assert.IsTrue(ws.Meets(eat.Preconditions), "food=1 应满足 eat 前提");
            ws.Apply(eat.Effects);
            Assert.AreEqual(0d, ws.Get("food"), "eat 后 food 1-1=0");
            Assert.AreEqual(0d, ws.Get("hunger"), "Set 0 对缺失键也生效（缺失键视为 0 后设定）");

            var goals = ContentLoader.LoadGoals(ExampleGoals);
            Assert.IsFalse(ws.Meets(goals[0].Conditions), "food=0 不满足 stay_fed（≥2）");
        }

        // ── 盘上示例文件 ─────────────────────────────────────────

        [Test]
        public void ExampleFiles_OnDisk_LoadCleanly()
        {
            // 工作目录为工程根时（Unity 编辑器与 headless 运行均是）直接吃盘上示例文件，
            // 保证 Docs/schemas/examples/ 与加载器永不分叉；其他目录运行则跳过。
            if (!TryReadFile("Docs/schemas/examples/action.example.json", out var actionsJson) ||
                !TryReadFile("Docs/schemas/examples/goal.example.json", out var goalsJson))
            {
                Assert.Ignore("工作目录下未找到示例文件，跳过（应在工程根运行）");
                return;
            }
            Assert.AreEqual(3, ContentLoader.LoadActions(actionsJson).Count);
            Assert.AreEqual(2, ContentLoader.LoadGoals(goalsJson).Count);
        }

        private static bool TryReadFile(string relativePath, out string content)
        {
            content = null;
            try
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
                if (!File.Exists(path)) return false;
                content = File.ReadAllText(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
