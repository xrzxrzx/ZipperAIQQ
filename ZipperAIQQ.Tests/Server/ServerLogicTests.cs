using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Reply;

namespace ZipperAIQQ.Tests.Server;

/// <summary>数值型运行参数的白名单与范围校验。</summary>
/// <remarks>
/// 校验必须在服务端（UI 的 NumberBox 只是引导）——
/// 否则一个手写 HTTP 请求就能把冷却时间设成负数。
/// </remarks>
public sealed class NumericSettingsSchemaTests
{
    [Theory]
    [InlineData("send.intervalMs", 0)]
    [InlineData("send.intervalMs", 300)]
    [InlineData("send.intervalMs", 10_000)]
    public void Valid_values_pass(string key, double value)
        => Assert.True(NumericSettingsSchema.TryValidate(key, value, out var error), error);

    [Theory]
    [InlineData("send.intervalMs", -1)]
    [InlineData("send.intervalMs", 10_001)]
    public void Out_of_range_is_rejected(string key, double value)
    {
        Assert.False(NumericSettingsSchema.TryValidate(key, value, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Unknown_key_is_rejected()
    {
        Assert.False(NumericSettingsSchema.TryValidate("不存在的键", 1, out var error));
        Assert.Contains("未知参数", error);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_finite_is_rejected(double value)
    {
        Assert.False(NumericSettingsSchema.TryValidate(NumericSettingsSchema.SendIntervalKey, value, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ─────────────────── JEV 判断灵敏度（新增项）───────────────────

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.55)]
    [InlineData(0.95)]
    public void Judge_threshold_accepts_valid_range(double value)
        => Assert.True(NumericSettingsSchema.TryValidate(NumericSettingsSchema.JudgeThresholdKey, value, out var error), error);

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void Judge_threshold_rejects_out_of_range(double value)
    {
        Assert.False(NumericSettingsSchema.TryValidate(NumericSettingsSchema.JudgeThresholdKey, value, out var error));
        Assert.Contains("JEV 判断灵敏度", error);
    }

    [Fact]
    public void Judge_threshold_default_is_neutral()
    {
        var setting = NumericSettingsSchema.Find(NumericSettingsSchema.JudgeThresholdKey);

        Assert.NotNull(setting);
        Assert.Equal(0.55, setting.Default);
    }

    [Fact]
    public void All_declared_keys_are_findable()
    {
        // 白名单自己不能有"声明了但查不到"的项
        foreach (var setting in NumericSettingsSchema.All)
        {
            Assert.NotNull(NumericSettingsSchema.Find(setting.Key));
        }
    }
}

/// <summary>
/// Decisions API 的地址推导。
/// </summary>
/// <remarks>
/// 用户在客户端填的是 chat 用的 baseUrl（<c>.../api/v1</c>），
/// 而 decisions 在 <c>.../api/alpha/decisions</c> —— 推导错了就是 404。
/// </remarks>
public sealed class DecisionsUrlTests
{
    [Theory]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/alpha/decisions")]
    [InlineData("https://openrouter.ai/api", "https://openrouter.ai/api/alpha/decisions")]
    [InlineData("https://openrouter.ai/", "https://openrouter.ai/api/alpha/decisions")]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/api/alpha/decisions")]
    public void Resolves_from_origin_regardless_of_path(string baseUrl, string expected)
    {
        // 从 origin 重拼、不猜路径：填什么 path 都能得到正确地址
        Assert.Equal(expected, DecisionsJudgeClient.ResolveDecisionsUrl(baseUrl).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("不是 URL")]
    [InlineData("ftp://x/y")]
    public void Rejects_invalid_base_url(string baseUrl)
        => Assert.ThrowsAny<Exception>(() => DecisionsJudgeClient.ResolveDecisionsUrl(baseUrl));
}

/// <summary>配置校验：必需项在 YAML 合并之后统一校验（只写 YAML 也能启动）。</summary>
public sealed class ServerOptionsTests
{
    [Fact]
    public void ValidateRequired_passes_when_all_required_present()
    {
        var options = new ServerOptions
        {
            NapCatWebSocketUrl = "ws://127.0.0.1:3001",
            LlmApiKey = "k",
            LlmBaseUrl = "https://example.test",
            LlmModel = "m",
        };

        options.ValidateRequired();
    }

    [Fact]
    public void ValidateRequired_names_every_missing_item()
    {
        var options = new ServerOptions
        {
            NapCatWebSocketUrl = string.Empty,
            LlmApiKey = string.Empty,
            LlmBaseUrl = string.Empty,
            LlmModel = string.Empty,
        };

        var ex = Assert.Throws<InvalidOperationException>(options.ValidateRequired);

        Assert.Contains("ZIPPER_NAPCAT_WS", ex.Message);
        Assert.Contains("ZIPPER_LLM_APIKEY", ex.Message);
        Assert.Contains("ZIPPER_LLM_BASEURL", ex.Message);
        Assert.Contains("ZIPPER_LLM_MODEL", ex.Message);
    }

    [Fact]
    public void Yaml_only_config_is_enough()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zipper-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "server.yaml");
        File.WriteAllText(path, """
            napcat:
              wsUrl: "ws://127.0.0.1:3999"
            llm:
              apiKey: "yaml-key"
              baseUrl: "https://yaml.test"
              model: "yaml-model"
            """);

        try
        {
            var options = ServerConfigLoader.Load(path);

            Assert.Equal("ws://127.0.0.1:3999", options.NapCatWebSocketUrl);
            Assert.Equal("yaml-key", options.LlmApiKey);
            Assert.Equal("https://yaml.test", options.LlmBaseUrl);
            Assert.Equal("yaml-model", options.LlmModel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
