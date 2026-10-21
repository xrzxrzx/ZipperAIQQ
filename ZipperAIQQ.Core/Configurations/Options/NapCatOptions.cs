using System.Text.Json.Serialization;

namespace ZipperAIQQ.Core.Configurations.Options;

public record NapCatOptions : IZipperOptions
{
    [JsonIgnore]
    public string SectionName => "NapCat";
    public string? NapCatUrl { get; init; }
    public string? NapCatPort { get; init; }
    public string? NapCatToken { get; init; }
}
