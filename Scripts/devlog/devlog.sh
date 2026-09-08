#!/bin/bash
# 每日开发日志：系统定时（launchd）每晚调用，用 glm-5.3-flash 无头扫描仓库，
# 生成 Docs/devlog/YYYY-MM-DD.md 并单独提交（消息 "devlog: 日期"）。
# 开关：Scripts/devlog/devlog.sh on|off|status（本机生效，重启保持）
# 安装/改时间/卸载见同目录 README.md。人工补跑：devlog.sh --force（无视开关与当日幂等检查）

set -euo pipefail

# launchd 不继承用户 shell 的 PATH，显式补齐 claude 所在位置
export PATH="$HOME/.local/bin:$HOME/.claude/local:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"

PROJECT_DIR="/Volumes/workspace 1/UnityProject/vibe Project"   # 本机绝对路径，换机器需同步改 plist 与此处
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TODAY="$(date +%F)"
DEVLOG_DIR="$PROJECT_DIR/Docs/devlog"
TARGET="$DEVLOG_DIR/$TODAY.md"
LOG_FILE="$HOME/Library/Logs/vibe-project-devlog.log"
FLAG_FILE="$SCRIPT_DIR/.disabled"   # 开关标志（已 gitignore，本机状态不入库）

log() { echo "[$(date '+%F %T')] $*" >> "$LOG_FILE"; }

# ---- 开关管理（on/off/status 不走扫描流程）----
case "${1:-}" in
  on)  rm -f "$FLAG_FILE"; echo "每日开发日志：已开启"; exit 0 ;;
  off) touch "$FLAG_FILE"; echo "每日开发日志：已关闭（launchd 到点直接跳过，不产生任何调用）"; exit 0 ;;
  status)
    if [ -f "$FLAG_FILE" ]; then echo "开关：关闭"; else echo "开关：开启"; fi
    if [ -f "$TARGET" ]; then echo "今日日志：已存在"; else echo "今日日志：未生成"; fi
    if launchctl print "gui/$(id -u)/com.vibeproject.devlog" >/dev/null 2>&1; then
      echo "launchd 任务：已装载（计划每晚 21:40）"
    else
      echo "launchd 任务：未装载（安装见 README.md）"
    fi
    exit 0 ;;
esac

# 外置卷未挂载/项目不在时静默跳过，不报错打扰系统
if [ ! -d "$PROJECT_DIR/.git" ]; then
  log "==== devlog run skipped: project dir not found (volume unmounted?) ===="
  exit 0
fi

# 开关关闭则直接跳过（--force 为人工显式补跑，无视开关）
if [ -f "$FLAG_FILE" ] && [ "${1:-}" != "--force" ]; then
  log "==== devlog run skipped: disabled by flag file ===="
  exit 0
fi

log "==== devlog run start (${1:-scheduled}) ===="

# 幂等：今日日志已存在则跳过；--force 可重跑（会重写并再提交一次）
if [ -f "$TARGET" ] && [ "${1:-}" != "--force" ]; then
  log "today's log already exists, skip"
  exit 0
fi

mkdir -p "$DEVLOG_DIR"
cd "$PROJECT_DIR"

if ! claude -p "$(cat "$SCRIPT_DIR/PROMPT.md")

今天的日期：${TODAY}。目标文件：Docs/devlog/${TODAY}.md。" \
    --model glm-5.3-flash \
    --permission-mode acceptEdits \
    --allowedTools "Read,Glob,Grep,Write,Bash(git log:*),Bash(git diff:*),Bash(git status:*),Bash(git show:*)" \
    >> "$LOG_FILE" 2>&1; then
  log "ERROR: claude -p failed, no log written, no commit"
  exit 1
fi

if [ -f "$TARGET" ]; then
  # add + pathspec 提交：即使文件是新建的（未跟踪）也能提交，且只提交日志文件，绝不裹挟工作区其他变更
  git add "$TARGET"
  git commit -m "devlog: $TODAY" -- "Docs/devlog/$TODAY.md" >> "$LOG_FILE" 2>&1 \
    && log "committed devlog $TODAY" \
    || log "WARN: commit failed (see git output above)"
else
  log "WARN: claude did not create $TARGET"
  exit 1
fi
