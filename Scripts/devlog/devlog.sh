#!/bin/bash
# 每日开发日志：系统定时（launchd）每晚调用，用 glm-5.3-flash 无头扫描仓库，
# 生成 Docs/devlog/YYYY-MM-DD.md 并单独提交（消息 "devlog: 日期"）。
# 开关：Scripts/devlog/devlog.sh on|off|status（本机生效，重启保持）
# 安装/改时间/卸载见同目录 README.md。人工补跑：devlog.sh --force（无视开关/幂等/无变更跳过）

set -euo pipefail

# launchd 不继承用户 shell 的 PATH，显式补齐 claude 所在位置
export PATH="$HOME/.local/bin:$HOME/.claude/local:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"
# glm-5.3-flash 不在 Claude Code 模型目录中，禁用未知模型上下文窗口强制检查，消除每次运行的警告
export CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT=1

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
    LAST_RUN="$(grep 'RESULT:' "$LOG_FILE" 2>/dev/null | tail -1 | sed 's/^\[[^]]*\] //' || true)"
    if [ -n "$LAST_RUN" ]; then echo "上次运行：$LAST_RUN"; else echo "上次运行：无记录"; fi
    exit 0 ;;
esac

log "==== devlog run start (${1:-scheduled}) ===="

# 外置卷未挂载/项目不在时静默跳过，不报错打扰系统
if [ ! -d "$PROJECT_DIR/.git" ]; then
  log "project dir not found (volume unmounted?), skip"
  log "RESULT: skipped (project not found)"
  exit 0
fi

# 开关关闭则直接跳过（--force 为人工显式补跑，无视开关）
if [ -f "$FLAG_FILE" ] && [ "${1:-}" != "--force" ]; then
  log "disabled by flag file, skip"
  log "RESULT: skipped (disabled)"
  exit 0
fi

# 幂等：今日日志已存在则跳过；--force 可重跑（会重写并再提交一次）
if [ -f "$TARGET" ] && [ "${1:-}" != "--force" ]; then
  log "today's log already exists, skip"
  log "RESULT: skipped (already exists)"
  exit 0
fi

# 无变更日跳过：上个日志点之后没有新提交、工作区也无变更 → 不调用模型、不生成、不提交
LAST_DEVLOG="$(git -C "$PROJECT_DIR" log --format=%ad --date=iso -1 -- Docs/devlog 2>/dev/null || true)"
HAS_COMMITS=0
if [ -z "$LAST_DEVLOG" ]; then
  HAS_COMMITS=1   # 尚无任何日志（首次运行）默认视为有变更
elif [ -n "$(git -C "$PROJECT_DIR" log --oneline --since="$LAST_DEVLOG" -- ':(exclude)Docs/devlog' 2>/dev/null | head -1)" ]; then
  HAS_COMMITS=1
fi
HAS_DIRTY=0
if [ -n "$(git -C "$PROJECT_DIR" status --porcelain)" ]; then HAS_DIRTY=1; fi
if [ "$HAS_COMMITS" -eq 0 ] && [ "$HAS_DIRTY" -eq 0 ] && [ "${1:-}" != "--force" ]; then
  log "no commits/changes since last devlog, skip"
  log "RESULT: skipped (no changes)"
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
  log "RESULT: FAILED (claude -p error)"
  exit 1
fi

if [ -f "$TARGET" ]; then
  # add + pathspec 提交：即使文件是新建的（未跟踪）也能提交，且只提交日志文件，绝不裹挟工作区其他变更
  git add "$TARGET"
  git commit -m "devlog: $TODAY" -- "Docs/devlog/$TODAY.md" >> "$LOG_FILE" 2>&1 \
    && { log "committed devlog $TODAY"; log "RESULT: OK (devlog $TODAY committed)"; } \
    || { log "WARN: commit failed (see git output above)"; log "RESULT: FAILED (commit error)"; }
else
  log "WARN: claude did not create $TARGET"
  log "RESULT: FAILED (log file not created)"
  exit 1
fi
