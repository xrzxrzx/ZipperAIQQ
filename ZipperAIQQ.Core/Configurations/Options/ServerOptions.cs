using System.Text.Json.Serialization;

namespace ZipperAIQQ.Core.Configurations.Options;

public record ServerOptions : IZipperOptions
{
    [JsonIgnore]
    public string SectionName => "Server";
}
