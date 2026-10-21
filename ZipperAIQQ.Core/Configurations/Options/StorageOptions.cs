using System.Text.Json.Serialization;

namespace ZipperAIQQ.Core.Configurations.Options;

public record StorageOptions : IZipperOptions
{
    [JsonIgnore]
    public string SectionName => "Storage";
    public string? StoragePath { get; init; }
}
