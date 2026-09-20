using System;
using System.Collections.Generic;
using Vibe.Core.Contracts;

namespace Vibe.Core
{
    /// <summary>一个 NPC 在一个 tick 内的行为汇报（Simulation 据此写行为日志）。</summary>
    internal readonly struct AgentTickReport
    {
        /// <summary>本 tick 是否发生了重规划（含原因）。</summary>
        public readonly bool Replanned;
        public readonly string ReplanGoalId;   // 重规划命中的目标 id；未重规划为 null
        public readonly int ReplanSteps;       // 新计划步数
        public readonly float ReplanCost;      // 新计划总代价
        public readonly bool ReplannedFromInvalidation; // true = 因计划耗尽/前提失效/规则变更触发

        /// <summary>执行的行动 id；闲置为 null。</summary>
        public readonly string ExecutedActionId;
        /// <summary>闲置原因（"goal_met"=最高可达目标已满足 / "unplannable"=全部目标不可达）；执行时为 null。</summary>
        public readonly string IdleReason;

        public AgentTickReport(bool replanned, string replanGoalId, int replanSteps, float replanCost,
            bool replannedFromInvalidation, string executedActionId, string idleReason)
        {
            Replanned = replanned;
            ReplanGoalId = replanGoalId;
            ReplanSteps = replanSteps;
            ReplanCost = replanCost;
            ReplannedFromInvalidation = replannedFromInvalidation;
            ExecutedActionId = executedActionId;
            IdleReason = idleReason;
        }
    }

    /// <summary>
    /// 单个 NPC 的 GOAP 代理：持有该 NPC 的目标与当前计划，实现 GOAP.md §3 的每 tick 循环——
    /// 无计划 / 计划耗尽 / 下一步前提失效 / 规则变更（经 Simulation 置空计划）时重规划，
    /// 否则执行计划中的下一个行动。
    ///
    /// 键投影（action.schema.json 所述「单 NPC 规划时的键投影/代入是规划侧职责」的落地）：
    /// 规划与前提判定在「投影视图」上进行——共享资源裸键原样可见、本 NPC 的 npc.&lt;id&gt;.&lt;key&gt;
    /// 投影为裸键 &lt;key&gt;（仅限 localKeys 声明过的键，同名时个体键覆盖共享键）、其他键不可见；
    /// 效果写回时反向映射：裸键 ∈ localKeys → npc.&lt;id&gt;.&lt;key&gt;，其余（含显式 npc. 前缀）原样落盘。
    /// 注意：效果键未声明进 localKeys 时会写向共享命名空间——属内容配置错误，内容作者负责
    /// （加载器只见单文件，无法做跨文件的键空间校验）。
    ///
    /// 效果结算经 <see cref="IRuleSet"/> 倍率通道（DESIGN.md §4.3）：Add 且正量 × 生产倍率、
    /// Add 且负量 × 消耗倍率（缺键视为 1，负倍率按 0 截断防倒贴），Set / MultiplyBy 不经通道。
    /// </summary>
    public sealed class NpcAgent
    {
        private readonly HashSet<string> _localKeys;

        /// <summary>NPC 标识（世界状态键 npc.&lt;id&gt;.&lt;key&gt; 的 id 段）。</summary>
        public string Id { get; }

        /// <summary>该 NPC 追求的目标（按配置 goalIds 顺序解析；规划时按优先级重排是规划器的事）。</summary>
        public IReadOnlyList<IGoal> Goals { get; }

        /// <summary>个体键清单（裸键名；驱动键投影与效果写回落点）。</summary>
        public IReadOnlyList<string> LocalKeys { get; }

        /// <summary>当前计划服务的目标；无计划为 null（可观测性）。</summary>
        public IGoal CurrentGoal => _plan?.Goal;

        /// <summary>当前计划长度；无计划为 0。</summary>
        public int PlanLength => _plan?.Steps.Count ?? 0;

        /// <summary>当前计划待执行步数（含本 tick 将执行的）。</summary>
        public int PendingSteps => _plan == null ? 0 : _plan.Steps.Count - _pendingIndex;

        private Plan _plan;
        private int _pendingIndex;

        internal NpcAgent(string id, IReadOnlyList<string> localKeys, IReadOnlyList<IGoal> goals)
        {
            Id = id;
            LocalKeys = localKeys;
            Goals = goals;
            _localKeys = new HashSet<string>(localKeys);
        }

        /// <summary>作废当前计划（规则变更等外部触发；下一次 Act 将重规划）。</summary>
        internal void InvalidatePlan()
        {
            _plan = null;
            _pendingIndex = 0;
        }

        /// <summary>
        /// 一个 tick 的行为循环（GOAP.md §3）：先判是否重规划，再执行下一步。
        /// world 会被行动效果（经结算通道）修改；规划发生在 world 的投影副本上，不直接污染。
        /// </summary>
        internal AgentTickReport Act(IWorldState world, IReadOnlyList<IAction> actions,
            IPlanner planner, IRuleProvider rules)
        {
            var view = ProjectView(world);

            bool fromInvalidation = _plan == null || _pendingIndex >= _plan.Steps.Count;
            if (!fromInvalidation && !view.Meets(_plan.Steps[_pendingIndex].Preconditions))
                fromInvalidation = true; // 剩余行动前提已失效（环境变了）→ 实时重规划

            bool replanned = false;
            string replanGoalId = null;
            int replanSteps = 0;
            float replanCost = 0f;

            if (fromInvalidation)
            {
                _plan = planner.Plan(view, Goals, actions, rules);
                _pendingIndex = 0;
                replanned = true;

                if (_plan == null)
                    return new AgentTickReport(true, null, 0, 0f, true, null, "unplannable");
                if (_plan.Steps.Count == 0)
                {
                    // 全部目标已满足（空步计划）：本 tick 闲置且不留存计划——
                    // 被动结算每 tick 都在改变世界，下一 tick 重新感知
                    string metGoalId = _plan.Goal.Id;
                    _plan = null;
                    return new AgentTickReport(true, metGoalId, 0, 0f, true, null, "goal_met");
                }
                replanGoalId = _plan.Goal.Id;
                replanSteps = _plan.Steps.Count;
                replanCost = _plan.TotalCost;
            }

            var step = _plan.Steps[_pendingIndex++];
            ApplySettledEffects(world, step.Effects, rules);
            return new AgentTickReport(replanned, replanGoalId, replanSteps, replanCost,
                fromInvalidation, step.Id, null);
        }

        /// <summary>
        /// 构建本 NPC 的规划视图：裸键原样、npc.&lt;id&gt;.&lt;key&gt;（key ∈ localKeys）投影为裸键、
        /// 其余（其他 NPC 的键、未声明的个体键）不可见。快照语义，与真实状态互不影响。
        /// </summary>
        private IWorldState ProjectView(IWorldState world)
        {
            string prefix = "npc." + Id + ".";
            var view = new Dictionary<string, double>();
            foreach (var key in world.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var suffix = key.Substring(prefix.Length);
                    if (_localKeys.Contains(suffix))
                        view[suffix] = world.Get(key);
                }
                else
                {
                    view[key] = world.Get(key);
                }
            }
            return new WorldState(world.Tick, world.Day, view);
        }

        /// <summary>效果写回：裸键 ∈ localKeys 落回个体命名空间，其余原样；Add 正/负量分别过生产/消耗倍率通道。</summary>
        private void ApplySettledEffects(IWorldState world, IReadOnlyList<WorldEffect> effects, IRuleProvider rules)
        {
            var production = rules?.Current?.ProductionMultipliers;
            var consumption = rules?.Current?.ConsumptionMultipliers;

            var settled = new List<WorldEffect>(effects.Count);
            foreach (var e in effects)
            {
                double amount = e.Amount;
                if (e.Op == EffectOp.Add && amount != 0d)
                {
                    var channel = amount > 0d ? production : consumption;
                    float mult = 1f;
                    if (channel != null && channel.TryGetValue(e.Key, out var m)) mult = m;
                    if (mult < 0f) mult = 0f; // 与 GoapPlanner 的代价乘数同款防御：非正倍率兜底
                    amount *= mult;
                }
                settled.Add(new WorldEffect(MapKey(e.Key), e.Op, amount));
            }
            world.Apply(settled);
        }

        private string MapKey(string key) =>
            _localKeys.Contains(key) ? "npc." + Id + "." + key : key;
    }
}
