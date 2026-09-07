# HANDOFF

> 接力交接文档：工作开始前先读这里，结束前（/checkpoint 时）更新这里。

## 当前状态

M1（GOAP 内核）动工前：核心接口与 World State JSON Schema 尚未定义，`Assets/script/` 仅有模板占位 `Test.cs`。2026-09-08 起项目启用 /checkpoint 检查点工作流（见"已决策"）。

## 进行中

无未完成的代码工作。另有一处未提交变更待确认：`Packages/manifest.json` / `packages-lock.json`（本地安装 MCP For Unity 包引入，注意其中是本机绝对路径引用，见"下一步"第 1 条）。

## 已决策

- **采用 /checkpoint 检查点工作流**（2026-09-08）：完整检查点 = 更新本文件 + 代码同一提交；轻量打点 = 仅提交。skill 位于 `.claude/skills/checkpoint/`，随仓库分发。理由：接力协作、双方空闲时间不固定，检查点是唯一的进度固化手段。
- **`.claude/settings.local.json` 不入库**：个人本地权限配置；`.claude/skills/` 正常入库。
- 检查点不做测试/编译验证（用户选择），但明知破坏编译时会先提醒。

## 下一步

1. 处理 MCP 包变更：`manifest.json` 中的 `file:D:/Unity/...` 是本机绝对路径，直接提交会让协作者无法解析——推送共享远程前需要改成双方都可用的安装方式（或暂时不提交该文件）。
2. 开始 M1：定义核心接口（IAction / IGoal / IWorldState / IPlanner / IRuleProvider / ILLMProvider）与 World State JSON Schema（范围见 `Docs/DESIGN.md` §6）。
3. 待办：配置共享远程仓库（GitHub/Gitee 私有），当前本地分支已关联 origin 但协作流程未定。
