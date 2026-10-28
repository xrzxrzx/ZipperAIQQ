namespace ZipperAIQQ.Server.Config;

/// <summary>RuntimeSettings 里的布尔值口径（历史写入有 <c>1</c> / <c>true</c> / <c>True</c> 三种）。</summary>
public static class SettingsValue
{
    public static bool IsTrue(string? raw) => raw is "1" or "true" or "True";
}
