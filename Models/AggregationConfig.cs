using System.Text.Json.Serialization;

namespace OpenCodeMcpServer.Models;

// <summary>
/// 【功能说明】：模型聚合配置 - 控制多路并行响应如何合并（vote/moa/best_of_n/none）
/// 【服务对象】：ChatCompletionProxyService，通过 Routing.Aggregation 节配置
/// 【调用方式】：IOptions<McpServerConfig>.Value.Routing.Aggregation
/// 【禁止重复】：项目内唯一聚合配置实现，新增聚合模式在此扩展
/// </summary>
public record AggregationConfig(
    /// <summary>聚合模式：none=单路，vote=共识投票，moa=生成式裁判，best_of_n=最优优先</summary>
    [property: JsonPropertyName("mode")]
    string Mode = "none",

    /// <summary>并行候选数量（仅在 vote/moa/best_of_n 模式下生效，1=退化为串行）</summary>
    [property: JsonPropertyName("fanOut")]
    int FanOut = 3,

    /// <summary>MoA 模式下使用的裁判模型 ID（必填，如 "SuperModel-均衡组"）</summary>
    [property: JsonPropertyName("judgeModel")]
    string? JudgeModel = null,

    /// <summary>MoA 模式下裁判提供商（可选，空则用 JudgeModel 解析）</summary>
    [property: JsonPropertyName("judgeProviderId")]
    string? JudgeProviderId = null,

    /// <summary>vote 模式下内容相似度阈值（0~1，低于此值视为无共识）</summary>
    [property: JsonPropertyName("consensusThreshold")]
    double ConsensusThreshold = 0.5,

    /// <summary>best_of_n 模式下权重计算方式：health=健康评分，latency=延迟倒数</summary>
    [property: JsonPropertyName("weightBy")]
    string WeightBy = "health"
);
