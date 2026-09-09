# HANDOFF

> 接力交接文档：工作开始前先读这里，结束前（/checkpoint 时）更新这里。

## 当前状态

M1（GOAP 内核）第一张任务卡完成：六大核心接口（IAction / IGoal / IWorldState / IPlanner / IRuleProvider / ILLMProvider）与 World State JSON Schema v1 已定义冻结。代码位于 `Assets/script/Core/Contracts/`，程序集 `Vibe.Core`（asmdef `noEngineReferences: true`——纯 C#、编译层面禁止引用 UnityEngine，落实 headless 铁律）；Schema 位于 `Docs/schemas/world-state.schema.json`（附 `examples/` 示例）。语义基线沿用 `Docs/GOAP.md`（缺失键视为 0、前提 ≥、效果 +=），算子做了受控泛化（见已决策 2026-09-09）。`Assets/script/Test.cs` 仍为模板占位。协作基础设施已就绪：项目 README、`Docs/AGENT_COMMON.md`、/checkpoint 检查点、每日开发日志（`Scripts/devlog/`）、共享远程（origin = GitHub，产生提交后即 push）。

## 进行中

- 待验证：用 Unity 打开工程，确认 Vibe.Core 编译通过、生成 `.meta` 后补提交推送（本次提交尚未含 .meta，Unity 打开时会自动生成）。

注意：每日日志的定时任务装在本机 `~/Library/LaunchAgents/`（不入库）；新机器协作需按 `Scripts/devlog/README.md` 安装。

## 已决策

- **M1 核心契约形态**（2026-09-09）：① 世界状态 = 纯数值平面键值对（缺失键视为 0），NPC 键用 `npc.<id>.<key>` 前缀，天气不入世界状态（只走规则通道）；② 条件 = (key, 算子, 阈值)，六种数值算子（≥ 为基线），效果 = Set/Add/MultiplyBy 三算子（+= 为基线）——相对 GOAP.md 教学版的 ≥/+= 纯基线受控泛化，避免冻结后返工；③ 规则集 = 三类倍率通道（行动成本/生产/消耗），LLM 影响全部收敛于此；④ LLM 失败不抛异常，以 Success=false 携带 Error 供上层回退；⑤ 内核程序集 `noEngineReferences: true`。落点：`Assets/script/Core/Contracts/`、`Docs/schemas/world-state.schema.json`（version:1 冻结）。
- **MCP 包内嵌入库**（2026-09-09）：`com.coplaydev.unity-mcp` 10.2.0 以 Unity 内嵌包形式入库（`Packages/com.coplaydev.unity-mcp/`，5.5 MB），manifest.json 不再引用本机绝对路径（原为 `file:/Volumes/Tool/...`，更早为 `file:D:/Unity/...`），任何协作方 clone 后即可打开工程。在「移除依赖」与「嵌入保留功能」之间选择了后者。
- **表现层 UI 选型：UI Toolkit**（2026-09-09）：本项目 UI 全为可观测性面板（行为日志/资源曲线/决策面板/指令输入），UI Toolkit 是该场景的设计目标（ListView 虚拟化、无 Canvas Rebuild）；UXML/USS 纯文本资产可 diff、可由 AI agent 可靠生成，契合接力协作。UGUI 仅在世界空间 UI 等 Toolkit 覆盖不到处局部引入（两者可共存）。完整理由与代价兜底见 `Docs/DESIGN.md` §4.1/§4.3。
- **模型路由补充：glm-5.2 可替代 glm-5.3**（2026-09-08）：表中要求 glm-5.3 的任务（架构设计、技术决策等）用 glm-5.2 接手同样算匹配，无需停下确认；Flash 版与完整版仍不互相替代。落点：`Docs/AGENT_COMMON.md` §6 路由表下方注释。
- **产生提交后即 push**（2026-09-08）：origin（github.com/bbbushi/LLM-GOAP_Project，私有，曾用名 LLM-GOA_Project）可推送，取代早期"远程未配置、只做本地提交"的设定；push 失败不阻塞本地检查点。项目此前仅存于本机外置卷，push 同时消除单点丢失风险。
- **约束/工作流剥离为 `Docs/AGENT_COMMON.md` 单一事实源**（2026-09-08）：多平台 agent 接力协作，各平台入口文件只写指针 + 极简红线，禁止复制内容，防文档分叉；CLAUDE.md 已瘦身为接入范例（含"模型切换提示必须中文"规则）。
- **每日开发日志自动化**（2026-09-08）：launchd 每晚 21:40 用 glm-5.3-flash 无头扫描仓库，生成 `Docs/devlog/YYYY-MM-DD.md`（编年史）并单独提交 `devlog: 日期`，与 HANDOFF.md（工作内存）互补；含本机 `on|off|status` 开关（off=标志文件跳过，不卸载任务，每机自管不入库）。无变更日自动跳过；每次运行以 `RESULT:` 留痕，`status` 可查上次结果。机制与安装见 `Scripts/devlog/README.md`，概览见 AGENT_COMMON.md §5。
- **采用 /checkpoint 检查点工作流**（2026-09-08）：完整检查点 = 更新本文件 + 代码同一提交；轻量打点 = 仅提交。skill 位于 `.claude/skills/checkpoint/`，随仓库分发。
- **`.claude/settings.local.json` 不入库**：个人本地权限配置；`.claude/skills/` 正常入库。
- 检查点不做测试/编译验证（用户选择），但明知破坏编译时会先提醒。

## 下一步

1. 用 Unity 打开工程：验证 Vibe.Core 编译、生成 `.meta`，补提交推送（见"进行中"）。
2. M1 后续任务卡（按序推进，粒度可再拆）：
   - ① WorldState 默认实现（Get/Set/Meets/Apply/Clone/Fingerprint）+ EditMode 单测；
   - ② Action/Goal 的 JSON 配置 Schema（`Docs/schemas/`）与加载器；
   - ③ A* Planner 正式实现（二叉堆优先队列 + 签名去重 + 迭代/深度预算）+ 单测；
   - ④ tick 循环与资源结算：1 NPC、3 Action、2 Goal 自主生存 24 游戏小时（M1 验收）。
3. 【TODO，2026-09-08】MCP 使用纳管：默认禁止 MCP（Unity 编辑器桥接），仅当任务需要直接操作 Unity 编辑器（跑 PlayMode 测试、读 Console、执行编辑器命令）时经用户确认临时开放，用完即关。设想：禁令落在"注册层"——不注册服务器 = MCP 工具不存在，对所有 agent（交互会话/headless/其他平台）天然禁止；做成 `Scripts/` 下 `on|off|status` 开关（同 devlog 模式，local scope 每机自管）。现状：无任何已注册 MCP 服务器、`.mcp.json` 不存在；`com.coplaydev.unity-mcp` 已内嵌入库（见已决策 2026-09-09），编辑器插件随处可用。实现前置（原 manifest 本机路径问题）已解决。注：每日 devlog 自动化的 `--allowedTools` 白名单不含 mcp__ 工具，自动化天然免疫，无需处理。
