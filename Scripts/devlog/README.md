# 每日开发日志（自动）

系统每晚 **21:40** 用 `launchd` 调用 `claude -p --model glm-5.3-flash` 无头扫描仓库，生成 `Docs/devlog/YYYY-MM-DD.md` 并**单独提交**（消息 `devlog: 日期`，只含日志文件）。Claude Code 没开也照跑；Mac 睡着时错过会在唤醒后补跑（launchd 特性）。

日志是**编年史**（每天发生了什么）；`HANDOFF.md` 是**工作内存**（现在进行到哪）。两者互补，不互相替代。

## 文件

| 文件 | 作用 |
|---|---|
| `devlog.sh` | 主脚本：幂等跳过 → claude 无头执行 → pathspec 单独提交日志 |
| `PROMPT.md` | 日志撰写员的提示词（信息源、格式、硬性规则） |
| `com.vibeproject.devlog.plist` | launchd 配置（仓库内是正本，安装时拷贝出去） |

## 安装（新机器）

```bash
mkdir -p ~/Library/LaunchAgents
cp "Scripts/devlog/com.vibeproject.devlog.plist" ~/Library/LaunchAgents/
chmod +x "Scripts/devlog/devlog.sh"
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.vibeproject.devlog.plist
```

注意：`devlog.sh` 与 plist 里的项目路径是**本机绝对路径**（外置卷 `/Volumes/workspace 1/...`），换机器要同步改这两处。

## 常用操作

```bash
# 手动补跑今天的日志（已存在时用 --force 重写并再提交一次）
Scripts/devlog/devlog.sh --force

# 改运行时间：编辑 ~/Library/LaunchAgents/ 下 plist 的 StartCalendarInterval，然后重载
launchctl bootout gui/$(id -u)/com.vibeproject.devlog
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.vibeproject.devlog.plist

# 卸载
launchctl bootout gui/$(id -u)/com.vibeproject.devlog
rm ~/Library/LaunchAgents/com.vibeproject.devlog.plist

# 查看运行状态 / 排查
launchctl print gui/$(id -u)/com.vibeproject.devlog | head -20
tail -50 ~/Library/Logs/vibe-project-devlog.log   # 脚本与 claude 输出
cat /tmp/vibeproject-devlog-launchd.err            # launchd 层错误
```

## 设计约束（改动前先读）

- 写日志的 agent 只有只读工具 + Write，且提示词规定**只能创建当天日志这一个文件**；提交由脚本用 `git commit -- <日志路径>` pathspec 完成，**永不裹挟工作区其他变更**。
- 幂等：当天日志已存在则直接跳过，不会重复提交。
- claude 调用失败 → 不写文件、不提交、日志留痕（`~/Library/Logs/vibe-project-devlog.log`），第二天照常。
- 本机制属于项目协作工作流，概览见 `Docs/AGENT_COMMON.md` §5。
