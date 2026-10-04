namespace Vibe.Core.Contracts
{
    /// <summary>事件类型（事件契约 v1，DESIGN.md §4.4）。append-only 演进：新增成员属兼容扩展。</summary>
    public enum SimEventType
    {
        /// <summary>游戏日开始（日界对齐绝对 tick，Simulation.Step 推进时间戳时发出）。</summary>
        DayBegin,

        /// <summary>全部 NPC 计划整体作废（当前唯一触发：规则变更；下次行动时各自重规划）。</summary>
        PlansInvalidated,

        /// <summary>某 NPC 完成一次<b>成功的</b>重规划（得到非空计划；空计划/失败见 AgentIdle）。</summary>
        AgentReplanned,

        /// <summary>某 NPC 执行了计划中的一个行动（效果已结算落盘）。</summary>
        AgentExecuted,

        /// <summary>某 NPC 本 tick 闲置（原因见 <see cref="IdleReason"/>）。</summary>
        AgentIdle,

        /// <summary>危机进入在场状态（条件由满足转为全满足的边沿；M3 据此驱动 LLM 规则调整）。</summary>
        CrisisTriggered,

        /// <summary>危机解除（在场 → 离场边沿；再次进入会重新 Triggered）。</summary>
        CrisisResolved
    }

    /// <summary>闲置原因。None 仅作非闲置事件的占位默认值。</summary>
    public enum IdleReason
    {
        /// <summary>非闲置事件（AgentReplanned / AgentExecuted 等）的占位值。</summary>
        None,

        /// <summary>最高优先级的可达目标已满足（GoalId 为该目标 id）——无事可做。</summary>
        GoalMet,

        /// <summary>存在未满足目标但预算内均不可达（重规划失败）。</summary>
        Unplannable
    }

    /// <summary>
    /// 一条结构化模拟事件（事件契约 v1，DESIGN.md §4.4）：可观测性的统一载体，取代裸字符串日志
    /// 作为留痕事实源——每次规划、每次执行、每次闲置、日界与计划作废均以事件留痕
    /// （DESIGN.md §4.5「可观测性优先」；M2 危机检测、M3 LLM 触发、M4 表现层都消费事件流）。
    ///
    /// 扁平只读结构（同 <see cref="WorldCondition"/>/<see cref="WorldEffect"/> 家族风格）：
    /// 字段按事件类型部分适用（如 AgentId 仅 Agent* 事件有值，其余 null），无继承体系，
    /// 平铺可序列化、可直接等值比较（确定性回放对照，DESIGN.md §4.3）。
    /// 演进策略 append-only：新增事件类型/新增字段默认兼容；已冻结字段的语义变更须升版本。
    /// </summary>
    public readonly struct SimEvent
    {
        /// <summary>事件发生时的绝对 tick（事件属于哪个 tick 即记哪个，日界事件记新 tick）。</summary>
        public int Tick { get; }

        /// <summary>事件发生时的游戏日。</summary>
        public int Day { get; }

        /// <summary>事件类型（决定哪些字段有值）。</summary>
        public SimEventType Type { get; }

        /// <summary>行为主体 NPC id；仅 Agent* 事件有值，其余 null。</summary>
        public string AgentId { get; }

        /// <summary>目标 id；AgentReplanned（新计划服务的目标）与 GoalMet 闲置（已满足的目标）有值，其余 null。</summary>
        public string GoalId { get; }

        /// <summary>行动 id；仅 AgentExecuted 有值，其余 null。</summary>
        public string ActionId { get; }

        /// <summary>危机 id；仅 CrisisTriggered / CrisisResolved 有值，其余 null。</summary>
        public string CrisisId { get; }

        /// <summary>新计划步数；仅 AgentReplanned 有意义。</summary>
        public int PlanSteps { get; }

        /// <summary>新计划总有效代价；仅 AgentReplanned 有意义。</summary>
        public float PlanCost { get; }

        /// <summary>闲置原因；仅 AgentIdle 有意义。</summary>
        public IdleReason Idle { get; }

        private SimEvent(int tick, int day, SimEventType type,
            string agentId, string goalId, string actionId, string crisisId,
            int planSteps, float planCost, IdleReason idle)
        {
            Tick = tick;
            Day = day;
            Type = type;
            AgentId = agentId;
            GoalId = goalId;
            ActionId = actionId;
            CrisisId = crisisId;
            PlanSteps = planSteps;
            PlanCost = planCost;
            Idle = idle;
        }

        /// <summary>惯用糖：游戏日开始。</summary>
        public static SimEvent DayBegan(int tick, int day)
            => new SimEvent(tick, day, SimEventType.DayBegin, null, null, null, null, 0, 0f, IdleReason.None);

        /// <summary>惯用糖：规则变更导致全部计划作废。</summary>
        public static SimEvent PlansInvalidated(int tick, int day)
            => new SimEvent(tick, day, SimEventType.PlansInvalidated, null, null, null, null, 0, 0f, IdleReason.None);

        /// <summary>惯用糖：重规划成功（新计划服务的目标、步数、总代价）。</summary>
        public static SimEvent Replanned(int tick, int day, string agentId, string goalId,
            int planSteps, float planCost)
            => new SimEvent(tick, day, SimEventType.AgentReplanned, agentId, goalId, null, null,
                planSteps, planCost, IdleReason.None);

        /// <summary>惯用糖：执行了一个行动（效果已结算）。</summary>
        public static SimEvent Executed(int tick, int day, string agentId, string actionId)
            => new SimEvent(tick, day, SimEventType.AgentExecuted, agentId, null, actionId, null, 0, 0f,
                IdleReason.None);

        /// <summary>惯用糖：闲置（GoalMet 时 goalId 为已满足的目标；Unplannable 时为 null）。</summary>
        public static SimEvent Idled(int tick, int day, string agentId, IdleReason reason, string goalId = null)
            => new SimEvent(tick, day, SimEventType.AgentIdle, agentId, goalId, null, null, 0, 0f, reason);

        /// <summary>惯用糖：危机进入在场状态（边沿触发，持续在场不重发）。</summary>
        public static SimEvent CrisisEntered(int tick, int day, string crisisId)
            => new SimEvent(tick, day, SimEventType.CrisisTriggered, null, null, null, crisisId, 0, 0f,
                IdleReason.None);

        /// <summary>惯用糖：危机解除（再次进入会重新 Triggered）。</summary>
        public static SimEvent CrisisLeft(int tick, int day, string crisisId)
            => new SimEvent(tick, day, SimEventType.CrisisResolved, null, null, null, crisisId, 0, 0f,
                IdleReason.None);
    }
}
