# MCP 使用纳管开关

Unity 编辑器桥接（`com.coplaydev.unity-mcp`，MCP for Unity）**默认禁止**；仅当任务需要直接操作 Unity 编辑器（跑 PlayMode 测试、读 Console、执行编辑器命令）时，**经用户确认**临时开放，用完即关。决策背景见 `Docs/AGENT_COMMON.md` 与 `HANDOFF.md` 已决策（2026-09-08 立项，2026-10-04 落地）。

## 用法

```bash
Scripts/mcp/mcp.sh status   # 查看注册状态 / uvx 可用性 / .mcp.json 违规检查
Scripts/mcp/mcp.sh on       # 临时开放：注册 UnityMCP（local scope，stdio: uvx mcp-for-unity）
Scripts/mcp/mcp.sh on --http <url>   # 可选：HTTP 直连（URL 以 Unity 编辑器 MCP 窗口显示为准，端口会漂移勿写死）
Scripts/mcp/mcp.sh off      # 用完即关：各 scope（local/user/project）全部移除
```

## 原理：禁令落在注册层

不注册服务器 = MCP 工具在客户端不存在，**对所有走注册表的客户端天然生效**（交互会话、headless `claude -p` 均如此），无需逐个客户端配置黑名单。每日 devlog 自动化的 `--allowedTools` 白名单本就不含 `mcp__` 工具，双保险。

- 注册用 `--scope local`（绑定当前项目目录、存本机用户配置，不入库）——同 devlog 开关的"每机自管"模式；本项目 `.mcp.json` 保持不存在，`status` 会检查这一点。
- 注册命令与包内 `ClaudeCodeConfigurator` 生成的一致（`claude mcp add --scope local --transport stdio UnityMCP -- <uvx> mcp-for-unity`），`off` 同包内 Unregister 惯例清全部 scope 防残留。
- stdio 需 `uv`（`brew install uv`；Unity 编辑器的 MCP 窗口 Configure 也会自动装）。

## 边界

- 本开关只管**注册表**：Cursor 等其他客户端若有各自独立的配置文件，不在管辖范围（本项目当前只有 Claude Code 在用，无此问题）。
- `on` 之后服务器能否连上还取决于 **Unity 编辑器是否开着**（桥接对端是编辑器进程）。
- 排查：`claude mcp list`（全量 + 健康检查）、`claude mcp get UnityMCP`（单项）。
