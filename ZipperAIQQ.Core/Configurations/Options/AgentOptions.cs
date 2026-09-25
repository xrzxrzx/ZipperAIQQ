using System.Text.Json.Serialization;

namespace ZipperAIQQ.Core.Configurations.Options;

public record AgentOptions : IZipperOptions
{
    [JsonIgnore]
    public string SectionName => "Agent";
    public string? AgentName { get; init; }
    public string? AgentPresetFile { get; init; }
    public string? BaseUrl { get; init; }
    public string? LlmModel { get; init; }
    public string? ApiKey { get; init; }
}
