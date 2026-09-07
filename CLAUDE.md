# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目是什么

基于 **LLM 规则生成 + GOAP 自主规划** 的双层 AI 模拟经营游戏（类《辐射避难所》）。项目定位为**实验性项目**：不追求可玩性与成品完整度，**可扩展性是架构的第一验收标准**。当前阶段的唯一最终目标是交付一个**最小可玩单元**（范围与验收标准见 `Docs/DESIGN.md` §6）。

**动手前必读 `Docs/DESIGN.md`**——它是权威设计文档，包含架构图、职责铁律、可扩展性原则、DoD 与里程碑。本文只是摘要。

## 常用命令

- **Unity 版本**：2022.3.15f1c1（用 Unity Hub 打开，版本需一致）。
- **编译**：无独立构建脚本；Unity 编辑器打开工程即编译（Assembly-CSharp）。
- **测试**：已安装 Unity Test Framework（`com.unity.test-framework`）。通过编辑器 Window → General → Test Runner 运行 EditMode/PlayMode 测试；CLI 形式供参考：
  ```
  /Applications/Unity/Hub/Editor/2022.3.15f1c1/Unity.app/Contents/MacOS/Unity \
    -projectPath "<项目路径>" -runTests -testPlatform EditMode -testResults results.xml -batchmode
  ```
- 无 lint/CI。本地 git 仓库已初始化（`.gitignore`/`.gitattributes` 为 Unity 标准模板，YAML 资产走 UnityYAMLMerge）；**尚未配置共享远程仓库**——双人接力协作需要双方都能推送（GitHub/Gitee 私有仓库），这是待办事项。

## 双层 AI 架构（大图）

- **LLM 层"规则制定者"**（低频：每游戏日一次 + 事件驱动）：生成规则配置 JSON、每日世界脚本（天气）、NPC 人格；将玩家宏观指令翻译为规则调整。
- **规则验证层**（所有 LLM 输出必经）：JSON Schema 校验 → 沙盒模拟 N tick 验证可达性 → 通过才应用，失败回退默认规则集/随机天气表。
- **GOAP 层"行动执行者"**（每 tick）：每个 NPC 独立 A* 规划器，环境变化实时重规划。

**职责铁律（不可违反）**：

1. LLM 只产出结构化 JSON 配置，**永不直接修改游戏状态、永不生成代码**。
2. **GOAP 引擎是唯一的状态修改者**；天气等外部效果只通过预定义效果通道（生产/消耗倍率、行动成本权重）生效。
3. LLM 调用只发生在游戏日边界或事件触发时，**不在帧循环中同步等待 API**；API 失败一律回退内置默认。

## 架构硬约束（源自实验性定位，见 DESIGN.md §4.5）

- **数据驱动**：Action、Goal、规则、NPC 人格、每日世界脚本全部是 JSON 配置；新增内容不改引擎代码。
- **面向接口**：`IAction / IGoal / IWorldState / IPlanner / IRuleProvider / ILLMProvider`，实现可替换。
- **headless 内核是一等公民**：模拟内核必须可脱离 Unity 渲染独立运行与测试；Unity 界面只是可插拔前端。
- **可观测性优先**：事件总线 + 全量行为日志，每次规划/规则变更/LLM 调用都留痕。
- **可复现**：固定随机种子 + 记录并缓存全部 LLM 请求/响应。

## 协作模式：接力式开发

两人空闲时间不固定，**不设固定分工**，谁有空谁推进（机制见 DESIGN.md §4.4）。要求：任务粒度小且有明确完成定义；工作开始前读、结束前更新 `HANDOFF.md`（M1 时建立：当前状态/进行中/已决策/下一步）；重大设计决策落 `Docs/DESIGN.md`，不留在对话里。

## 当前状态

- 处于 **M1（GOAP 内核）动工前**：`Assets/script/` 仅有一个模板占位 `Test.cs`，核心接口与 World State JSON Schema 尚未定义。
- 脚本目录约定为 `Assets/script/`（沿用现有命名）。
- 设计决策变更时同步更新 `Docs/DESIGN.md` 版本号与对应章节。
