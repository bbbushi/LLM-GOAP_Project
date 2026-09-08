# HANDOFF

> 接力交接文档：工作开始前先读这里，结束前（/checkpoint 时）更新这里。

## 当前状态

M1（GOAP 内核）动工前：核心接口与 World State JSON Schema 尚未定义，`Assets/script/` 仅有模板占位 `Test.cs`。协作基础设施已就绪：公共约束文档（`Docs/AGENT_COMMON.md`）、/checkpoint 检查点、每日开发日志自动化（`Scripts/devlog/`）。

## 进行中

无未完成的代码工作。

注意：每日日志的定时任务装在本机 `~/Library/LaunchAgents/`（不入库）；新机器协作需按 `Scripts/devlog/README.md` 安装。

## 已决策

- **约束/工作流剥离为 `Docs/AGENT_COMMON.md` 单一事实源**（2026-09-08）：多平台 agent 接力协作，各平台入口文件只写指针 + 极简红线，禁止复制内容，防文档分叉；CLAUDE.md 已瘦身为接入范例（含"模型切换提示必须中文"规则）。
- **每日开发日志自动化**（2026-09-08）：launchd 每晚 21:40 用 glm-5.3-flash 无头扫描仓库，生成 `Docs/devlog/YYYY-MM-DD.md`（编年史）并单独提交 `devlog: 日期`，与 HANDOFF.md（工作内存）互补。机制与安装见 `Scripts/devlog/README.md`，概览见 AGENT_COMMON.md §5。
- **采用 /checkpoint 检查点工作流**（2026-09-08）：完整检查点 = 更新本文件 + 代码同一提交；轻量打点 = 仅提交。skill 位于 `.claude/skills/checkpoint/`，随仓库分发。
- **`.claude/settings.local.json` 不入库**：个人本地权限配置；`.claude/skills/` 正常入库。
- 检查点不做测试/编译验证（用户选择），但明知破坏编译时会先提醒。

## 下一步

1. 开始 M1：定义核心接口（IAction / IGoal / IWorldState / IPlanner / IRuleProvider / ILLMProvider）与 World State JSON Schema（范围见 `Docs/DESIGN.md` §6）。
2. 推送共享远程前处理 `Packages/manifest.json` 的 `file:D:/Unity/...` 本机绝对路径（已随 d11d4a3 入库，协作者无法解析）。
3. 待办：配置共享远程仓库（GitHub/Gitee 私有），当前本地分支已关联 origin 但协作流程未定。
