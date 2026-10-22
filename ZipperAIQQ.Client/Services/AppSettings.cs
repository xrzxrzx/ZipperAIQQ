using System.Text.Json;
using ZipperAIQQ.Client.Dto;

namespace ZipperAIQQ.Client.Services;

/// <summary>
/// 客户端本地设置：**只需要服务器地址 + token**（FR-8.3）。
/// </summary>
/// <remarks>
/// 存在 <c>%LOCALAPPDATA%\ZipperAIQQ\client.json</c>。
/// ⚠️ 这里只放"连哪台服务端"，**不放任何模型 key** —— key 由服务端加密保管。
/// </remarks>
public sealed class AppSettings
{
    private static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZipperAIQQ");

    private static readonly string FilePath = Path.Combine(Directory, "client.json");

    public string ServerUrl { get; set; } = "http://127.0.0.1:5180";

    public string Token { get; set; } = string.Empty;

    /// <summary>侧栏是否展开（记住上次关闭应用时的状态）。</summary>
    public bool IsPaneOpen { get; set; } = true;

    /// <summary>上次选中的导航项 Tag（可选，默认打开「状态」）。</summary>
    public string SelectedNavTag { get; set; } = "status";

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, ClientJson.Options) ?? new AppSettings();
        }
        catch
        {
            // 配置坏了就用默认值，不能因此起不来
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, ClientJson.Options));
        }
        catch
        {
            // 保存失败不影响本次使用
        }
    }
}
