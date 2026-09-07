# GOAP 入门讲解

> **面向读者**：计算机专业、无游戏开发经验的协作者。
> **前置知识**：数据结构（图搜索、优先队列）、A\* 算法、基础 C#。不需要任何游戏引擎经验。
> **读完后**：理解本项目 GOAP 层的四个核心概念与规划器工作原理，能读懂并跑通文中的最小实现。
> **定位**：教学材料，不引入新设计决策；与 `Docs/DESIGN.md` 冲突时，以 `DESIGN.md` 为准。

---

## 1. GOAP 解决什么问题

写游戏 AI，本质是在回答一个问题：**NPC 下一步该做什么？**

传统做法的演进：

| 方案 | 做法 | 问题 |
|---|---|---|
| if-else 硬编码 | 每种情况写一条规则 | 情况组合爆炸，规则之间互相打架 |
| 有限状态机（FSM） | 手工定义状态与转移条件 | 状态数随内容暴涨，转移连线很快看不过来 |
| 行为树（BT） | 树形分解决策，模块化较好 | 仍然是"作者预先想全所有情形"，新增内容常要改树结构 |
| **GOAP** | 只提供"零件"和"愿望"，运行时自动搜索组合 | 需要一次性实现一个规划器（本项目预估 500–800 行） |

**GOAP（Goal-Oriented Action Planning，面向目标的行动规划）的本质一句话**：

> 把"NPC 该做什么"转化为一个图搜索问题——起点是当前世界状态，终点是目标状态，
> 边是当前可行的行动，用 A\* 找出总代价最小的行动链。

对计算机专业背景的读者来说，这就是《人工智能》课程里**自动规划（Automated Planning）**的经典模型 **STRIPS** 在游戏中的工程化落地。算法本体你已经学过——A\*，只是搜索空间从"格子地图"换成了"抽象世界状态"。

**关键思维转变**：游戏设计者不再编写"行为"，而是编写"零件"（原子行动）与"愿望"（目标）；具体行为是规划器在运行时自动组合出来的。新增一种行动 = 加一条数据，规划器自动发现它与其它行动的新组合，引擎代码一行不改——这正是本项目"内容扩展靠数据不靠代码"原则（DESIGN.md §4.5）选型 GOAP 的根本原因。

---

## 2. 四个核心概念

### 2.1 World State（世界状态）

对世界的**量化描述**，一个键值对集合：

```json
{ "food": 2, "wood": 0, "money": 10, "at_forest": 0 }
```

- 缺失的键视为 0。
- 数值语义约定：**前提条件判断用「≥」；行动效果用「+=」**（效果可为负，如吃掉一份食物是 `food: -1`）。
- 世界状态**必须可复制**：规划器搜索时要不断生成"假如执行了行动 A，世界会变成什么样"的假想状态，不能污染真实世界。

### 2.2 Action（行动）

一个行动 = **前提条件** + **执行效果** + **代价**，例如：

| 字段 | 收集浆果 | 含义 |
|---|---|---|
| Preconditions | `{ "at_forest": 1 }` | 只有在森林里才能执行 |
| Effects | `{ "food": +1 }` | 执行后食物 +1 |
| Cost | `4` | 执行代价，规划器追求总代价最小 |

**行动是纯数据，不含任何逻辑代码。** 这一点是双层架构的前提：LLM 可以生成或调整行动配置，而不用担心它"写入程序逻辑"。

### 2.3 Goal（目标）

期望世界满足的条件 + 优先级：

```json
{ "name": "不挨饿", "conditions": { "food": 2 }, "priority": 10 }
```

行动是世界**能发生**的事，目标是 NPC **想达成**的事，规划器负责把两者连起来。

### 2.4 Planner（规划器）

- 输入：当前世界状态 + 一个目标 + 行动库。
- 输出：一个行动序列（**计划 / plan**），或"目标当前不可达"。
- 内部实现就是一次 A\* 搜索（见下一节）。

---

## 3. 规划器在做什么：隐式状态图上的 A\*

如果你熟悉 A\* 寻路，可以这样对应：

| | A\* 寻路 | GOAP 规划 |
|---|---|---|
| 节点 | 地图格子（位置） | 一个完整的世界状态 |
| 边 | 相邻格子间的移动 | 一个"前提满足"的可执行行动 |
| 边权 | 移动距离/耗时 | 行动的 Cost |
| 起点 | 当前位置 | 当前世界状态 |
| 终点 | 目标位置 | 目标条件全部满足的状态 |
| 启发函数 h(n) | 到目标的直线距离 | 目标中**尚未满足的条件个数** |

两个对本项目重要的技术细节：

1. **图是隐式的**。节点（世界状态）不预先存在，而是"取当前状态 → 复制 → 应用某行动的效果"动态生成的。所以搜索前不需要枚举整个状态空间。
2. **计划是"推断"出来的，不是"程序"**。它只在规划那一刻有效：执行期间，其他 NPC、规则、天气都在改变真实世界，剩余行动的前提可能失效。检测到失效就**重新规划**。NPC 的"自主感"与"反应力"都来自这个 *失效 → 重规划* 循环，这也是 DESIGN.md 中"环境变化实时重规划"的含义。

### 运行时循环（每 tick）

```
每个 tick：
    若 没有计划 或 计划中下一个行动的前提已不满足：
        goal = 优先级最高、且可规划出的目标
        plan = planner.Plan(当前世界状态, goal.条件, 行动库)
    若 plan 非空：
        执行下一个行动（一个行动可能持续多个 tick）
        行动完成 → 将其 Effects 应用到真实世界状态，计划指针前移
```

> tick = 模拟内核的最小时间步。本项目模拟内核以 tick 推进（headless 也可运行），"游戏日"调度在 M2 引入（DESIGN.md §6.4）。

---

## 4. 最小可运行实现（纯 C#，不依赖 Unity）

以下约 130 行代码是一个完整可运行的 GOAP 内核教学版，符合本项目"headless 内核是一等公民"的定位——可以直接用 `dotnet run` 或任意 C# 环境跑起来，不依赖 Unity。M1 要做的正式实现就是这些概念的工程化版本（冻结接口 `IWorldState / IAction / IGoal / IPlanner`）+ 单元测试。

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

// ═══════════ 1. 世界状态 ═══════════
// 「键 → 整数」描述世界；缺失键视为 0。前提判断「>=」，效果「+=」。
public sealed class WorldState
{
    private readonly Dictionary<string, int> _values;

    public WorldState(Dictionary<string, int> values)
        => _values = new Dictionary<string, int>(values);   // 拷贝，保证不可变语义

    public WorldState Clone() => new WorldState(_values);

    public int Get(string key) => _values.TryGetValue(key, out var v) ? v : 0;

    public bool Meets(string key, int min) => Get(key) >= min;

    public bool MeetsAll(IReadOnlyDictionary<string, int> conditions)
    {
        foreach (var kv in conditions)
            if (Get(kv.Key) < kv.Value) return false;
        return true;
    }

    public void Apply(IReadOnlyDictionary<string, int> effects)
    {
        foreach (var kv in effects)
            _values[kv.Key] = Get(kv.Key) + kv.Value;
    }
}

// ═══════════ 2. 行动：纯数据，不含逻辑 ═══════════
public sealed class GoapAction
{
    public string Name;
    public Dictionary<string, int> Preconditions = new Dictionary<string, int>(); // 可执行前提（>= 语义）
    public Dictionary<string, int> Effects       = new Dictionary<string, int>(); // 执行后的数值增量（+= 语义）
    public float Cost = 1f;                                                       // 代价，规划器求总代价最小
}

// ═══════════ 3. 目标：期望世界满足的条件 ═══════════
public sealed class GoapGoal
{
    public string Name;
    public Dictionary<string, int> Conditions = new Dictionary<string, int>();
    public float Priority;   // 多目标并存时先尝试优先级高的
}

// ═══════════ 4. 规划器：在隐式状态图上跑 A* ═══════════
public sealed class GoapPlanner
{
    private sealed class Node
    {
        public WorldState State;
        public Node Parent;
        public GoapAction Via;   // 经由哪个行动从 Parent 到达本节点
        public float G;          // 起点到此的真实累计代价
        public float H;          // 启发值 = 目标中尚未满足的条件个数
        public float F => G + H;
    }

    public List<GoapAction> Plan(WorldState start, Dictionary<string, int> goal,
                                 IEnumerable<GoapAction> actions, int maxIterations = 5000)
    {
        var open = new List<Node> { new Node { State = start, G = 0, H = Heuristic(start, goal) } };

        for (int iter = 0; iter < maxIterations && open.Count > 0; iter++)
        {
            // 取 F 最小节点。教学版线性扫描；正式实现应换二叉堆。
            int best = 0;
            for (int i = 1; i < open.Count; i++)
                if (open[i].F < open[best].F) best = i;
            Node cur = open[best];
            open.RemoveAt(best);

            if (cur.H == 0) return Reconstruct(cur);          // 目标全部满足 → 回溯得计划

            foreach (var a in actions)
            {
                if (!cur.State.MeetsAll(a.Preconditions)) continue;   // 前提不满足，此路不通
                var next = cur.State.Clone();
                next.Apply(a.Effects);
                open.Add(new Node { State = next, Parent = cur, Via = a,
                                    G = cur.G + a.Cost, H = Heuristic(next, goal) });
            }
        }
        return null;   // 搜索预算内无解：目标当前不可达
    }

    private static int Heuristic(WorldState s, Dictionary<string, int> goal)
    {
        int unmet = 0;
        foreach (var kv in goal)
            if (!s.Meets(kv.Key, kv.Value)) unmet++;
        return unmet;
    }

    private static List<GoapAction> Reconstruct(Node n)
    {
        var plan = new List<GoapAction>();
        for (var cur = n; cur.Via != null; cur = cur.Parent) plan.Add(cur.Via);
        plan.Reverse();
        return plan;
    }
}

// ═══════════ 5. 演示：一个饥饿 NPC 的决策 ═══════════
public static class Demo
{
    public static void Main()
    {
        // 行动库：NPC 会什么。注意——全部是数据。
        var actions = new List<GoapAction>
        {
            new GoapAction { Name="购买面包", Cost=2f,
                Preconditions=new Dictionary<string,int>{ ["money"]=5, ["at_market"]=1 },
                Effects=new Dictionary<string,int>{ ["food"]=3, ["money"]=-5 } },
            new GoapAction { Name="收集浆果", Cost=4f,
                Preconditions=new Dictionary<string,int>{ ["at_forest"]=1 },
                Effects=new Dictionary<string,int>{ ["food"]=1 } },
            new GoapAction { Name="前往市场", Cost=1f,
                Effects=new Dictionary<string,int>{ ["at_market"]=1, ["at_forest"]=-1 } },
            new GoapAction { Name="前往森林", Cost=1f,
                Effects=new Dictionary<string,int>{ ["at_forest"]=1, ["at_market"]=-1 } },
        };

        // 目标集：NPC 想要什么
        var goals = new List<GoapGoal>
        {
            new GoapGoal { Name="不挨饿", Priority=10f,
                Conditions=new Dictionary<string,int>{ ["food"]=2 } },
            new GoapGoal { Name="攒木头",  Priority=5f,
                Conditions=new Dictionary<string,int>{ ["wood"]=3 } },
        };

        // 当前世界（at_market / at_forest 缺省 → 视为 0，即不在任何地点）
        var world = new WorldState(new Dictionary<string,int> { ["food"]=0, ["money"]=10 });

        // 每个 tick 的核心动作：选目标 → 规划 → 执行
        var goal = goals[0];   // 取最高优先级（此处从简，未做可达性排序）
        var plan = new GoapPlanner().Plan(world, goal.Conditions, actions);

        if (plan == null) { Console.WriteLine($"「{goal.Name}」当前不可达：换目标或等待"); return; }

        Console.WriteLine($"目标「{goal.Name}」的计划，总代价 {plan.Sum(a => a.Cost)}：");
        foreach (var a in plan)
        {
            world.Apply(a.Effects);   // 正式实现中这一步发生在行动执行完毕时
            Console.WriteLine($"  执行 {a.Name} → food={world.Get("food")}, money={world.Get("money")}");
        }
    }
}
```

**预期输出**：

```
目标「不挨饿」的计划，总代价 3：
  执行 前往市场 → food=0, money=10
  执行 购买面包 → food=3, money=5
```

值得注意：行动库里存在"去森林采两次浆果"的方案（代价 1+4+4=9），但规划器算出"去市场买面包"更便宜（1+2=3）。**这个选择不是任何人写死在代码里的，是搜索自己算出来的**——这就是 GOAP 与硬编码行为的本质区别。

**教学版的三个有意简化**（正式实现 M1 会补上）：

1. open 集合用线性扫描代替优先队列，且没有 closed 集合做状态去重——只影响速度，不影响正确性；Unity 的 API 兼容级别下没有现成优先队列，需自写二叉堆。
2. 状态数值无上下界（如位置出现 -1）——正式 World State Schema 会约束取值范围。
3. 单一规划深度由 `maxIterations` 兜底防组合爆炸——正式实现还会加状态签名去重与深度上限。

**动手实验**（建议跑一遍，建立直觉）：

- 把初始 `money` 改为 `0` → 计划自动变为"前往森林 → 收集浆果 ×2"。同一份行动数据，不同世界状态，得出不同计划。
- 再删掉"前往森林"行动 → `Plan` 返回 `null`，目标不可达。**这正是本项目"规则验证层"做沙盒可达性检查的原理**：把 LLM 生成的新规则放进沙盒跑 N tick，用同一个规划器检验 NPC 目标是否仍然可达（DESIGN.md §4.2）。
- 把"收集浆果"的 `Cost` 改成 `0.5f` → 计划翻转为去森林。可见**改一条数据的代价参数，就能翻转 NPC 行为**——记住这一点，下一节会用到。

---

## 5. GOAP 在本项目中的位置

本项目是双层 AI 架构（详见 DESIGN.md 第三节），GOAP 只是下半层：

```
玩家宏观指令（如"优先发展农业"）
      │
      ▼
LLM 层「规则制定者」(低频：每游戏日 + 事件驱动)
      │  只输出结构化 JSON，永不直接改状态、永不生成代码
      ▼
规则验证层（Schema 校验 → 沙盒模拟 N tick → 通过才应用，失败回退默认）
      │
      ▼
GOAP 层「行动执行者」(每 tick)：每个 NPC 独立规划器
      │
      ▼
小镇状态演化 → 反馈给 LLM 层，进入下一轮
```

**核心问题：LLM 到底和 GOAP 有什么关系？** 答案是——LLM 调整的是 GOAP 的**参数**，不是**逻辑**。用一个例子看：

- **平时**：浆果链总代价 1+2+2 = 5，买面包链总代价 1+5 = 6 → NPC 自动选择去森林。
- **暴雨日**：LLM 生成一份日脚本（示意，Schema 未冻结）：

  ```json
  { "type": "weather", "name": "暴雨",
    "effects": { "action_cost_multiplier": { "收集浆果": 2.0 } } }
  ```

  浆果代价 ×2：浆果链变成 1+4+4 = 9 > 6 → **NPC 的计划自动翻转为去市场买面包**。
- 全程没有任何新代码、没有任何人为 NPC 写"下雨要改买面包"的规则。**行为变化是规划器重搜一遍自己算出来的**——这就是双层架构的意义：LLM 负责"世界发生了什么变化"（创造力、低成本的内容生产），GOAP 负责"NPC 该怎么应对"（可靠性、可解释、无幻觉）。

职责铁律再强调一遍（DESIGN.md §3.2，违反即架构失效）：

1. LLM 只产出结构化 JSON（行动/目标/规则/人格配置），**永不直接修改游戏状态，永不生成代码**；
2. **GOAP 引擎是唯一的状态修改者**；
3. LLM 只在游戏日边界或事件触发时调用，不在帧循环里同步等 API，失败一律回退内置默认。

M1 里程碑交付的，就是上图中下半层的 headless 内核：1 个 NPC、3 个行动、2 个目标，自主生存 24 游戏小时并附单元测试（DESIGN.md §6.4）。文末第 4 节的教学实现，就是你实现它的概念地图。

---

## 6. FAQ

**Q1：GOAP 和 A\* 寻路是什么关系？**
同一算法，不同搜索空间。寻路的节点是"格子/位置"，GOAP 的节点是"整个世界状态"；边从"相邻格子"变成"前提满足的行动"。图都是隐式的，搜索过程都一样：open 集、g/h 值、启发函数。

**Q2：状态空间会不会组合爆炸？**
理论上是无限的（数值状态无界），实践中有成熟的约束手段：原子行动库通常只有几十上百个（本项目 M1 只有 3 个）；启发函数大幅剪枝；给搜索设迭代预算上限（代码里的 `maxIterations`）；对状态做签名去重。GOAP 在《F.E.A.R.》（2005）等商业游戏中已在百量级行动库下实时运行，本项目规模远小于此。

**Q3：和强化学习（RL）有什么区别？为什么不用 RL？**
RL 靠试错学出策略：需要海量交互训练，产出是一个难以解释的黑盒函数。GOAP 显式建模前提与效果：**零训练**，每次决策都能从日志打印出"我为什么这么做"（哪些前提、哪条计划、代价多少）。本项目是实验平台，要求可复现、可解释、每次规划留痕（DESIGN.md §4.5 可观测性优先），GOAP 完全契合；且"创造力"已由上层 LLM 提供，下层需要的是可靠性。

**Q4：为什么不用行为树？**
行为树表达的是"作者预先想好的所有情形"，新增内容往往要修改树结构本身。GOAP 中新增内容 = 新增一条数据，规划器自动发现新的组合方式。对本项目"内容扩展靠数据、不靠代码"的一等要求，GOAP 是更贴合的底座。

---

## 7. 延伸阅读

- Jeff Orkin, *Three States and a Plan: The A.I. of F.E.A.R.*（GDC 2005）—— GOAP 在商业游戏中的开山实践，概念与本文一致。
- Fikes & Nilsson, *STRIPS: A New Approach to the Application of Theorem Proving to Problem Solving*（1971）—— 自动规划的起点，《人工智能》课程经典。
- Ian Millington, *AI for Games*—— 第 5 章（Planning）有与本文同一思路的教材式讲解，含 C++ 实现。
