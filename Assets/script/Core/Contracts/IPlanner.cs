using System.Collections.Generic;

namespace Vibe.Core.Contracts
{
    /// <summary>一次规划的结果：命中的目标、行动序列与总有效代价。不可变。</summary>
    public sealed class Plan
    {
        /// <summary>本次计划所服务的目标。</summary>
        public IGoal Goal { get; }

        /// <summary>按执行顺序排列的行动序列。</summary>
        public IReadOnlyList<IAction> Steps { get; }

        /// <summary>有效总代价（已含规则乘数），规划器追求其最小。</summary>
        public float TotalCost { get; }

        public Plan(IGoal goal, IReadOnlyList<IAction> steps, float totalCost)
        {
            Goal = goal;
            Steps = steps;
            TotalCost = totalCost;
        }
    }

    /// <summary>
    /// GOAP 规划器：在隐式状态图上做 A* 搜索（节点 = 世界状态，边 = 前提满足的行动，
    /// 启发值 = 目标中尚未满足的条件个数，详见 Docs/GOAP.md §3）。
    /// 无状态服务：每次调用相互独立，规则变化由调用方触发重规划。
    /// 正式实现必须自带迭代预算上限、状态签名去重（Fingerprint）与搜索深度上限，
    /// 防止组合爆炸（GOAP.md §4 教学版简化点 3）。
    /// </summary>
    public interface IPlanner
    {
        /// <summary>
        /// 从 current 出发，为「优先级最高的可达目标」规划总代价最低的行动序列。
        /// 目标按 Priority 降序逐一尝试，<b>已满足的目标跳过不占调度</b>（防高优先级目标
        /// 永久遮蔽低优先级目标）；全部满足时返回最高优先级目标的空步计划（TotalCost=0）。
        /// 行动有效代价 = BaseCost × rules 提供的乘数（天气等外部影响即经此通道改变计划，DESIGN.md §4.3）。
        /// 返回 null 表示存在未满足目标但预算内均不可达。
        /// </summary>
        Plan Plan(IWorldState current, IReadOnlyList<IGoal> goals, IReadOnlyList<IAction> actions, IRuleProvider rules);
    }
}
