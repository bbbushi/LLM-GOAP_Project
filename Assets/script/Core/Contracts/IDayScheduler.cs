namespace Vibe.Core.Contracts
{
    /// <summary>
    /// 游戏日调度挂载点（M2 收尾，DESIGN.md §6.4「游戏日推进调度」）：引擎在日界且仅日界时
    /// 调用——紧随 DayBegin 事件、先于被动结算，回调所见世界是新日 0 时快照（值仍为昨日收盘）。
    /// 这是引擎结构上唯一的日界级外部入口：M3 的每日世界脚本（LLM 天气等）在此装载，
    /// 「LLM 调用只发生在游戏日边界或事件触发时」（DESIGN.md §3.2 铁律 3）由引擎时序承载，
    /// 不依赖调用方自律（危机触发的规则调整走 CrisisTriggered 事件通道，M3 落地）。
    ///
    /// 实现约定（与内核整体姿态一致）：回调必须非阻塞、只写数据——外部 API 的预取、缓存
    /// 与失败回退内置默认是实现方自己的事，tick 循环内不得等待 API；日界内容的生效走既有
    /// 通道：替换 <see cref="IRuleProvider.Current"/> 并触发 RulesChanged，引擎在同 tick
    /// 作废全部计划（PlansInvalidated），NPC 当日首个 tick 即按新规则重规划。
    /// 回调异常向上冒泡（fail-fast，同 <see cref="Simulation.Emitted"/> 观察者）；
    /// 不得在其中推进模拟或修改世界状态。
    /// </summary>
    public interface IDayScheduler
    {
        /// <summary>新游戏日开始。day 为新日号（首个日界为 2）——与 DayBegin 事件
        /// 「只在日界变迁发出、不含初始日」的语义一致；初始日的规则由宿主在构造期设定。</summary>
        void OnDayBegin(int day);
    }
}
