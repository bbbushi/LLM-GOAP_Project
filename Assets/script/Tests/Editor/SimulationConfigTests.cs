using System.IO;
using NUnit.Framework;
using Vibe.Core;
using Vibe.Core.Config;

namespace Vibe.Core.Tests
{
    /// <summary>
    /// LoadWorldState / LoadSimulation 契约测试：合法配置全量解析（含算子缺省）、
    /// 防御性校验逐条镜像 world-state.schema.json 与 simulation.schema.json（v1）约束
    /// （未知字段、版本号、整数与下界、空 agents、重复 id、npc. 前缀 localKeys、空 goalIds），
    /// 以及 M1 场景四件套在盘可加载（保证示例与加载器永不分叉）。
    /// Schema 单一事实源：Docs/schemas/。
    /// </summary>
    public class SimulationConfigTests
    {
        private const string WorldJson =
            @"{""version"":1,""tick"":3,""day"":2,
                ""values"":{""food"":7.5,""wood"":0,""npc.n1.hunger"":0.35}}";

        private const string SimJson =
            @"{""version"":1,""ticksPerGameHour"":10,
                ""passiveEffects"":[
                    {""key"":""npc.n1.hunger"",""amount"":1},
                    {""key"":""npc.n1.flag"",""op"":""Set"",""amount"":2}],
                ""agents"":[
                    {""id"":""n1"",""localKeys"":[""hunger"",""at_forest""],""goalIds"":[""stay_alive"",""stock_up""]},
                    {""id"":""n2"",""localKeys"":[],""goalIds"":[""g""]}]}";

        // ── LoadWorldState ───────────────────────────────────────

        [Test]
        public void LoadWorldState_ParsesAllFields()
        {
            var ws = ContentLoader.LoadWorldState(WorldJson);

            Assert.AreEqual(3, ws.Tick);
            Assert.AreEqual(2, ws.Day);
            Assert.AreEqual(7.5d, ws.Get("food"));
            Assert.AreEqual(0d, ws.Get("wood"));
            Assert.AreEqual(0.35d, ws.Get("npc.n1.hunger"));
        }

        [Test]
        public void LoadWorldState_UnknownField_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadWorldState(@"{""version"":1,""tick"":0,""day"":1,""values"":{},""extra"":1}"));
            StringAssert.Contains("未知字段 'extra'", ex.Message);
        }

        [Test]
        public void LoadWorldState_WrongVersion_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadWorldState(@"{""version"":2,""tick"":0,""day"":1,""values"":{}}"));
            StringAssert.Contains("不支持的契约版本", ex.Message);
        }

        [Test]
        public void LoadWorldState_MissingField_Throws()
        {
            Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadWorldState(@"{""version"":1,""tick"":0,""day"":1}"));
        }

        [Test]
        public void LoadWorldState_FractionalTick_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadWorldState(@"{""version"":1,""tick"":0.5,""day"":1,""values"":{}}"));
            StringAssert.Contains("整数", ex.Message);
        }

        [Test]
        public void LoadWorldState_TickBelowZero_DayBelowOne_Throw()
        {
            Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadWorldState(@"{""version"":1,""tick"":-1,""day"":1,""values"":{}}"));
            Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadWorldState(@"{""version"":1,""tick"":0,""day"":0,""values"":{}}"));
        }

        [Test]
        public void LoadWorldState_NonNumberValue_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadWorldState(@"{""version"":1,""tick"":0,""day"":1,""values"":{""food"":""3""}}"));
            StringAssert.Contains("values.food", ex.Message);
        }

        [Test]
        public void LoadWorldState_NullInput_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => ContentLoader.LoadWorldState(null));
        }

        // ── LoadSimulation ───────────────────────────────────────

        [Test]
        public void LoadSimulation_ParsesAllFields_InOrder()
        {
            var cfg = ContentLoader.LoadSimulation(SimJson);

            Assert.AreEqual(10, cfg.TicksPerGameHour);
            Assert.AreEqual(2, cfg.PassiveEffects.Count);
            Assert.AreEqual(Vibe.Core.Contracts.EffectOp.Add, cfg.PassiveEffects[0].Op, "op 缺省取 Add 基线");
            Assert.AreEqual("npc.n1.hunger", cfg.PassiveEffects[0].Key);
            Assert.AreEqual(1d, cfg.PassiveEffects[0].Amount);
            Assert.AreEqual(Vibe.Core.Contracts.EffectOp.Set, cfg.PassiveEffects[1].Op);

            Assert.AreEqual(2, cfg.Agents.Count);
            Assert.AreEqual("n1", cfg.Agents[0].Id);
            Assert.AreEqual(new[] { "hunger", "at_forest" }, cfg.Agents[0].LocalKeys);
            Assert.AreEqual(new[] { "stay_alive", "stock_up" }, cfg.Agents[0].GoalIds);
            Assert.AreEqual(0, cfg.Agents[1].LocalKeys.Count, "localKeys 允许为空（纯共享资源 NPC）");
        }

        [Test]
        public void LoadSimulation_EmptyPassiveEffects_Allowed()
        {
            var cfg = ContentLoader.LoadSimulation(
                @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                   ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}]}");
            Assert.AreEqual(0, cfg.PassiveEffects.Count);
        }

        [Test]
        public void LoadSimulation_UnknownField_Throws_TopLevelAndAgent()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}],
                       ""seed"":42}"));
            StringAssert.Contains("未知字段 'seed'", ex.Message);

            ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""],""hp"":1}]}"));
            StringAssert.Contains("未知字段 'hp'", ex.Message);
        }

        [Test]
        public void LoadSimulation_WrongVersion_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":3,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}]}"));
            StringAssert.Contains("不支持的契约版本", ex.Message);
        }

        [Test]
        public void LoadSimulation_TicksPerGameHour_BadValues_Throw()
        {
            string agent = @"{""version"":1,""ticksPerGameHour"":{0},""passiveEffects"":[],
                ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}]}";
            Assert.Throws<ContentLoadException>(() => ContentLoader.LoadSimulation(agent.Replace("{0}", "0")));
            Assert.Throws<ContentLoadException>(() => ContentLoader.LoadSimulation(agent.Replace("{0}", "-5")));
            Assert.Throws<ContentLoadException>(() => ContentLoader.LoadSimulation(agent.Replace("{0}", "1.5")));
        }

        [Test]
        public void LoadSimulation_EmptyAgents_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],""agents"":[]}"));
            StringAssert.Contains("不能为空", ex.Message);
        }

        [Test]
        public void LoadSimulation_DuplicateAgentId_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]},
                                   {""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}]}"));
            StringAssert.Contains("重复", ex.Message);
        }

        [Test]
        public void LoadSimulation_EmptyGoalIds_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[]}]}"));
            StringAssert.Contains("至少", ex.Message);
        }

        [Test]
        public void LoadSimulation_NpcPrefixedLocalKey_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[""npc.n1.hunger""],""goalIds"":[""g""]}]}"));
            StringAssert.Contains("不得以 npc. 开头", ex.Message);
        }

        [Test]
        public void LoadSimulation_BadStringListEntries_Throw()
        {
            // 空字符串与非字符串条目都拒绝
            Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[""""],""goalIds"":[""g""]}]}"));
            Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[3],""goalIds"":[""g""]}]}"));
        }

        [Test]
        public void LoadSimulation_NullInput_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => ContentLoader.LoadSimulation(null));
        }

        // ── crises（M2 卡②增补，可选字段）──────────────────────

        [Test]
        public void LoadSimulation_CrisesAbsent_EmptyList()
        {
            var cfg = ContentLoader.LoadSimulation(SimJson); // SimJson 无 crises
            Assert.IsNotNull(cfg.Crises);
            Assert.AreEqual(0, cfg.Crises.Count, "缺省 crises = 无危机监测（向后兼容）");
        }

        [Test]
        public void LoadSimulation_Crises_ParsedInOrder_WithOps()
        {
            var cfg = ContentLoader.LoadSimulation(
                @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                   ""agents"":[{""id"":""n1"",""localKeys"":[""hunger""],""goalIds"":[""g""]}],
                   ""crises"":[
                     {""id"":""famine"",
                      ""conditions"":[{""key"":""food"",""op"":""LessOrEqual"",""threshold"":1}]},
                     {""id"":""exhaustion"",
                      ""conditions"":[{""key"":""npc.n1.hunger"",""op"":""Greater"",""threshold"":80}]}]}");

            Assert.AreEqual(2, cfg.Crises.Count, "顺序与配置一致（求值序即事件序）");
            Assert.AreEqual("famine", cfg.Crises[0].Id);
            Assert.AreEqual("food", cfg.Crises[0].Conditions[0].Key);
            Assert.AreEqual(Vibe.Core.Contracts.ConditionOp.LessOrEqual, cfg.Crises[0].Conditions[0].Op);
            Assert.AreEqual(1d, cfg.Crises[0].Conditions[0].Threshold);
            Assert.AreEqual("exhaustion", cfg.Crises[1].Id);
            Assert.AreEqual("npc.n1.hunger", cfg.Crises[1].Conditions[0].Key,
                "条件读完整键空间（npc.<id>.<key> 直接引用，不做投影）");
        }

        [Test]
        public void LoadSimulation_Crises_UnknownField_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}],
                       ""crises"":[{""id"":""famine"",""severity"":3,
                                    ""conditions"":[{""key"":""food"",""threshold"":1}]}]}"));
            StringAssert.Contains("未知字段 'severity'", ex.Message);
        }

        [Test]
        public void LoadSimulation_Crises_DuplicateId_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}],
                       ""crises"":[{""id"":""famine"",""conditions"":[{""key"":""food"",""threshold"":1}]},
                                   {""id"":""famine"",""conditions"":[{""key"":""wood"",""threshold"":1}]}]}"));
            StringAssert.Contains("重复", ex.Message);
        }

        [Test]
        public void LoadSimulation_Crises_EmptyConditions_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}],
                       ""crises"":[{""id"":""famine"",""conditions"":[]}]}"));
            StringAssert.Contains("至少", ex.Message);
        }

        [Test]
        public void LoadSimulation_Crises_NotArray_Throws()
        {
            var ex = Assert.Throws<ContentLoadException>(
                () => ContentLoader.LoadSimulation(
                    @"{""version"":1,""ticksPerGameHour"":1,""passiveEffects"":[],
                       ""agents"":[{""id"":""n1"",""localKeys"":[],""goalIds"":[""g""]}],
                       ""crises"":{""id"":""famine""}}"));
            StringAssert.Contains("必须是数组", ex.Message);
        }

        // ── M1 场景四件套在盘可加载 ─────────────────────────────

        [Test]
        public void M1ScenarioFiles_OnDisk_LoadCleanly()
        {
            // 工作目录为工程根时直接吃盘上的 M1 场景文件，保证 Docs/schemas/examples/m1-scenario/
            // 与加载器永不分叉；其他目录运行则跳过（正式断言在 SimulationTests 的内联镜像上进行）。
            const string dir = "Docs/schemas/examples/m1-scenario/";
            if (!TryReadFile(dir + "actions.json", out var actionsJson) ||
                !TryReadFile(dir + "goals.json", out var goalsJson) ||
                !TryReadFile(dir + "world.json", out var worldJson) ||
                !TryReadFile(dir + "simulation.json", out var simJson))
            {
                Assert.Ignore("工作目录下未找到 M1 场景文件，跳过（应在工程根运行）");
                return;
            }
            Assert.AreEqual(3, ContentLoader.LoadActions(actionsJson).Count);
            Assert.AreEqual(2, ContentLoader.LoadGoals(goalsJson).Count);

            var world = ContentLoader.LoadWorldState(worldJson);
            Assert.AreEqual(0, world.Tick);
            Assert.AreEqual(1, world.Day);

            var cfg = ContentLoader.LoadSimulation(simJson);
            Assert.AreEqual(10, cfg.TicksPerGameHour);
            Assert.AreEqual(1, cfg.PassiveEffects.Count);
            Assert.AreEqual(1, cfg.Agents.Count);
            Assert.AreEqual("n1", cfg.Agents[0].Id);
        }

        [Test]
        public void M2ScenarioFiles_OnDisk_LoadCleanly()
        {
            // M2 多 NPC 场景（2 NPC 分工 + famine 危机）盘上四件套可加载；
            // 运行时行为断言在 SimulationTests 的内联镜像上进行。
            const string dir = "Docs/schemas/examples/m2-scenario/";
            if (!TryReadFile(dir + "actions.json", out var actionsJson) ||
                !TryReadFile(dir + "goals.json", out var goalsJson) ||
                !TryReadFile(dir + "world.json", out var worldJson) ||
                !TryReadFile(dir + "simulation.json", out var simJson))
            {
                Assert.Ignore("工作目录下未找到 M2 场景文件，跳过（应在工程根运行）");
                return;
            }
            Assert.AreEqual(3, ContentLoader.LoadActions(actionsJson).Count, "行动库与 M1 相同");
            Assert.AreEqual(3, ContentLoader.LoadGoals(goalsJson).Count, "stay_alive 共用 + 囤积目标分叉");

            var world = ContentLoader.LoadWorldState(worldJson);
            Assert.AreEqual(0, world.Tick);
            Assert.AreEqual(1, world.Day);
            Assert.AreEqual(2d, world.Get("food"));

            var cfg = ContentLoader.LoadSimulation(simJson);
            Assert.AreEqual(2, cfg.Agents.Count, "双 NPC");
            Assert.AreEqual(2, cfg.PassiveEffects.Count, "两条饥饿被动结算");
            Assert.AreEqual(1, cfg.Crises.Count, "famine 危机在岗");
            Assert.AreEqual("famine", cfg.Crises[0].Id);
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
