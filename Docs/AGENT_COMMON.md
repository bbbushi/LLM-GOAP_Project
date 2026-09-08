# AGENT_COMMON — 所有 agent 共用的约束与工作流

> 本文档是本项目全部 AI agent（不限平台：Claude Code、Codex、Cursor 等）共用的约束与工作流**单一事实源**。
> 各平台入口文件（CLAUDE.md、AGENTS.md、.cursorrules 等）只应指向本文件并保留平台特有机制，**禁止把本文档内容复制进入口文件**——约束变更只改这里（接入指引见 §9）。

## 1. 项目是什么

基于 **LLM 规则生成 + GOAP 自主规划** 的双层 AI 模拟经营游戏（类《辐射避难所》）。项目定位为**实验性项目**：不追求可玩性与成品完整度，**可扩展性是架构的第一验收标准**。当前阶段的唯一最终目标是交付一个**最小可玩单元**（范围与验收标准见 `Docs/DESIGN.md` §6）。

**动手前必读：**

- `Docs/DESIGN.md` —— 权威设计文档：架构图、职责铁律、可扩展性原则、DoD 与里程碑。
- `HANDOFF.md` —— 当前进度与交接状态（**项目当前状态以它为准**，本文件不维护状态快照）。工作开始前先读、结束前更新。

## 2. 双层 AI 架构（大图）

- **LLM 层"规则制定者"**（低频：每游戏日一次 + 事件驱动）：生成规则配置 JSON、每日世界脚本（天气）、NPC 人格；将玩家宏观指令翻译为规则调整。
- **规则验证层**（所有 LLM 输出必经）：JSON Schema 校验 → 沙盒模拟 N tick 验证可达性 → 通过才应用，失败回退默认规则集/随机天气表。
- **GOAP 层"行动执行者"**（每 tick）：每个 NPC 独立 A* 规划器，环境变化实时重规划。

架构细节见 `Docs/DESIGN.md` §3。

## 3. 职责铁律（不可违反）

1. LLM 只产出结构化 JSON 配置，**永不直接修改游戏状态、永不生成代码**。
2. **GOAP 引擎是唯一的状态修改者**；天气等外部效果只通过预定义效果通道（生产/消耗倍率、行动成本权重）生效。
3. LLM 调用只发生在游戏日边界或事件触发时，**不在帧循环中同步等待 API**；API 失败一律回退内置默认。

## 4. 架构硬约束（源自实验性定位，见 DESIGN.md §4.5）

- **数据驱动**：Action、Goal、规则、NPC 人格、每日世界脚本全部是 JSON 配置；新增内容不改引擎代码。
- **面向接口**：`IAction / IGoal / IWorldState / IPlanner / IRuleProvider / ILLMProvider`，实现可替换。
- **headless 内核是一等公民**：模拟内核必须可脱离 Unity 渲染独立运行与测试；Unity 界面只是可插拔前端。
- **可观测性优先**：事件总线 + 全量行为日志，每次规划/规则变更/LLM 调用都留痕。
- **可复现**：固定随机种子 + 记录并缓存全部 LLM 请求/响应。

## 5. 协作模式：接力式开发

人与 agent 的空闲时间都不固定，**不设固定分工**，谁有空谁推进（机制见 DESIGN.md §4.4）。要求：

- 任务粒度小且有明确完成定义；
- **工作开始前读、结束前更新 `HANDOFF.md`**（当前状态/进行中/已决策/下一步四个板块）；
- 重大设计决策落 `Docs/DESIGN.md`，不留在对话里。

**检查点工作流**：一段工作结束 = 更新 HANDOFF.md + git 提交（文档与代码同一提交），完整流程定义于 `.claude/skills/checkpoint/SKILL.md`。Claude Code 用 `/checkpoint` 触发；**其他平台的 agent 阅读同一文档、手动执行同一流程**。产生新提交后即 `git push`（origin 见 §7）；push 失败不阻塞本地检查点，恢复后补推。

**每日开发日志**：系统每晚自动用 glm-5.3-flash 无头扫描仓库，生成 `Docs/devlog/YYYY-MM-DD.md` 并单独提交（`devlog: 日期`）。日志是**编年史**，HANDOFF.md 是**工作内存**，两者互补。本机开关：`Scripts/devlog/devlog.sh on|off|status`。机制、安装与约束见 `Scripts/devlog/README.md`。

## 6. 模型路由约束（所有工作者必守）

本项目由不同大模型接力工作（Claude Code 用 `/model` 切换；其他平台用其自身的模型切换机制）。**接到任务先自查当前模型是否匹配任务类型；不匹配就停下来向用户确认并推荐切换，不要硬做。**

| 任务类型 | 应使用的模型 |
|---|---|
| 视觉/UI 工作（拼 UI、布局、UXML/USS、材质贴图、镜头/后处理等） | **GPT-6** |
| 架构设计、技术决策、`Docs/DESIGN.md` 级方案文档 | **glm-5.3**（完整版，非 Flash） |
| 测试编写、说明文档、git 提交/交接等文书工作 | **glm-5.3-flash**（默认） |

执行规则：

1. 当前运行的模型标识见当前平台标示的位置（Claude Code 在系统提示中，形如 "You are powered by the model glm-5.3-flash"）。开工前先对照上表自查。
2. 模型与任务不匹配时：停下说明"当前模型为 X，本任务建议使用 Y（/model 切换）"，等用户确认；用户明确坚持时照做，但需在结果中注明风险。
3. **所有模型选择/切换相关的提示必须使用中文**：停下说明、切换建议、确认提问一律中文表述，无论当前模型的默认输出语言是什么。
4. 调整模型分工时，同步更新本表。

## 7. 常用命令

- **Unity 版本**：2022.3.15f1c1（用 Unity Hub 打开，版本需一致）。
- **编译**：无独立构建脚本；Unity 编辑器打开工程即编译（Assembly-CSharp）。
- **测试**：已安装 Unity Test Framework（`com.unity.test-framework`）。通过编辑器 Window → General → Test Runner 运行 EditMode/PlayMode 测试；CLI 形式供参考：
  ```
  /Applications/Unity/Hub/Editor/2022.3.15f1c1/Unity.app/Contents/MacOS/Unity \
    -projectPath "<项目路径>" -runTests -testPlatform EditMode -testResults results.xml -batchmode
  ```
- 无 lint/CI。本地 git 仓库已初始化（`.gitignore`/`.gitattributes` 为 Unity 标准模板，YAML 资产走 UnityYAMLMerge）。
- **共享远程**：`origin` = `github.com/bbbushi/LLM-GOAP_Project`（私有）。约定：产生新提交后即 push；push 失败不阻塞本地工作。
- **工具脚本**：`Scripts/devlog/`——每日开发日志自动化（开关、安装、约束见其 README）。新增运维类脚本放 `Scripts/<名称>/`，并在此处登记一行。

## 8. 工程约定

- 脚本目录：`Assets/script/`（沿用现有命名）。
- 设计决策变更时同步更新 `Docs/DESIGN.md` 版本号与对应章节。

## 9. 新平台接入指引

接入新的 agent 平台时：

1. 创建该平台的入口文件（如 `AGENTS.md`、`.cursorrules`），内容只需：一句平台定位 + "动手前必读 `Docs/AGENT_COMMON.md` 与 `Docs/DESIGN.md`、读 `HANDOFF.md`" + 该平台特有机制（模型切换方式、专属工具等）。
2. **不要**把本文档的约束内容复制进入口文件；约束变更只改本文档。
3. 平台特有机制（如该平台的 skill/命令目录）随仓库入库，保证协作者可用。
