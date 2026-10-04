using System;
using System.Collections.Generic;
using Vibe.Core.Config;
using Vibe.Core.Contracts;

namespace Vibe.Core
{
    /// <summary>
    /// headless 模拟内核（DESIGN.md §4.2「模拟内核」）：tick 循环 + 资源结算 + NPC 调度。
    /// 每 tick 的固定次序：推进时间戳（日界时发射 DayBegin 事件，随后调用日界挂载点
    /// <see cref="DayScheduler"/>）→ 被动结算（饥饿上升等，配置于 simulation.schema.json v1，
    /// 直接落盘不经倍率通道）→ 规则变更失效检查 → 各 NPC 依次执行 GOAP 循环（<see cref="NpcAgent"/>）
    /// → 危机检测（<see cref="SimulationConfig.Crises"/>：对行动后的完整世界状态求值，边沿触发）。
    ///
    /// 时间模型：tick 为最小时间步；游戏小时 / 游戏日由配置换算（ticksPerGameHour × 24 = ticksPerDay），
    /// 日界对齐绝对 tick（ticksPerDay 的整数倍处 day+1）——初始快照的 tick/day 不符合该约定时
    /// 由内容作者负责（M1 场景从 tick 0 起）。每个 tick 重建带新时间戳的世界状态（值拷贝），
    /// 效果不会改动时间戳（IWorldState 契约），重建即推进。
    ///
    /// 可观测性（事件契约 v1，DESIGN.md §4.4）：结构化事件流 <see cref="Events"/> 是留痕事实源——
    /// 每次重规划/执行/闲置、日界、计划作废均发事件、全量留存不丢（DESIGN.md §4.5）；
    /// <see cref="Emitted"/> 同步推送观察者（单线程 tick 内调用，观察者只读、不得改状态，
    /// 异常向上冒泡属 fail-fast）。<see cref="Log"/> 为人类可读的行为日志（M1 起的展示载体，
    /// 内容是事件流的子集投影，两者由同一处代码同源生成）。
    /// 无随机源——同输入同轨迹（可复现，DESIGN.md §4.3）。
    /// 非线程安全：模拟单线程推进。实现 <see cref="IDisposable"/> 以退订规则事件（测试中
    /// 同一 provider 会驱动多个 Simulation 实例，不退订会让旧实例驻留事件委托链）。
    /// </summary>
    public sealed class Simulation : IDisposable
    {
        private readonly IReadOnlyList<IAction> _actions;
        private readonly IPlanner _planner;
        private readonly IRuleProvider _rules;
        private readonly List<NpcAgent> _agents;
        private readonly List<string> _log = new List<string>();
        private readonly List<SimEvent> _events = new List<SimEvent>();
        private readonly HashSet<string> _activeCrises = new HashSet<string>();
        private bool _subscribedRulesChanged;

        private IWorldState _world;
        private bool _rulesDirty;

        /// <summary>模拟运行配置（Docs/schemas/simulation.schema.json v1）。</summary>
        public SimulationConfig Config { get; }

        /// <summary>当前世界状态（时间戳随 tick 推进重建；值由引擎与结算通道修改）。</summary>
        public IWorldState Current => _world;

        /// <summary>当前绝对 tick 数。</summary>
        public int Tick => _world.Tick;

        /// <summary>当前游戏日（从初始日 起，ticksPerDay 的整数倍处 +1）。</summary>
        public int Day => _world.Day;

        /// <summary>当前游戏日内的小时数（0–23）。</summary>
        public int GameHour => (_world.Tick % TicksPerDay) / Config.TicksPerGameHour;

        /// <summary>一个游戏日的 tick 数 = 24 × ticksPerGameHour。</summary>
        public int TicksPerDay => Config.TicksPerGameHour * 24;

        /// <summary>全量行为日志（每 NPC 每 tick 一行；人类可读展示，事实源是 <see cref="Events"/>）。</summary>
        public IReadOnlyList<string> Log => _log;

        /// <summary>全量结构化事件流（事件契约 v1；按发生序留存，不丢、可回放对照）。</summary>
        public IReadOnlyList<SimEvent> Events => _events;

        /// <summary>
        /// 事件推送：每条事件在发生处同步调用（先入 <see cref="Events"/> 再推送，观察者所见即全量序）。
        /// 单线程语义（tick 循环内），观察者只读；异常会中断模拟（fail-fast，与内核整体姿态一致）。
        /// </summary>
        public event Action<SimEvent> Emitted;

        /// <summary>
        /// 游戏日调度挂载点（M2 收尾，<see cref="Contracts.IDayScheduler"/>）：日界且仅日界时
        /// 被引擎调用——紧随 DayBegin 事件、先于被动结算与规则失效检查（实现约定见接口注释）。
        /// 可空（无日界级工作）、可随时替换；只由引擎调用，外部不得直接调用实现或在回调中推进模拟。
        /// </summary>
        public IDayScheduler DayScheduler { get; set; }

        /// <summary>参与模拟的 NPC（按配置顺序）。</summary>
        public IReadOnlyList<NpcAgent> Agents => _agents;

        /// <summary>
        /// 组合一次模拟：初始世界 + 行动库 + 目标库 + 运行配置。
        /// 跨文件引用校验在此执行：每个 agent 的 goalIds 必须存在于目标库（缺失抛
        /// <see cref="ArgumentException"/>——加载器只见单文件，组合时才见全貌）。
        /// planner 省略时用默认 <see cref="GoapPlanner"/>；rules 为 null 表示无规则生效（全通道恒 1）。
        /// </summary>
        public Simulation(IWorldState initialWorld, IReadOnlyList<IAction> actions, IReadOnlyList<IGoal> goals,
            SimulationConfig config, IPlanner planner = null, IRuleProvider rules = null)
        {
            if (initialWorld == null) throw new ArgumentNullException(nameof(initialWorld));
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            if (goals == null) throw new ArgumentNullException(nameof(goals));
            if (config == null) throw new ArgumentNullException(nameof(config));

            var goalsById = new Dictionary<string, IGoal>();
            foreach (var goal in goals)
                goalsById[goal.Id] = goal;

            _world = initialWorld.Clone(); // 不与调用方共享可变状态
            _actions = actions;
            Config = config;
            _planner = planner ?? new GoapPlanner();
            _rules = rules;

            _agents = new List<NpcAgent>(config.Agents.Count);
            foreach (var spec in config.Agents)
            {
                var agentGoals = new List<IGoal>(spec.GoalIds.Count);
                foreach (var goalId in spec.GoalIds)
                {
                    if (!goalsById.TryGetValue(goalId, out var goal))
                        throw new ArgumentException(
                            $"agents[] ('{spec.Id}') 引用的 goalId '{goalId}' 不在目标库中（跨文件引用须一致）",
                            nameof(config));
                    agentGoals.Add(goal);
                }
                _agents.Add(new NpcAgent(spec.Id, spec.LocalKeys, agentGoals));
            }

            if (_rules != null)
            {
                _rules.RulesChanged += OnRulesChanged;
                _subscribedRulesChanged = true;
            }
        }

        private void OnRulesChanged(IRuleSet newRules) => _rulesDirty = true;

        /// <summary>退订规则事件（可多次调用）。</summary>
        public void Dispose()
        {
            if (_subscribedRulesChanged && _rules != null)
            {
                _rules.RulesChanged -= OnRulesChanged;
                _subscribedRulesChanged = false;
            }
        }

        /// <summary>推进 ticks 个 tick（等价于逐次 <see cref="Step"/>）。</summary>
        public void Run(int ticks)
        {
            if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks), "tick 数不能为负");
            for (int i = 0; i < ticks; i++) Step();
        }

        /// <summary>推进一个 tick（固定次序见类注释）。</summary>
        public void Step()
        {
            // 1. 推进时间戳：以当前值重建带新 (tick, day) 的状态；日界对齐绝对 tick
            int nextTick = _world.Tick + 1;
            int nextDay = _world.Day;
            bool dayBegins = nextTick % TicksPerDay == 0;
            if (dayBegins)
            {
                nextDay++;
                _log.Add($"[t={nextTick}] day {nextDay} begins");
                Emit(SimEvent.DayBegan(nextTick, nextDay));
            }
            _world = new WorldState(nextTick, nextDay, SnapshotValues(_world));

            // 1b. 日界挂载点（铁律 3 的结构承载，DESIGN.md §3.2）：引擎唯一的日界级外部调用，
            //     紧随 DayBegin 事件、先于被动结算——回调所见 Current 即新日 0 时、昨日收盘值；
            //     回调内替换规则并触发 RulesChanged 时由本 tick 第 3 步捕获，NPC 当日首个
            //     tick 即按新规则重规划（M3 每日世界脚本由此生效）
            if (dayBegins) DayScheduler?.OnDayBegin(nextDay);

            // 2. 被动结算：直接落盘（完整键名，不经倍率通道——通道只作用于行动效果）
            if (Config.PassiveEffects.Count > 0)
                _world.Apply(Config.PassiveEffects);

            // 3. 规则变更：整体作废各 NPC 计划（下次 Act 触发重规划）
            if (_rulesDirty)
            {
                _rulesDirty = false;
                foreach (var agent in _agents) agent.InvalidatePlan();
                _log.Add($"[t={nextTick}] rules changed → all plans invalidated");
                Emit(SimEvent.PlansInvalidated(nextTick, nextDay));
            }

            // 4. 各 NPC 依次行动（后行动者看到先行动者的效果，配置顺序即结算顺序）
            foreach (var agent in _agents)
            {
                var report = agent.Act(_world, _actions, _planner, _rules);
                _log.Add(FormatLine(nextTick, nextDay, agent.Id, report));
                EmitAgentEvents(nextTick, nextDay, agent.Id, report);
            }

            // 5. 危机检测：对行动后的完整世界状态求值（tick 中段瞬时恶化若被同 tick 行动
            //    补回则不算——求值的是本 tick 的定局）；边沿触发，配置序即事件序（确定性）
            foreach (var crisis in Config.Crises)
            {
                bool present = _world.Meets(crisis.Conditions);
                if (present && _activeCrises.Add(crisis.Id))
                {
                    _log.Add($"[t={nextTick}] crisis {crisis.Id} triggered");
                    Emit(SimEvent.CrisisEntered(nextTick, nextDay, crisis.Id));
                }
                else if (!present && _activeCrises.Remove(crisis.Id))
                {
                    _log.Add($"[t={nextTick}] crisis {crisis.Id} resolved");
                    Emit(SimEvent.CrisisLeft(nextTick, nextDay, crisis.Id));
                }
            }
        }

        /// <summary>单次事件落地：先入全量流，再推送观察者（推送所见即留存序）。</summary>
        private void Emit(SimEvent e)
        {
            _events.Add(e);
            Emitted?.Invoke(e);
        }

        /// <summary>行为汇报 → 事件（与 FormatLine 同源：同一 report 产出日志行与事件，防分叉）。</summary>
        private void EmitAgentEvents(int tick, int day, string agentId, AgentTickReport r)
        {
            if (r.ExecutedActionId != null)
            {
                if (r.Replanned)
                    Emit(SimEvent.Replanned(tick, day, agentId, r.ReplanGoalId, r.ReplanSteps, r.ReplanCost));
                Emit(SimEvent.Executed(tick, day, agentId, r.ExecutedActionId));
                return;
            }
            // 闲置：GoalMet 附已满足的目标 id；Unplannable 重规划失败，无目标可附
            Emit(r.Idle == IdleReason.GoalMet
                ? SimEvent.Idled(tick, day, agentId, IdleReason.GoalMet, r.ReplanGoalId)
                : SimEvent.Idled(tick, day, agentId, IdleReason.Unplannable));
        }

        /// <summary>行为日志行：执行形如「[t=61 d=1 h=6] n1 replan(goal=stay_alive steps=1 cost=1) exec eat」；
        /// 闲置形如「n1 idle(goal_met stay_alive)」/「n1 idle(unplannable)」。</summary>
        private string FormatLine(int tick, int day, string agentId, AgentTickReport r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[t=").Append(tick).Append(" d=").Append(day)
              .Append(" h=").Append(GameHour).Append("] ").Append(agentId);

            if (r.ExecutedActionId != null)
            {
                if (r.Replanned)
                    sb.Append(" replan(goal=").Append(r.ReplanGoalId)
                      .Append(" steps=").Append(r.ReplanSteps)
                      .Append(" cost=").Append(r.ReplanCost.ToString("0.###",
                          System.Globalization.CultureInfo.InvariantCulture)).Append(')');
                sb.Append(" exec ").Append(r.ExecutedActionId);
            }
            else if (r.Idle == IdleReason.GoalMet)
            {
                sb.Append(" idle(goal_met ").Append(r.ReplanGoalId).Append(')');
            }
            else
            {
                sb.Append(" idle(unplannable)");
            }
            return sb.ToString();
        }

        private static Dictionary<string, double> SnapshotValues(IWorldState world)
        {
            var values = new Dictionary<string, double>();
            foreach (var key in world.Keys)
                values[key] = world.Get(key);
            return values;
        }
    }
}
