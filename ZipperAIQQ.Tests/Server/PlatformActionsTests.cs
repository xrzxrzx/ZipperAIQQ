using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Mail;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Mails;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 第三方平台接入（S3-F / 决策七）：<c>getPlatforms</c> 不泄密 + <c>saveDouyin</c> 的加解密与生效。
/// </summary>
/// <remarks>
/// 用临时文件 SQLite（<see cref="SqliteDatabase"/> 每次读写开新连接，内存库会随连接消失）。
/// 加密用假实现（<c>enc:</c> 前缀）—— 这里要证的是"库里不是明文"，不是 DataProtection 本身。
/// </remarks>
public sealed class PlatformActionsTests : IAsyncLifetime
{
    private const string TokenPlain = "sidecar-super-secret-token";

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-f-{Guid.NewGuid():N}.db");
    private string _connectionString = string.Empty;
    private SqliteDatabase _db = null!;
    private SqliteRuntimeSettingsStore _settings = null!;
    private SqliteConfigStore _configs = null!;
    private FakeProtector _protector = null!;
    private FakeChannel _channel = null!;
    private DouyinChannel _douyin = null!;
    private DouyinOptionsService _douyinOptions = null!;
    private MailOptionsService _mailSwitch = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source={_dbPath}";
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

        _db = new SqliteDatabase(_connectionString, Logger);
        _settings = new SqliteRuntimeSettingsStore(_db);
        _configs = new SqliteConfigStore(_db);
        _protector = new FakeProtector();

        // ServerOptions 有 4 个 required 成员：测试里给占位值
        var bootstrap = new ServerOptions
        {
            NapCatWebSocketUrl = "ws://127.0.0.1:0",
            LlmApiKey = "test-key",
            LlmBaseUrl = "http://127.0.0.1:0",
            LlmModel = "test-model",
            Douyin = new DouyinOptions { Enabled = true, Token = TokenPlain },
        };

        _douyinOptions = new DouyinOptionsService(_settings, bootstrap, _protector, Logger);
        await _douyinOptions.InitializeAsync();

        // 邮件总开关的运行时版本（S3-F 补）：启动值默认关，测试里按需打开
        _mailSwitch = new MailOptionsService(_settings, new MailOptions(), Logger);
        await _mailSwitch.InitializeAsync();

        _douyin = new DouyinChannel(_douyinOptions.Current.ToOptions(), Logger);
        _channel = new FakeChannel();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 连接池可能还占着句柄
        }
    }

    [Fact]
    public async Task GetPlatforms_NeverExposesSecrets()
    {
        // 一条带凭据的邮件账号：响应里只能有 hasSecret
        await new SqliteMailRepository(_db).UpsertAccountAsync(new MailAccount
        {
            Id = "qq-1",
            Address = "bot@example.com",
            ImapHost = "imap.example.com",
            Username = "bot@example.com",
            SecretCipher = "cipher-must-not-leak",
        });

        var action = new GetPlatformsAction(
            _channel, _douyin, _douyinOptions, new SqliteMailRepository(_db), new MailOptions(), _mailSwitch);
        var json = ToJson(await action.HandleAsync(EmptyPayload(), CancellationToken.None));

        var text = json.GetRawText();
        Assert.DoesNotContain("cipher-must-not-leak", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenPlain, text, StringComparison.Ordinal);

        var account = json.GetProperty("mail").GetProperty("accounts")[0];
        Assert.True(account.GetProperty("hasSecret").GetBoolean());

        var douyin = json.GetProperty("douyin");
        Assert.True(douyin.GetProperty("tokenConfigured").GetBoolean());
        Assert.Equal("/ws/channel", douyin.GetProperty("endpoint").GetString());

        // 平台总览：qq + douyin 都在
        var platforms = json.GetProperty("platforms").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("qq", platforms);
        Assert.Contains("douyin", platforms);
    }

    [Fact]
    public async Task SaveDouyin_StoresCipherNotPlaintext()
    {
        var action = new SaveDouyinAction(_douyinOptions, _douyin, _configs);

        var result = await action.HandleAsync(Payload("""{ "token": "new-plain-token" }"""), CancellationToken.None);

        var json = ToJson(result);
        Assert.False(json.GetProperty("skipped").GetBoolean());
        Assert.DoesNotContain("new-plain-token", json.GetRawText(), StringComparison.Ordinal);

        var stored = await _settings.GetAsync(DouyinOptionsService.TokenCipherKey, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.NotEqual("new-plain-token", stored);
        Assert.StartsWith("enc:", stored, StringComparison.Ordinal);

        // 立即生效：通道拿到的也是新值
        Assert.Equal("new-plain-token", _douyinOptions.Current.Token);
    }

    [Fact]
    public async Task SaveDouyin_EmptyToken_ClearsIt()
    {
        var action = new SaveDouyinAction(_douyinOptions, _douyin, _configs);

        await action.HandleAsync(Payload("""{ "token": "" }"""), CancellationToken.None);

        Assert.Null(_douyinOptions.Current.Token);
        var stored = await _settings.GetAsync(DouyinOptionsService.TokenCipherKey, CancellationToken.None);
        Assert.True(string.IsNullOrEmpty(stored));
    }

    [Fact]
    public async Task SaveDouyin_NormalizesAllowedSessions()
    {
        var action = new SaveDouyinAction(_douyinOptions, _douyin, _configs);

        await action.HandleAsync(
            Payload("""{ "allowedSessions": [" douyin:p* ", "", "douyin:g1", "douyin:p*"] }"""),
            CancellationToken.None);

        Assert.Equal(["douyin:p*", "douyin:g1"], _douyinOptions.Current.AllowedSessions);

        // 持久化也要是规范化的（重启后一致）
        var stored = await _settings.GetAsync(DouyinOptionsService.AllowedSessionsKey, CancellationToken.None);
        Assert.Equal("douyin:p*,douyin:g1", stored);
    }

    [Fact]
    public async Task SaveDouyin_UnchangedValues_ReturnSkippedInsteadOfBadRequest()
    {
        // 客户端把配置原样再存一次：不该报 400（与 setAccess 同口径）
        var action = new SaveDouyinAction(_douyinOptions, _douyin, _configs);

        var result = await action.HandleAsync(
            Payload("""{ "enabled": true, "sendTimeoutMs": 15000 }"""), CancellationToken.None);

        var json = ToJson(result);
        Assert.True(json.GetProperty("skipped").GetBoolean());
        Assert.Equal(0, json.GetProperty("saved").GetInt32());
    }

    [Fact]
    public async Task SaveDouyin_InvalidTimeout_ReturnsBadRequest()
    {
        var action = new SaveDouyinAction(_douyinOptions, _douyin, _configs);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            action.HandleAsync(Payload("""{ "sendTimeoutMs": 0 }"""), CancellationToken.None));

        Assert.Equal(ApiResponse.BadRequest, ex.Code);
    }

    // ─────────────────────────── 邮件运行时开关（S3-F 补）───────────────────────────

    [Fact]
    public async Task SaveMail_PersistsAndReflectsInGetPlatforms()
    {
        Assert.False(_mailSwitch.Enabled); // 启动默认关

        var action = new SaveMailAction(_mailSwitch, _configs);
        var json = ToJson(await action.HandleAsync(Payload("""{ "enabled": true }"""), CancellationToken.None));

        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, json.GetProperty("saved").GetInt32());

        // 落库 + 运行时生效
        var stored = await _settings.GetAsync(MailOptionsService.EnabledKey, CancellationToken.None);
        Assert.Equal("1", stored);
        Assert.True(_mailSwitch.Enabled);

        // getPlatforms 读的是运行时真值
        var get = new GetPlatformsAction(
            _channel, _douyin, _douyinOptions, new SqliteMailRepository(_db), new MailOptions(), _mailSwitch);
        var platforms = ToJson(await get.HandleAsync(EmptyPayload(), CancellationToken.None));
        Assert.True(platforms.GetProperty("mail").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task SaveMail_UnchangedValue_ReturnsSkipped()
    {
        var action = new SaveMailAction(_mailSwitch, _configs);

        var first = ToJson(await action.HandleAsync(Payload("""{ "enabled": true }"""), CancellationToken.None));
        Assert.False(first.GetProperty("skipped").GetBoolean());

        // 原样再存一次：不算错，也不重复审计
        var second = ToJson(await action.HandleAsync(Payload("""{ "enabled": true }"""), CancellationToken.None));
        Assert.True(second.GetProperty("skipped").GetBoolean());
        Assert.Equal(0, second.GetProperty("saved").GetInt32());
    }

    [Fact]
    public async Task SaveMail_InvalidPayload_ReturnsBadRequest()
    {
        var action = new SaveMailAction(_mailSwitch, _configs);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            action.HandleAsync(Payload("""{ "enabled": "yes" }"""), CancellationToken.None));

        Assert.Equal(ApiResponse.BadRequest, ex.Code);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static JsonElement EmptyPayload() => JsonDocument.Parse("{}").RootElement;

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement ToJson(object? value) =>
        JsonSerializer.SerializeToElement(value);

    /// <summary>假加密：<c>enc:明文</c>。要证的是"库里不是明文"，不是 DataProtection 本身。</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public string? Unprotect(string? cipher) =>
            string.IsNullOrEmpty(cipher) ? null
            : cipher.StartsWith("enc:", StringComparison.Ordinal) ? cipher[4..]
            : null;
    }

    /// <summary>假通道：两个平台，各自有自己的 selfId。</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        private readonly System.Threading.Channels.Channel<InboundEvent> _inbound =
            System.Threading.Channels.Channel.CreateUnbounded<InboundEvent>();

        public string Platform => "qq";

        public IReadOnlyList<string> Platforms => ["qq", "douyin"];

        public string? SelfId => "10001";

        public string? SelfIdFor(string platform) => platform switch
        {
            "qq" => "10001",
            "douyin" => null,
            _ => null,
        };

        public ChannelCapabilities Capabilities => ChannelCapabilities.Standard;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in _inbound.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }

        public Task SendAsync(OutgoingMessage message, CancellationToken ct) => Task.CompletedTask;
    }
}
