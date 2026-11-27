using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Express;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Express;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Express;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>B6 T6.6：三个档位 action（状态 / 改档 / 回滚）与快照表。</summary>
public sealed class ExpressActionsTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-express-act-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;

    private ExpressOptionsService _express = null!;
    private SqliteExpressSnapshotStore _snapshots = null!;
    private SqliteConfigStore _configs = null!;

    public ExpressActionsTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

        var settings = new SqliteRuntimeSettingsStore(_db);
        _express = new ExpressOptionsService(settings, Logger);
        await _express.InitializeAsync();

        _snapshots = new SqliteExpressSnapshotStore(_db);
        _configs = new SqliteConfigStore(_db);
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
            // 连接池可能还持有句柄
        }
    }

    private GetExpressStatusAction Status() => new(_express, _snapshots);

    private SetExpressLevelAction Set() => new(_express, _snapshots, _configs, Logger);

    private RollbackExpressAction Rollback() => new(_express, _snapshots, _configs, Logger);

    private static JsonElement Payload(object value) =>
        JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Set_Then_Get_ReportsSameState()
    {
        var saved = await Set().HandleAsync(Payload(new
        {
            level = "close",
            groupCap = "daily",
            hygieneEnabled = false,
        }), CancellationToken.None);

        var setJson = JsonSerializer.SerializeToElement(saved);
        Assert.Equal(3, setJson.GetProperty("saved").GetInt32());
        Assert.False(setJson.GetProperty("skipped").GetBoolean());
        Assert.Equal("close", setJson.GetProperty("level").GetString());
        Assert.True(setJson.GetProperty("snapshotId").GetInt64() > 0);

        var status = JsonSerializer.SerializeToElement(
            await Status().HandleAsync(Payload(new { }), CancellationToken.None));

        Assert.Equal("close", status.GetProperty("level").GetString());
        Assert.Equal("亲近", status.GetProperty("levelLabel").GetString());
        Assert.Equal("daily", status.GetProperty("groupCap").GetString());
        Assert.Equal("global", status.GetProperty("source").GetString());
        Assert.False(status.GetProperty("hygiene").GetProperty("enabled").GetBoolean());
        Assert.Equal("http-client", status.GetProperty("lastChangedBy").GetString());
        Assert.True(status.GetProperty("lastChangedAt").GetInt64() > 0);

        // 改前那份快照 + 这次改档后的回滚点都在
        Assert.True(status.GetProperty("snapshots").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Set_UnknownLevel_ReturnsBadRequest()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Set().HandleAsync(Payload(new { level = "crazy" }), CancellationToken.None));

        Assert.Equal(ApiResponse.BadRequest, ex.Code);
        Assert.Contains("daily / close / explicit", ex.Message);

        // 什么都没写进去
        Assert.Equal(ExpressLevel.Daily, _express.Current.Level);
    }

    [Fact]
    public async Task Set_NoActualChange_ReportsSkipped()
    {
        var result = JsonSerializer.SerializeToElement(
            await Set().HandleAsync(Payload(new { level = "daily" }), CancellationToken.None));

        Assert.Equal(0, result.GetProperty("saved").GetInt32());
        Assert.True(result.GetProperty("skipped").GetBoolean());
    }

    [Fact]
    public async Task SessionOverride_AppearsInStatus_AndSourceBecomesSession()
    {
        await Set().HandleAsync(Payload(new
        {
            sessionId = "qq:g10001",
            sessionLevel = "daily",
            level = "explicit",
        }), CancellationToken.None);

        var status = JsonSerializer.SerializeToElement(
            await Status().HandleAsync(Payload(new { sessionId = "qq:g10001" }), CancellationToken.None));

        Assert.Equal("session", status.GetProperty("source").GetString());
        var overrides = status.GetProperty("sessionOverrides");
        Assert.Equal(1, overrides.GetArrayLength());
        Assert.Equal("qq:g10001", overrides[0].GetProperty("sessionId").GetString());
        Assert.Equal("daily", overrides[0].GetProperty("level").GetString());
    }

    [Fact]
    public async Task SessionOverride_CanBeCleared()
    {
        await Set().HandleAsync(Payload(new
        {
            sessionId = "qq:g10001",
            sessionLevel = "close",
        }), CancellationToken.None);
        Assert.Single(_express.Current.SessionOverrides);

        // 只给 sessionId、不给 sessionLevel = 显式删除
        var cleared = JsonSerializer.SerializeToElement(
            await Set().HandleAsync(Payload(new { sessionId = "qq:g10001" }), CancellationToken.None));

        Assert.Equal(1, cleared.GetProperty("saved").GetInt32());
        Assert.Empty(_express.Current.SessionOverrides);
    }

    [Fact]
    public async Task Rollback_RestoresByteIdenticalState()
    {
        // 先有一份"改之前"的状态
        var before = ExpressSnapshotPayload.ToJson(_express.Current);

        var set = JsonSerializer.SerializeToElement(
            await Set().HandleAsync(Payload(new
            {
                level = "explicit",
                promptExpress = "改过的规则段",
                sessionId = "douyin:p1000000000",
                sessionLevel = "explicit",
            }), CancellationToken.None));

        var snapshotId = set.GetProperty("snapshotId").GetInt64();
        Assert.NotEqual(before, ExpressSnapshotPayload.ToJson(_express.Current));

        var restored = JsonSerializer.SerializeToElement(
            await Rollback().HandleAsync(Payload(new { id = snapshotId }), CancellationToken.None));

        Assert.True(restored.GetProperty("restored").GetBoolean());
        Assert.Equal("daily", restored.GetProperty("level").GetString());
        Assert.False(restored.GetProperty("promptExpress").GetProperty("customized").GetBoolean());
        Assert.Empty(_express.Current.SessionOverrides);

        // A-24：回滚后与改动前**逐字节一致**（updatedAt/By 是回滚动作自己的，单独比）
        var actual = ExpressSnapshotPayload.TryParse(
            ExpressSnapshotPayload.ToJson(_express.Current), out var settings, out _);
        Assert.True(actual);
        Assert.Equal(before, ExpressSnapshotPayload.ToJson(_express.Current with
        {
            UpdatedAtMs = 0,
            UpdatedBy = null,
        }));
        Assert.Equal(ExpressLevel.Daily, settings.Level);
    }

    [Fact]
    public async Task Rollback_MissingSnapshot_ReturnsBadRequest()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Rollback().HandleAsync(Payload(new { id = 999L }), CancellationToken.None));

        Assert.Equal(ApiResponse.BadRequest, ex.Code);
        Assert.Contains("快照不存在", ex.Message);
    }

    [Fact]
    public async Task Rollback_ChecksumMismatch_IsRejected()
    {
        var id = await _snapshots.AppendAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "http-client",
            """{"level":"close"}""",
            "0000",   // 故意写错的校验和 = 库里被手改过
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Rollback().HandleAsync(Payload(new { id }), CancellationToken.None));

        Assert.Equal(ApiResponse.BadRequest, ex.Code);
        Assert.Contains("校验和不一致", ex.Message);
    }

    [Fact]
    public async Task Snapshots_AreTrimmedToFive()
    {
        for (var i = 0; i < 8; i++)
        {
            await Set().HandleAsync(Payload(new
            {
                level = i % 2 == 0 ? "close" : "explicit",
            }), CancellationToken.None);
        }

        var recent = await _snapshots.ListRecentAsync(50, CancellationToken.None);
        Assert.Equal(5, recent.Count);
    }

    [Fact]
    public async Task Snapshots_TrimKeepsAtLeastOne()
    {
        await _snapshots.AppendAsync(1, "http-client", """{"level":"daily"}""", "x", CancellationToken.None);

        await _snapshots.TrimAsync(0, CancellationToken.None);

        var recent = await _snapshots.ListRecentAsync(10, CancellationToken.None);
        Assert.Single(recent);
    }

    [Fact]
    public async Task Set_WritesAuditRow()
    {
        await Set().HandleAsync(Payload(new { level = "close" }), CancellationToken.None);

        var audit = await _configs.ListAuditAsync(10, CancellationToken.None);

        Assert.Contains(audit, e => e.Action == "setExpressLevel");
    }
}

