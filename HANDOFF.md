# HANDOFF

> 接力交接文档：工作开始前先读这里，结束前（/checkpoint 时）更新这里。

## 当前状态

M1（GOAP 内核）动工前：核心接口与 World State JSON Schema 尚未定义，`Assets/script/` 仅有模板占位 `Test.cs`。协作基础设施已就绪：项目 README（门面与文档地图）、公共约束文档（`Docs/AGENT_COMMON.md`）、/checkpoint 检查点、每日开发日志自动化（`Scripts/devlog/`）、共享远程（origin = GitHub，产生提交后即 push）。

## 进行中

无未完成的代码工作。

注意：每日日志的定时任务装在本机 `~/Library/LaunchAgents/`（不入库）；新机器协作需按 `Scripts/devlog/README.md` 安装。

## 已决策

- **表现层 UI 选型：UI Toolkit**（2026-09-09）：本项目 UI 全为可观测性面板（行为日志/资源曲线/决策面板/指令输入），UI Toolkit 是该场景的设计目标（ListView 虚拟化、无 Canvas Rebuild）；UXML/USS 纯文本资产可 diff、可由 AI agent 可靠生成，契合接力协作。UGUI 仅在世界空间 UI 等 Toolkit 覆盖不到处局部引入（两者可共存）。完整理由与代价兜底见 `Docs/DESIGN.md` §4.1/§4.3。
- **模型路由补充：glm-5.2 可替代 glm-5.3**（2026-09-08）：表中要求 glm-5.3 的任务（架构设计、技术决策等）用 glm-5.2 接手同样算匹配，无需停下确认；Flash 版与完整版仍不互相替代。落点：`Docs/AGENT_COMMON.md` §6 路由表下方注释。
- **产生提交后即 push**（2026-09-08）：origin（github.com/bbbushi/LLM-GOAP_Project，私有，曾用名 LLM-GOA_Project）可推送，取代早期"远程未配置、只做本地提交"的设定；push 失败不阻塞本地检查点。项目此前仅存于本机外置卷，push 同时消除单点丢失风险。
- **约束/工作流剥离为 `Docs/AGENT_COMMON.md` 单一事实源**（2026-09-08）：多平台 agent 接力协作，各平台入口文件只写指针 + 极简红线，禁止复制内容，防文档分叉；CLAUDE.md 已瘦身为接入范例（含"模型切换提示必须中文"规则）。
- **每日开发日志自动化**（2026-09-08）：launchd 每晚 21:40 用 glm-5.3-flash 无头扫描仓库，生成 `Docs/devlog/YYYY-MM-DD.md`（编年史）并单独提交 `devlog: 日期`，与 HANDOFF.md（工作内存）互补；含本机 `on|off|status` 开关（off=标志文件跳过，不卸载任务，每机自管不入库）。无变更日自动跳过；每次运行以 `RESULT:` 留痕，`status` 可查上次结果。机制与安装见 `Scripts/devlog/README.md`，概览见 AGENT_COMMON.md §5。
- **采用 /checkpoint 检查点工作流**（2026-09-08）：完整检查点 = 更新本文件 + 代码同一提交；轻量打点 = 仅提交。skill 位于 `.claude/skills/checkpoint/`，随仓库分发。
- **`.claude/settings.local.json` 不入库**：个人本地权限配置；`.claude/skills/` 正常入库。
- 检查点不做测试/编译验证（用户选择），但明知破坏编译时会先提醒。

## 下一步

1. 开始 M1：定义核心接口（IAction / IGoal / IWorldState / IPlanner / IRuleProvider / ILLMProvider）与 World State JSON Schema（范围见 `Docs/DESIGN.md` §6）。
2. 处理 `Packages/manifest.json` 的 `file:D:/Unity/...` 本机绝对路径（已随 d11d4a3 入库且已在 origin 上）：改为双方可解析的引用方式后提交推送，协作者才能打开工程。
3. 【TODO，2026-09-08】MCP 使用纳管：默认禁止 MCP（Unity 编辑器桥接），仅当任务需要直接操作 Unity 编辑器（跑 PlayMode 测试、读 Console、执行编辑器命令）时经用户确认临时开放，用完即关。设想：禁令落在"注册层"——不注册服务器 = MCP 工具不存在，对所有 agent（交互会话/headless/其他平台）天然禁止；做成 `Scripts/` 下 `on|off|status` 开关（同 devlog 模式，local scope 每机自管）。现状：无任何已注册 MCP 服务器、`.mcp.json` 不存在、`com.coplaydev.unity-mcp` 在 manifest.json 指向 Windows 本机路径（本机不可用）。实现前置：第 2 条的 manifest 路径问题。注：每日 devlog 自动化的 `--allowedTools` 白名单不含 mcp__ 工具，自动化天然免疫，无需处理。
