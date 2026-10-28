using System.Text;
using System.Text.Json;
using Serilog;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.External;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 外部 WS 配置：token **加密落库 + 不回显**，以及 setExternal 的三态语义。
/// </summary>
/// <remarks>
/// 用可逆的 <see cref="FakeProtector"/> 代替 Data Protection，测试只关心"落库是不是密文、能不能读回"。
/// 数据库用临时文件（<see cref="SqliteDatabase"/> 每次读写都开新连接）。
/// </remarks>
public sealed class ExternalOptionsTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();
    private static readonly ServerOptions Bootstrap = new()
    {
        NapCatWebSocketUrl = "ws://127.0.0.1:3001",
        LlmApiKey = "k",
        LlmBaseUrl = "https://example.test",
        LlmModel = "m",
    };

    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly SqliteRuntimeSettingsStore _settings;
    private readonly FakeProtector _protector = new();

    public ExternalOptionsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-external-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase($"Data Source={_dbPath}", Logger);
        _settings = new SqliteRuntimeSettingsStore(_db);
    }

    public async Task InitializeAsync()
    {
        var initializer = new DatabaseInitializer($"Data Source={_dbPath}", Logger);
        await initializer.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
                // 临时文件清理失败不影响测试结论
            }
        }
    }

    private ExternalOptionsService NewService() =>
        new(_settings, Bootstrap, _protector, Logger);

    [Fact]
    public async Task Tokens_are_encrypted_at_rest_and_roundtrip()
    {
        var service = NewService();
        await service.UpdateAsync(
            allowSend: true,
            allowedSessions: ["qq:p10001"],
            reverseServers: [new ExternalReverseServer { Host = "0.0.0.0", Port = 5181, Token = "reverse-secret" }],
            forwardServers: [new ExternalForwardServer { Url = "ws://example.test", Token = "forward-secret" }],
            CancellationToken.None);

        var reverseJson = await _settings.GetAsync(ExternalOptionsService.ReverseServersKey, CancellationToken.None);
        var forwardJson = await _settings.GetAsync(ExternalOptionsService.ForwardServersKey, CancellationToken.None);

        Assert.NotNull(reverseJson);
        Assert.NotNull(forwardJson);
        Assert.DoesNotContain("reverse-secret", reverseJson);
        Assert.DoesNotContain("forward-secret", forwardJson);
        Assert.Contains("enc:", reverseJson);
        Assert.Contains("enc:", forwardJson);

        var reloaded = NewService();
        await reloaded.InitializeAsync(CancellationToken.None);

        Assert.Equal("reverse-secret", reloaded.Current.ReverseServers[0].Token);
        Assert.Equal("forward-secret", reloaded.Current.ForwardServers[0].Token);
    }

    [Fact]
    public async Task Legacy_plaintext_tokens_are_migrated_on_startup()
    {
        var legacy = """
            [{"Name":"旧监听","Host":"0.0.0.0","Port":5181,"Token":"legacy-secret","Enabled":true}]
            """;
        await _settings.SetAsync(ExternalOptionsService.ReverseServersKey, legacy, CancellationToken.None);

        var service = NewService();
        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal("legacy-secret", service.Current.ReverseServers[0].Token);

        var stored = await _settings.GetAsync(ExternalOptionsService.ReverseServersKey, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.DoesNotContain("legacy-secret", stored);
        Assert.Contains("enc:", stored);
    }

    [Fact]
    public async Task SetExternal_keeps_token_when_omitted_and_clears_on_empty()
    {
        var service = NewService();
        await service.UpdateAsync(
            allowSend: true,
            allowedSessions: [],
            reverseServers: [new ExternalReverseServer { Host = "0.0.0.0", Port = 5181, Token = "secret" }],
            forwardServers: [],
            CancellationToken.None);

        var action = new SetExternalAction(service);

        await action.HandleAsync(Payload("""
            {"allowSend":true,"allowedSessions":[],"reverseServers":[
              {"name":"x","host":"0.0.0.0","port":5181,"enabled":true}
            ],"forwardServers":[]}
            """), CancellationToken.None);
        Assert.Equal("secret", service.Current.ReverseServers[0].Token);

        await action.HandleAsync(Payload("""
            {"allowSend":true,"allowedSessions":[],"reverseServers":[
              {"name":"x","host":"0.0.0.0","port":5181,"token":"","enabled":true}
            ],"forwardServers":[]}
            """), CancellationToken.None);
        Assert.Null(service.Current.ReverseServers[0].Token);

        await action.HandleAsync(Payload("""
            {"allowSend":true,"allowedSessions":[],"reverseServers":[
              {"name":"x","host":"0.0.0.0","port":5181,"token":"new-secret","enabled":true}
            ],"forwardServers":[]}
            """), CancellationToken.None);
        Assert.Equal("new-secret", service.Current.ReverseServers[0].Token);
    }

    [Fact]
    public async Task GetExternal_never_echoes_plaintext_token()
    {
        var service = NewService();
        await service.UpdateAsync(
            allowSend: false,
            allowedSessions: [],
            reverseServers: [new ExternalReverseServer { Host = "0.0.0.0", Port = 5181, Token = "secret" }],
            forwardServers: [],
            CancellationToken.None);

        var result = await new GetExternalAction(service).HandleAsync(default, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        var text = json.GetRawText();
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("\"token\"", text);
        Assert.True(json.GetProperty("reverseServers")[0].GetProperty("tokenConfigured").GetBoolean());
    }

    private static JsonElement Payload(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) =>
            "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string? Unprotect(string? cipher)
        {
            if (cipher is null || !cipher.StartsWith("enc:", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(cipher[4..]));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
