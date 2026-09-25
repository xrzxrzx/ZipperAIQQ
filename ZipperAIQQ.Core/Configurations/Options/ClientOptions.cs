using System.Text.Json.Serialization;

namespace ZipperAIQQ.Core.Configurations.Options;

public record ClientOptions : IZipperOptions
{
    [JsonIgnore]
    public string SectionName => "Client";
}
