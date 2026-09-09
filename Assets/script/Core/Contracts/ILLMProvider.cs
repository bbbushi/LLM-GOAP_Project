using System.Threading;
using System.Threading.Tasks;

namespace Vibe.Core.Contracts
{
    /// <summary>
    /// 一次 LLM 调用请求。
    /// Id 必须确定性生成（相同输入得到相同 id），作为缓存与重放的键（可复现性，DESIGN.md §4.3）。
    /// </summary>
    public sealed class LlmRequest
    {
        public string Id { get; }
        public string SystemPrompt { get; }
        public string UserPrompt { get; }
        public float Temperature { get; }
        public int MaxTokens { get; }

        public LlmRequest(string id, string systemPrompt, string userPrompt,
            float temperature = 0.7f, int maxTokens = 2048)
        {
            Id = id;
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            Temperature = temperature;
            MaxTokens = maxTokens;
        }
    }

    /// <summary>
    /// 一次 LLM 调用结果。
    /// 失败不抛异常：以 Success=false 携带 Error，由调用方回退内置默认（铁律 3）。
    /// </summary>
    public sealed class LlmResult
    {
        public bool Success { get; }

        /// <summary>是否命中本地缓存（重放而非重调；可复现性的观测指标）。</summary>
        public bool FromCache { get; }

        /// <summary>模型原始输出；约定为结构化 JSON 文本，其解析与校验在规则验证层完成。</summary>
        public string RawContent { get; }

        /// <summary>失败原因；成功时为空字符串。</summary>
        public string Error { get; }

        /// <summary>调用耗时（毫秒）；命中缓存时为读缓存耗时。</summary>
        public double LatencyMs { get; }

        public LlmResult(bool success, bool fromCache, string rawContent, string error, double latencyMs)
        {
            Success = success;
            FromCache = fromCache;
            RawContent = rawContent;
            Error = error;
            LatencyMs = latencyMs;
        }
    }

    /// <summary>
    /// LLM 交互提供者（DeepSeek / 通义千问 / 本地模型皆可实现——可替换性本身是实验变量）。
    ///
    /// 铁律（DESIGN.md §3.2）：
    ///  - 只产结构化 JSON 文本，永不直接修改游戏状态、永不生成代码；
    ///  - 调用只发生在游戏日边界或事件触发，异步进行，绝不在帧循环中同步等待；
    ///  - 实现必须记录并缓存全部请求/响应（可复现），失败一律返回 Success=false 供上层回退。
    /// </summary>
    public interface ILLMProvider
    {
        Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
    }
}
