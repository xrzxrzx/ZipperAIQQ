using System.Text.Json;
using Serilog;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 人设保存不能把【表达】段丢掉（B6 独立审查 P1-1）。
/// </summary>
/// <remarks>
/// 客户端的编辑框只有形象 / 性格两个；服务端若用三参 <c>FromParts</c> 重建正文，
/// 「点一次保存」就会把 <c>config/persona.md</c> 里的【表达】段整段删掉 ——
/// 而且客户端既看不到、也补不回来。这组用例就是把那条路堵住。
/// </remarks>
public sealed class PersonaActionsTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private const string InitialContent = """
        【形象】
        蓝发猫耳少女

        【性格】
        表面嫌麻烦其实细心

        【表达】
        说话别先自我审查
        """;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-persona-{Guid.NewGuid():N}.db");
    private readonly string _personaPath = Path.Combine(Path.GetTempPath(), $"zipper-persona-{Guid.NewGuid():N}.md");
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;

    private DefaultPersonaStore _personas = null!;
    private SqliteConfigStore _configs = null!;
    private ServerOptions _options = null!;

    public PersonaActionsTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

        await File.WriteAllTextAsync(_personaPath, InitialContent);

        _personas = new DefaultPersonaStore(
            _db,
            new Persona("小衣", InitialContent));
        _configs = new SqliteConfigStore(_db);
        _options = new ServerOptions
        {
            PersonaPath = _personaPath,

            // 本组只用到 PersonaPath；其余 required 项给占位值（不去连任何东西）
            NapCatWebSocketUrl = "ws://127.0.0.1:3001",
            LlmApiKey = "test",
            LlmBaseUrl = "https://example.invalid",
            LlmModel = "test",
        };
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        foreach (var path in new[] { _dbPath, _personaPath })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 连接池可能还持有句柄
            }
        }
    }

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task SetGlobalPersona_KeepsExpressSection()
    {
        var action = new SetGlobalPersonaAction(_options, _configs, _personas);

        await action.HandleAsync(
            Payload(new { image = "换了个形象", personality = "换了个性格" }),
            CancellationToken.None);

        var written = await File.ReadAllTextAsync(_personaPath);

        Assert.Contains("【表达】", written);
        Assert.Contains("说话别先自我审查", written);       // ← 关键：没被删
        Assert.Contains("换了个形象", written);
        Assert.Contains("换了个性格", written);
    }

    [Fact]
    public async Task SetGlobalPersona_ExplicitExpress_OverridesAndEmptyClears()
    {
        var action = new SetGlobalPersonaAction(_options, _configs, _personas);

        await action.HandleAsync(
            Payload(new { image = "形象", personality = "性格", express = "新表达段" }),
            CancellationToken.None);

        Assert.Contains("新表达段", await File.ReadAllTextAsync(_personaPath));

        // 显式空串 = 清除该段
        await action.HandleAsync(
            Payload(new { image = "形象", personality = "性格", express = "" }),
            CancellationToken.None);

        Assert.DoesNotContain("【表达】", await File.ReadAllTextAsync(_personaPath));
    }

    [Fact]
    public async Task SetPersona_KeepsExpressSectionOfSessionOverride()
    {
        // 先给会话建一份带【表达】段的预设
        await _personas.SetAsync(
            "qq:p10001",
            Persona.FromParts("小衣", "原形象", "原性格", "会话表达段"),
            CancellationToken.None);

        var action = new SetPersonaAction(_personas, _configs);

        await action.HandleAsync(
            Payload(new { sessionId = "qq:p10001", image = "新形象", personality = "新性格" }),
            CancellationToken.None);

        var saved = await _personas.GetStateAsync("qq:p10001", CancellationToken.None);
        Assert.NotNull(saved.Override);
        Assert.Equal("会话表达段", saved.Override!.Express);
    }

    [Fact]
    public async Task GetPersona_ReturnsExpressSection()
    {
        var action = new GetPersonaAction(_personas);

        var result = JsonSerializer.SerializeToElement(
            await action.HandleAsync(Payload(new { sessionId = "global" }), CancellationToken.None));

        Assert.Equal("说话别先自我审查", result.GetProperty("express").GetString());
        Assert.Equal("蓝发猫耳少女", result.GetProperty("image").GetString());
    }
}
