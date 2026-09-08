# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 本文件是什么

Claude Code 的入口文件。项目约束与工作流的单一事实源在 `Docs/AGENT_COMMON.md`，本文件只保留指针、红线摘要与 Claude Code 特有机制。**动手前必读：**

- `Docs/AGENT_COMMON.md` —— 所有 agent 共用的约束与工作流（架构铁律、协作模式、模型路由表、常用命令都在这里）
- `Docs/DESIGN.md` —— 权威设计文档
- `HANDOFF.md` —— 当前进度（接手先读，收尾更新）

## 红线（完整版见 AGENT_COMMON.md §3/§4；修改红线须两处同步）

1. LLM 只产结构化 JSON，**永不直接修改游戏状态、永不生成代码**。
2. **GOAP 引擎是唯一的状态修改者**。
3. LLM 调用不进帧循环；API 失败一律回退内置默认。
4. 新增内容靠 JSON 配置，不改引擎代码。

## Claude Code 特有

- `/model` 切换模型（任务→模型路由表见 AGENT_COMMON.md §6；模型切换相关提示必须用中文）。
- `/checkpoint` 检查点工作流（skill 位于 `.claude/skills/checkpoint/`）：一段工作结束 = 更新 HANDOFF.md + git 提交。
