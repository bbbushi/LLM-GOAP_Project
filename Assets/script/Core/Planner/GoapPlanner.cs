using System;
using System.Collections.Generic;
using Vibe.Core.Contracts;

namespace Vibe.Core
{
    /// <summary>
    /// <see cref="IPlanner"/> 的默认实现：在隐式状态图上做 A* 搜索
    /// （节点 = 世界状态，边 = 前提满足的行动，边权 = BaseCost × 规则乘数，
    /// 启发值 = 目标中尚未满足的条件个数，详见 Docs/GOAP.md §3）。
    ///
    /// 教学版（GOAP.md §4）三个简化点的正式解法（IPlanner 契约要求）：
    ///  - open 集线性扫描 → 数组二叉堆（<see cref="BinaryHeap{T}"/>）；
    ///  - 无状态去重 → closed 集合按 <see cref="IWorldState.GetFingerprint"/> 签名去重；
    ///  - 仅迭代预算 → 迭代预算 + 搜索深度上限双兜底。
    ///
    /// 语义要点：
    ///  - 目标按 Priority 降序逐一尝试，首个可规划出计划的目标胜出；
    ///    目标在起点已满足时返回<b>空步计划</b>（TotalCost=0），表示该目标当前无事可做。
    ///  - 预算按「每个目标一次完整搜索」计，不做跨目标分摊——换目标即换问题，旧预算无意义。
    ///  - 确定性：相同输入（含目标/行动列表的元素顺序）产生相同计划（DESIGN.md §4.3 可复现）——
    ///    同优先级目标按输入序尝试，堆内同 F 节点按入堆序弹出。
    /// 非线程安全，与 <see cref="WorldState"/> 同在模拟单线程内使用。
    /// </summary>
    public sealed class GoapPlanner : IPlanner
    {
        /// <summary>默认搜索迭代（节点弹出）预算。M1 行动库仅个位数，2000 次绰绰有余。</summary>
        public const int DefaultMaxIterations = 2000;

        /// <summary>默认计划深度上限（计划最大步数）。</summary>
        public const int DefaultMaxDepth = 16;

        private readonly int _maxIterations;
        private readonly int _maxDepth;

        /// <param name="maxIterations">单目标搜索的迭代（节点弹出）预算，≥1。</param>
        /// <param name="maxDepth">计划深度上限，≥0（0 表示只接受起点已满足的目标）。</param>
        public GoapPlanner(int maxIterations = DefaultMaxIterations, int maxDepth = DefaultMaxDepth)
        {
            if (maxIterations < 1)
                throw new ArgumentOutOfRangeException(nameof(maxIterations), "迭代预算至少为 1");
            if (maxDepth < 0)
                throw new ArgumentOutOfRangeException(nameof(maxDepth), "深度上限不能为负");
            _maxIterations = maxIterations;
            _maxDepth = maxDepth;
        }

        public Plan Plan(IWorldState current, IReadOnlyList<IGoal> goals,
            IReadOnlyList<IAction> actions, IRuleProvider rules)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (goals == null) throw new ArgumentNullException(nameof(goals));
            if (actions == null) throw new ArgumentNullException(nameof(actions));

            // 目标按 Priority 降序；平级保持输入序（List.Sort 不稳定，用下标做平级决胜，保证确定性）
            var order = new List<int>(goals.Count);
            for (int i = 0; i < goals.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = goals[b].Priority.CompareTo(goals[a].Priority);
                return c != 0 ? c : a.CompareTo(b);
            });

            foreach (int index in order)
            {
                var plan = SearchForGoal(current, goals[index], actions, rules);
                if (plan != null) return plan;
            }
            return null;
        }

        private Plan SearchForGoal(IWorldState start, IGoal goal,
            IReadOnlyList<IAction> actions, IRuleProvider rules)
        {
            // 单条件列表预生成：启发值需逐条件判定，而 IWorldState 只暴露整组 Meets。
            // 复用 WorldState 的算子语义（不在本类重复实现六算子，防契约分叉），每次搜索仅分配一次。
            var singleConditions = new List<WorldCondition>[goal.Conditions.Count];
            for (int i = 0; i < goal.Conditions.Count; i++)
                singleConditions[i] = new List<WorldCondition> { goal.Conditions[i] };

            var closed = new HashSet<string>();
            var open = new BinaryHeap<Node>(CompareNodes);
            long seq = 0;
            open.Push(new Node(start, null, null, 0f,
                CountUnmet(start, singleConditions), 0, seq++));

            for (int iter = 0; iter < _maxIterations && open.Count > 0; iter++)
            {
                Node cur = open.Pop();
                if (!closed.Add(cur.State.GetFingerprint()))
                    continue; // 惰性删除：该状态已被更优（不劣）路径扩展过

                if (cur.H == 0f)
                    return Reconstruct(goal, cur); // 目标条件全部满足 → 回溯得计划

                if (cur.Depth >= _maxDepth)
                    continue; // 深度上限：超出预算的计划不再向下扩展

                foreach (var action in actions)
                {
                    if (!cur.State.Meets(action.Preconditions)) continue; // 前提不满足，此路不通
                    var next = cur.State.Clone();
                    next.Apply(action.Effects);
                    string fingerprint = next.GetFingerprint();
                    if (closed.Contains(fingerprint)) continue; // 已扩展过的状态不再入堆（防无效效果自环）

                    float cost = EffectiveCost(action, rules);
                    open.Push(new Node(next, cur, action, cur.G + cost,
                        CountUnmet(next, singleConditions), cur.Depth + 1, seq++));
                }
            }
            return null; // 预算耗尽或 open 空：该目标当前不可达，换下一个目标
        }

        /// <summary>GOAP.md §3 冻结的启发函数：目标中尚未满足的条件个数。</summary>
        private static float CountUnmet(IWorldState state, List<WorldCondition>[] singleConditions)
        {
            // 注：存在「一箭双雕」行动（一次满足多个条件）或边权 &lt; 1 时该值可能高估，
            // 属文档基线允许的近似——换取零领域知识的通用性与可解释性（极端配置下计划可能非最优）。
            int unmet = 0;
            for (int i = 0; i < singleConditions.Length; i++)
                if (!state.Meets(singleConditions[i])) unmet++;
            return unmet;
        }

        /// <summary>有效代价 = BaseCost × 行动成本乘数（DESIGN.md §4.3 倍率通道，天气等外部影响经此改计划）。</summary>
        private static float EffectiveCost(IAction action, IRuleProvider rules)
        {
            // 防御：rules 为 null（无规则生效）或乘数缺键一律视为 1；乘数为负按 0 截断——
            // A* 要求边权非负。经规则验证层校验的规则集不应出现非正乘数，此处仅兜底防搜索退化。
            var multipliers = rules?.Current?.ActionCostMultipliers;
            float mult = 1f;
            if (multipliers != null && multipliers.TryGetValue(action.Id, out var m)) mult = m;
            if (mult < 0f) mult = 0f;
            return action.BaseCost * mult;
        }

        private static int CompareNodes(Node a, Node b)
        {
            // F 小者先出堆；同 F 按入堆序（FIFO），保证搜索顺序确定
            int c = a.F.CompareTo(b.F);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }

        private static Plan Reconstruct(IGoal goal, Node node)
        {
            var steps = new List<IAction>(node.Depth);
            for (var cur = node; cur.Via != null; cur = cur.Parent)
                steps.Add(cur.Via);
            steps.Reverse();
            return new Plan(goal, steps, node.G);
        }

        /// <summary>A* 搜索节点：不可变（构造后 State 不再改动，签名随之稳定）。</summary>
        private sealed class Node
        {
            public readonly IWorldState State;
            public readonly Node Parent;
            public readonly IAction Via;   // 经由哪个行动从 Parent 到达本节点；根节点为 null
            public readonly float G;       // 起点到此的真实累计有效代价
            public readonly float H;       // 启发值 = 目标中尚未满足的条件个数
            public readonly int Depth;     // 计划步数深度（根为 0）
            public readonly long Seq;      // 入堆序号，同 F 时保 FIFO，保证确定性

            public float F => G + H;

            public Node(IWorldState state, Node parent, IAction via,
                float g, float h, int depth, long seq)
            {
                State = state;
                Parent = parent;
                Via = via;
                G = g;
                H = h;
                Depth = depth;
                Seq = seq;
            }
        }
    }
}
