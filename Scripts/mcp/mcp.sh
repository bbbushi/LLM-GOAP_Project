#!/bin/bash
# MCP 使用纳管开关（2026-09-08 决策的落地，见 Scripts/mcp/README.md 与 HANDOFF.md）：
# Unity 编辑器桥接（unity-mcp，MCP for Unity 包）默认禁止；仅当任务需要直接操作 Unity
# 编辑器（跑 PlayMode 测试、读 Console、执行编辑器命令）时经用户确认临时开放（on），用完即关（off）。
# 禁令落在"注册层"：不注册服务器 = MCP 工具不存在，对所有走注册表的客户端（交互会话 / headless
# claude -p）天然生效；devlog 自动化的 --allowedTools 白名单本就不含 mcp__ 工具，双保险。
# 开关：Scripts/mcp/mcp.sh on|off|status（--scope local 注册，每机自管，不入库）

set -euo pipefail

export PATH="$HOME/.local/bin:$HOME/.claude/local:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# 项目根 = 脚本所在仓库根（Scripts/mcp/ 的上两级），自定位；claude mcp 的 local scope 按 cwd 归属项目，须在根目录执行
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

SERVER_NAME="UnityMCP"     # 与 MCP for Unity 包内 Configurator 的注册名一致
PACKAGE="mcp-for-unity"    # PyPI 包名，经 uvx 拉起（与包内 stdio 注册命令一致）

is_registered() { claude mcp get "$SERVER_NAME" >/dev/null 2>&1; }

case "${1:-status}" in
  on)
    if ! command -v uvx >/dev/null 2>&1; then
      echo "错误：未找到 uvx（uv）。先安装（brew install uv）再用 on；" >&2
      echo "或改在 Unity 编辑器 Window → MCP For Unity 窗口里 Configure（可选 HTTP 传输）。" >&2
      exit 1
    fi
    if is_registered; then
      echo "$SERVER_NAME 已注册，无需重复 on。当前注册："
      claude mcp get "$SERVER_NAME"
      echo "（若是旧注册，先 off 再 on）"
      exit 0
    fi
    cd "$ROOT"
    UVX="$(command -v uvx)"
    if [ "${2:-}" = "--http" ]; then
      # HTTP 直连：URL 以 Unity 编辑器 MCP 窗口显示的当前端点为准（端口可能随占用漂移，勿写死）
      URL="${3:?用法: mcp.sh on --http <url>}"
      claude mcp add --scope local --transport http "$SERVER_NAME" "$URL"
    else
      claude mcp add --scope local --transport stdio "$SERVER_NAME" -- "$UVX" "$PACKAGE"
    fi
    echo "已注册（local scope）。提醒：用完即关（Scripts/mcp/mcp.sh off）。"
    exit 0 ;;
  off)
    REMOVED=0
    for scope in local user project; do
      # 包内 Unregister 同款清理：各 scope 都试一遍，防陈旧注册残留；不存在的 scope 自然失败，忽略
      if claude mcp remove --scope "$scope" "$SERVER_NAME" >/dev/null 2>&1; then
        echo "已移除 $SERVER_NAME（scope: $scope）"
        REMOVED=1
      fi
    done
    [ "$REMOVED" -eq 0 ] && echo "$SERVER_NAME 本就未注册（各 scope 均无），保持默认禁止状态"
    exit 0 ;;
  status)
    echo "== MCP 纳管状态（规则：默认禁止，临时开放，用完即关）=="
    if is_registered; then
      echo "${SERVER_NAME}：已注册（非默认状态，确认任务仍需要它；用完 off）"
      claude mcp get "$SERVER_NAME" 2>/dev/null | sed 's/^/    /'
    else
      echo "${SERVER_NAME}：未注册（默认状态，符合纳管规则）"
    fi
    if [ -f "$ROOT/.mcp.json" ]; then
      echo "注意：项目根存在 .mcp.json（project scope 随仓库分发，含 MCP 配置时违反纳管规则）"
    fi
    if command -v uvx >/dev/null 2>&1; then
      echo "uvx：$(command -v uvx)（stdio 注册可用）"
    else
      echo "uvx：未找到（on 前需装 uv，或改在 Unity 编辑器内配置 HTTP）"
    fi
    exit 0 ;;
  *)
    echo "用法: Scripts/mcp/mcp.sh on|off|status  （on --http <url> 为可选 HTTP 直连）" >&2
    exit 2 ;;
esac
