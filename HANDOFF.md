# HANDOFF

> 接力交接文档：工作开始前先读这里，结束前（/checkpoint 时）更新这里。

## 当前状态

**M1（GOAP 内核）达成，M2（多 NPC 生存闭环）已开工——卡①事件总线完成**。M1 验收（1 NPC、3 Action、2 Goal 自主生存 24 游戏小时）以精确行为计数通过；M2 卡①：结构化事件契约 v1（`Contracts/SimEvent.cs`，五类事件 + append-only 演进）落地——`Simulation.Events` 全量留存为留痕事实源、`Emitted` 同步推送、`Log` 降为人类可读子集投影（同源生成防分叉），危机检测（M2 卡②）与 M3 LLM 触发将消费事件流。已冻结契约：核心接口、World State v1、Action/Goal 配置 v1、Simulation 配置 v1、事件契约 v1（登记见 `Docs/DESIGN.md` §4.4，v0.9）。内核构成：`Assets/script/Core/` 下 Contracts（六接口 + IRuleSet + SimEvent）、WorldState、Config（MiniJson + ContentLoader 四种加载器）、Planner（GoapPlanner + internal BinaryHeap）、Sim（Simulation tick 引擎 + NpcAgent，键投影与三类倍率结算通道、事件发射）。M1 场景内容：`Docs/schemas/examples/m1-scenario/` 四件套。单测共 158 项全绿（WorldState 32 + MiniJson 22 + Action/Goal 加载 22 + Simulation 配置加载 20 + Planner 33 + Simulation 29（含事件总线 8 项）），双通道验证一致：headless mono 反射跑器 + Unity Test Runner（EditMode，正式留档 `Docs/test-reports/2026-10-04-editmode.xml`；跑法见 `Docs/AGENT_COMMON.md` §7）。`Assets/script/Test.cs` 仍为模板占位。协作基础设施：README、`Docs/AGENT_COMMON.md`、/checkpoint、/supervise 监管小组（首次审查已关闭，报告 `Docs/reviews/2026-09-10-design-handoff.md`）、每日 devlog（`Scripts/devlog/`；09-18 起因外置卷挂载名变化断更 17 天，10-04 修复）、MCP 纳管开关（`Scripts/mcp/`）、共享远程（提交后即 push）。

## 进行中

（无）——可选留档项已了结：Unity Test Runner（EditMode）158/158 与 headless mono 反射跑器一致，正式留档 `Docs/test-reports/2026-10-04-editmode.xml`。

注意：每日日志的定时任务装在本机 `~/Library/LaunchAgents/`（不入库）；新机器协作需按 `Scripts/devlog/README.md` 安装。

## 已决策

- **事件契约 v1 冻结：结构化事件流取代字符串日志成为留痕事实源**（2026-10-04，M2 卡①）：① SimEvent 扁平只读结构（tick/day/type + 按类型适用的可选字段，同 WorldCondition/WorldEffect 家族风格——平铺可序列化、可等值比较，无继承体系）；五类事件 DayBegin / PlansInvalidated / AgentReplanned / AgentExecuted / AgentIdle。② 载体双通道：`Simulation.Events` 全量留存（不丢、可回放对照）+ `Emitted` 同步推送（观察者只读、单线程语义）；③ 行为日志 `Log` 降为事件流的人类可读子集投影，与事件在同一处代码同源生成防分叉（M1 的 150 项断言原样通过即格式兼容证明）；④ 演进 append-only：新增事件类型/字段属兼容扩展（危机事件 M2 卡②、LLM 调用事件 M3），已冻结字段语义变更须升版本。登记：DESIGN.md §4.4。
- **MCP 纳管开关落地**（2026-10-04，关闭 09-08 TODO）：禁令落注册层——不注册服务器 = MCP 工具对所有走注册表的客户端（交互会话 / headless `claude -p`）天然不存在；devlog 自动化的 `--allowedTools` 白名单不含 mcp__ 工具，双保险。开关 `Scripts/mcp/mcp.sh on|off|status`：local scope 每机自管、`.mcp.json` 保持不存在（status 检查）；默认 stdio（`uvx mcp-for-unity`，与包内 ClaudeCodeConfigurator 生成命令一致），`on --http <url>` 可选（端口随占用漂移，勿写死）；off 按包内 Unregister 惯例清全部 scope 防残留。规则正文入 `AGENT_COMMON.md` §4，自述见 `Scripts/mcp/README.md`。现状：默认未注册；`on` 仅经用户确认后执行。
- **每日日志脚本自定位项目根**（2026-10-04）：断更根因是外置卷挂载名变化（`/Volumes/workspace 1` → `/Volumes/workspace`）致 launchd 找不到程序——launchd 层失败在脚本侧**无任何日志痕迹**，`status` 的「上次运行」日期停滞即信号。devlog.sh 改为从脚本位置自定位项目根（卷名不再入代码），plist 程序路径仍需本机绝对路径（换机器/卷名变化时同步改并重载）。
- **Simulation 配置 v1 冻结 + 运行时语义**（2026-09-20，M1 卡⑤）：① 生存压力（饥饿上升等）是内容不是代码——每 tick 被动结算效果进 `simulation.schema.json` 的 passiveEffects，直接落盘不经倍率通道（通道只作用于行动效果结算，语义归 IRuleSet）；② 键投影按 agents[].localKeys 声明执行：规划视图 npc.\<id\>.\<key\>→裸键、效果写回落回个体命名空间（action.schema.json 把投影职责指给规划侧，此为落地）；③ 每 tick 固定次序：推进时间戳→被动结算→规则变更失效→NPC 依次行动；④ M1 场景内容在 `Docs/schemas/examples/m1-scenario/`。登记：DESIGN.md §4.4。
- **GoapPlanner 目标选择修订：已满足的目标跳过，不返回空计划**（2026-09-20，修订卡④当日决策）：原「起点已满足→空计划、不落向次优先级」会产生目标遮蔽——已满足的高优先级目标永久压住低优先级目标（M1 场景 stay_alive 满足时 stock_up 永不执行，饥饿失控）。改为经典 GOAP 语义：只在未满足目标中按优先级取首个可达者；全部满足→最高优先级目标的空步计划（idle goal_met）；空目标列表→null。其余语义不变：预算按每目标一次完整搜索；乘数缺键/null 视为 1、负值截断 0；同优先级按输入序、同 F 按入堆序（确定性）。落点：`GoapPlanner.cs`、`IPlanner.cs` 接口注释已同步。
- **GoapPlanner 实现语义**（2026-09-20，卡④；① 已于同日修订，见上条）：② 搜索预算按「每目标一次完整搜索」计，不跨目标分摊；③ 规则乘数缺键 / 规则为 null 视为 1，负乘数截断为 0（A* 边权非负的防御兜底）。
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

1. **M2 卡②：危机检测**（DESIGN.md §6.4 / §6.1 DoD-2 前置）：资源阈值判定 → 危机事件（ResourceCrisis，事件契约 append-only 扩展）。现成基础：事件流已结构化（危机检测的输入与输出都有了）；实现方向待定——阈值配置进 simulation.json（内容化，同 passiveEffects 先例）还是独立危机配置文件，动手前先定。注意：内核/设计级任务，模型路由要求 glm-5.3 档（AGENT_COMMON §6）。
2. M2 其余任务卡（DESIGN.md §6.4）：多 NPC 资源竞争/协作（结构已就绪：agents 顺序结算有测试）、游戏日推进调度。
