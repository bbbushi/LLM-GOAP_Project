# HANDOFF

> 接力交接文档：工作开始前先读这里，结束前（/checkpoint 时）更新这里。

## 当前状态

M1（GOAP 内核）任务卡 ①–④ 完成：核心接口（六接口 + 伴生 IRuleSet）与 World State JSON Schema v1 已冻结，WorldState 默认实现完成；Action/Goal 配置 Schema v1（`Docs/schemas/action.schema.json`、`goal.schema.json` + 示例）已冻结并实现零依赖加载器（`Assets/script/Core/Config/`），契约登记见 `Docs/DESIGN.md` §4.4；卡 ④ A* Planner 正式实现落地 `Assets/script/Core/Planner/`（GoapPlanner：二叉堆 open 集 + GetFingerprint 签名去重 + 迭代/深度双预算 + 规则倍率通道——GOAP.md §4 教学版三简化点的正式解法；伴生 internal BinaryHeap，经 AssemblyInfo 的 InternalsVisibleTo 直测）。单测共 108 项全绿（WorldState 32 + MiniJson 22 + 配置加载 22 + Planner 32，`Assets/script/Tests/Editor/`；headless mono 反射跑器验证，编辑器 Test Runner 正式留档仍待做，见进行中）。语义基线沿用 `Docs/GOAP.md`（缺失键视为 0、前提 ≥、效果 +=）。`Assets/script/Test.cs` 仍为模板占位。协作基础设施：README、`Docs/AGENT_COMMON.md`、/checkpoint、/supervise 监管小组（首次审查已关闭，报告 `Docs/reviews/2026-09-10-design-handoff.md`）、每日 devlog（`Scripts/devlog/`）、共享远程（提交后即 push）；`Docs/DESIGN.md` 现 v0.7。

## 进行中

- （可选留档，非阻塞）在 Unity Test Runner（EditMode）跑一遍 Vibe.Core.Tests——headless mono 反射跑器已验证 108/108 全绿（本机 mcs `-langversion:latest` 编译 + 项目 PackageCache 的 nunit.framework.dll，dll 需复制到测试 dll 同目录供 mono 解析；反射跑器须同时匹配 [Test] 与 [TestCase]——WorldStateTests 有 12 个参数化用例），此步仅为正式化记录。

注意：每日日志的定时任务装在本机 `~/Library/LaunchAgents/`（不入库）；新机器协作需按 `Scripts/devlog/README.md` 安装。

## 已决策

- **GoapPlanner 实现语义三则**（2026-09-20）：① 目标在起点已满足 → 返回空步计划（TotalCost=0，"无事可做"），不落向次优先级目标；② 搜索预算按「每目标一次完整搜索」计，不跨目标分摊；③ 规则乘数缺键 / 规则为 null 视为 1，负乘数截断为 0（A* 边权非负的防御兜底）。确定性：同优先级目标按输入序、堆内同 F 节点按入堆序 → 同输入同计划（可复现要求）。落点：`Assets/script/Core/Planner/GoapPlanner.cs` 类注释。
- **配置解析零依赖：自带 MiniJson，不引 Newtonsoft/JsonUtility**（2026-09-13）：JsonUtility 绑 Unity 无法 headless，Newtonsoft 引包破坏内核 `noEngineReferences` 且给 headless mono 跑测添 dll 解析负担；`Vibe.Core.Config.Json.MiniJson`（严格文法 + 行列定位报错）约 300 行，换来内核零依赖。加载器逐条镜像 Schema 约束做防御校验（未知字段/未知算子/重复 id/负代价显式报错，绝不静默忽略——防 LLM 生成配置时字段拼写错误被吞）。Schema 与加载器是同一契约的两处执行，改契约须两处同步（DESIGN.md §4.4）。
- **设立监管小组（/supervise）**（2026-09-10）：AI 产出的文档/计划按需接受三成员（一致性/合理性/红线合规）独立并行审查，只报告不修改；模型按审查对象分档（设计级 → glm-5.3，文书级 → flash）。落点：`.claude/skills/supervise/`、`.claude/agents/`、`Docs/AGENT_COMMON.md` §5/§6/§8，报告落 `Docs/reviews/`。
- **M1 核心契约形态**（2026-09-09）：World State v1 与核心接口（含伴生 IRuleSet）冻结；LLM 影响收敛于三类倍率通道，LLM 失败以 Success=false 回退，内核 noEngineReferences。登记与要点见 `Docs/DESIGN.md` §4.4 冻结契约登记；事实源：`Docs/schemas/`、`Assets/script/Core/Contracts/`。
- **MCP 包内嵌入库**（2026-09-09）：`com.coplaydev.unity-mcp` 10.2.0 以 Unity 内嵌包形式入库（`Packages/com.coplaydev.unity-mcp/`，5.5 MB），manifest.json 不再引用本机绝对路径（原为 `file:/Volumes/Tool/...`，更早为 `file:D:/Unity/...`），任何协作方 clone 后即可打开工程。在「移除依赖」与「嵌入保留功能」之间选择了后者。
- **表现层 UI 选型：UI Toolkit**（2026-09-09）：本项目 UI 全为可观测性面板（行为日志/资源曲线/决策面板/指令输入），UI Toolkit 是该场景的设计目标（ListView 虚拟化、无 Canvas Rebuild）；UXML/USS 纯文本资产可 diff、可由 AI agent 可靠生成，契合接力协作。UGUI 仅在世界空间 UI 等 Toolkit 覆盖不到处局部引入（两者可共存）。完整理由与代价兜底见 `Docs/DESIGN.md` §4.1/§4.3。
- **模型路由补充：glm-5.2 可替代 glm-5.3**（2026-09-08）：表中要求 glm-5.3 的任务（架构设计、技术决策等）用 glm-5.2 接手同样算匹配，无需停下确认；Flash 版与完整版仍不互相替代。落点：`Docs/AGENT_COMMON.md` §6 路由表下方注释。
- **产生提交后即 push**（2026-09-08）：origin（github.com/bbbushi/LLM-GOAP_Project，私有）可用，消除本机单点丢失风险；push 失败不阻塞检查点，恢复后补推。
- **约束/工作流剥离为 `Docs/AGENT_COMMON.md` 单一事实源**（2026-09-08）：平台入口只写指针、禁止复制约束正文，防文档分叉。
- **每日开发日志自动化**（2026-09-08）：launchd 每晚 21:40 用 glm-5.3-flash 无头生成 `Docs/devlog/`（编年史）并单独提交，与 HANDOFF.md 互补；每机自管开关。机制与安装见 `Scripts/devlog/README.md`。
- **采用 /checkpoint 检查点工作流**（2026-09-08）：完整 = 更新本文件 + 同一提交，轻量 = 仅提交；skill 随仓库分发。
- **`.claude/settings.local.json` 不入库**：个人本地权限配置；`.claude/skills/` 正常入库。
- 检查点不做测试/编译验证（用户选择），但明知破坏编译时会先提醒。

## 下一步

1. M1 收官任务卡（卡号只增不复用，①–④ 已完成见当前状态）：
   - ⑤ tick 循环与资源结算：1 NPC、3 Action、2 Goal 自主生存 24 游戏小时（M1 验收；示例内容可直接取 `Docs/schemas/examples/` 的 3 Action + 2 Goal 扩展成正式 Config，规划器已就绪可端到端串联）。
2. 【TODO，2026-09-08】MCP 使用纳管：默认禁止 MCP（Unity 编辑器桥接），仅当任务需要直接操作 Unity 编辑器（跑 PlayMode 测试、读 Console、执行编辑器命令）时经用户确认临时开放，用完即关。设想：禁令落在"注册层"——不注册服务器 = MCP 工具不存在，对所有 agent（交互会话/headless/其他平台）天然禁止；做成 `Scripts/` 下 `on|off|status` 开关（同 devlog 模式，local scope 每机自管）。现状：无任何已注册 MCP 服务器、`.mcp.json` 不存在；`com.coplaydev.unity-mcp` 已内嵌入库（见已决策·MCP 包内嵌入库），编辑器插件随处可用。实现前置（原 manifest 本机路径问题）已解决。注：每日 devlog 自动化的 `--allowedTools` 白名单不含 mcp__ 工具，自动化天然免疫，无需处理。
