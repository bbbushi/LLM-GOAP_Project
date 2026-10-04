# HANDOFF

> 接力交接文档：工作开始前先读这里，结束前（/checkpoint 时）更新这里。

## 当前状态

**M1（GOAP 内核）达成，M2（多 NPC 生存闭环）进行中——卡①事件总线、卡②危机检测、卡③多 NPC 场景完成**。卡③（DoD-1 预演）：`Docs/schemas/examples/m2-scenario/` 四件套（行动库与 M1 相同，**竞争/分工全靠目标分叉**——n1 囤食 food≥4、n2 囤木 wood≥4），2 NPC + famine 危机自主运转 3 游戏日（720 tick）以精确行为计数通过：完全分工（n1 gather=24/chop=0，n2 chop=4/gather=0）、同 tick 双吃的直接竞争（各 eat=11，相位同步）、采集者供养全队的协作与资源守恒（2+24−22=4）、无饿死（unplannable=0，饥饿峰值 ≤60）、famine 在岗 0 误报（谷值 ≥2）、事件流确定性成立。此前的卡①②：事件契约 v1（`Contracts/SimEvent.cs` 七类事件，Events 全量留存 + Emitted 推送 + Log 子集投影）；危机检测（`crises[]` 可选增补，tick 末求值、边沿触发，M1+饥荒跑出 3 轮精确触发/解除）。已冻结契约：核心接口、World State v1、Action/Goal 配置 v1、Simulation 配置 v1（含 crises 增补）、事件契约 v1（登记见 `Docs/DESIGN.md` §4.4，v0.10）。内核构成：`Assets/script/Core/` 下 Contracts（六接口 + IRuleSet + SimEvent）、WorldState、Config（MiniJson + ContentLoader 四种加载器）、Planner（GoapPlanner + internal BinaryHeap）、Sim（Simulation tick 引擎 + NpcAgent，键投影与三类倍率结算通道、事件发射、危机检测）。M1 场景：`Docs/schemas/examples/m1-scenario/`（未加 crises，M1 验收内容不动）。单测共 172 项全绿（WorldState 32 + MiniJson 22 + Action/Goal 加载 22 + Simulation 配置加载 27 + Planner 33 + Simulation 36），双通道验证一致：headless mono 反射跑器 + Unity Test Runner（EditMode，正式留档 `Docs/test-reports/2026-10-04-editmode.xml`；跑法见 `Docs/AGENT_COMMON.md` §7）。`Assets/script/Test.cs` 仍为模板占位。协作基础设施：README、`Docs/AGENT_COMMON.md`、/checkpoint、/supervise 监管小组（首次审查已关闭，报告 `Docs/reviews/2026-09-10-design-handoff.md`）、每日 devlog（`Scripts/devlog/`；09-18 起因外置卷挂载名变化断更 17 天，10-04 修复）、MCP 纳管开关（`Scripts/mcp/`）、共享远程（提交后即 push）。

## 进行中

（无）——可选留档项已了结：Unity Test Runner（EditMode）158/158 与 headless mono 反射跑器一致，正式留档 `Docs/test-reports/2026-10-04-editmode.xml`。

注意：每日日志的定时任务装在本机 `~/Library/LaunchAgents/`（不入库）；新机器协作需按 `Scripts/devlog/README.md` 安装。

## 已决策

- **多 NPC 竞争/分工靠目标分叉，不扩引擎**（2026-10-04，M2 卡③）：行动库零新增（与 M1 完全相同），n1/n2 的 goalIds 指向不同囤积目标（stock_food / stock_wood）即涌现完全分工；共享 food + 双方 stay_alive 产生同 tick 双吃的直接竞争；采集者囤积目标被队友进食持续拉低 → 持续补货 = 协作。多 NPC 的内核机制（agents 顺序结算、后者见前者效果）M1 卡⑤已就绪，本卡只做内容与验收。场景落 `Docs/schemas/examples/m2-scenario/`；famine(food≤1) 配置在岗但协作良好时不触发（谷值 ≥2）——「监测在岗不误报」是合理语义，危机触发路径由卡②饥荒循环测试覆盖。
- **危机检测语义：tick 末求值 + 边沿触发 + 全局观察者**（2026-10-04，M2 卡②）：① 阈值配置进 `simulation.schema.json` 可选 `crises[{id, conditions}]`（内容化，同 passiveEffects 先例；条件形状与行动/目标条件完全一致，六算子；读完整键空间含 npc.\<id\>.\<key\>，**不做 NPC 投影**——危机检测是全局观察者）；② 求值点在每 tick 末（全部 NPC 行动后）——tick 中段瞬时恶化若被同 tick 行动补回不算危机（求值的是定局）；③ 边沿触发状态机：进入在场发 CrisisTriggered、持续在场不重发、解除发 CrisisResolved、再次进入可重触发（M3 据触发事件驱动 LLM，不该每 tick 被轰）；④ 多危机同 tick 在场按配置序发事件（确定性）；⑤ 向后兼容增补不升 schema 版本（同事件契约 append-only 精神，老内容不受影响），DESIGN §4.4 两行登记同步。已验证：M1 场景 + food≤2 饥荒危机跑出 3 轮精确触发/解除。
- **事件契约 v1 冻结：结构化事件流取代字符串日志成为留痕事实源**（2026-10-04，M2 卡①）：① SimEvent 扁平只读结构（tick/day/type + 按类型适用的可选字段，同 WorldCondition/WorldEffect 家族风格——平铺可序列化、可等值比较，无继承体系）；卡① 五类 + 卡② 增补 CrisisTriggered / CrisisResolved 与 CrisisId 字段（现共七类）。② 载体双通道：`Simulation.Events` 全量留存（不丢、可回放对照）+ `Emitted` 同步推送（观察者只读、单线程语义）；③ 行为日志 `Log` 降为事件流的人类可读子集投影，与事件在同一处代码同源生成防分叉（M1 的 150 项断言原样通过即格式兼容证明）；④ 演进 append-only：新增事件类型/字段属兼容扩展（LLM 调用事件 M3），已冻结字段语义变更须升版本。登记：DESIGN.md §4.4。
- **MCP 纳管开关落地**（2026-10-04，关闭 09-08 TODO）：禁令落注册层——不注册服务器 = MCP 工具对所有走注册表的客户端（交互会话 / headless `claude -p`）天然不存在；devlog 自动化的 `--allowedTools` 白名单不含 mcp__ 工具，双保险。开关 `Scripts/mcp/mcp.sh on|off|status`：local scope 每机自管、`.mcp.json` 保持不存在（status 检查）；默认 stdio（`uvx mcp-for-unity`，与包内 ClaudeCodeConfigurator 生成命令一致），`on --http <url>` 可选（端口随占用漂移，勿写死）；off 按包内 Unregister 惯例清全部 scope 防残留。规则正文入 `AGENT_COMMON.md` §4，自述见 `Scripts/mcp/README.md`。现状：默认未注册；`on` 仅经用户确认后执行。
- **每日日志脚本自定位项目根**（2026-10-04）：断更根因是外置卷挂载名变化（`/Volumes/workspace 1` → `/Volumes/workspace`）致 launchd 找不到程序——launchd 层失败在脚本侧**无任何日志痕迹**，`status` 的「上次运行」日期停滞即信号。devlog.sh 改为从脚本位置自定位项目根（卷名不再入代码），plist 程序路径仍需本机绝对路径（换机器/卷名变化时同步改并重载）。
- **Simulation 配置 v1 冻结 + 运行时语义**（2026-09-20，M1 卡⑤）：① 生存压力（饥饿上升等）是内容不是代码——每 tick 被动结算效果进 `simulation.schema.json` 的 passiveEffects，直接落盘不经倍率通道（通道只作用于行动效果结算，语义归 IRuleSet）；② 键投影按 agents[].localKeys 声明执行：规划视图 npc.\<id\>.\<key\>→裸键、效果写回落回个体命名空间（action.schema.json 把投影职责指给规划侧，此为落地）；③ 每 tick 固定次序：推进时间戳→被动结算→规则变更失效→NPC 依次行动→危机检测（⑤为 M2 卡②增补，见上条）；④ M1 场景内容在 `Docs/schemas/examples/m1-scenario/`。登记：DESIGN.md §4.4。
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

1. **M2 收尾：游戏日推进调度**（DESIGN.md §6.4 最后一张卡）：日界已有（DayBegin 事件 + 留痕），缺的是 M3 每日 LLM 调用的正式挂载点（如日界回调/调度接口，保证「LLM 只在日界/事件触发时被调」可被引擎结构强制而非约定）。注意：内核/设计级任务，模型路由要求 glm-5.3 档（AGENT_COMMON §6）。完成后 M2 达成，可对照 DoD-1 复核。
2. **M3 开工前置**（DESIGN.md §6.4 跨里程碑软依赖）：危机触发规则调整依赖危机检测（✓ 卡②）、每日世界脚本依赖游戏日调度（上一条）；另 DoD-2 的人为食物危机制造可用 m2-scenario + 调低初始 food / 加大被动消耗来构造。
