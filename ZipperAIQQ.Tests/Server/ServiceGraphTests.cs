using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Server.Composition;
using ZipperAIQQ.Server.Configuration;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 服务图自检：**所有注册的依赖都必须在构建期可解析**。
/// </summary>
/// <remarks>
/// 2026-11-06 的教训：把 <c>QuotaOptions</c> 换成运行时快照后，<c>SpendReader</c> 还在要旧类型，
/// 结果编译通过、测试全绿、**部署启动才炸**（DI 在 Program.cs 里才解析）。
/// 这条用例让同类错误在 CI 阶段就暴露。
/// </remarks>
public sealed class ServiceGraphTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }

    [Fact]
    public void Service_graph_has_no_missing_dependencies()
    {
        var services = new ServiceCollection();
        services.AddSerilog(new LoggerConfiguration().CreateLogger(), dispose: false);
        services.AddDataProtection();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());

        services.AddZipperServices(
            new ServerOptions
            {
                NapCatWebSocketUrl = "ws://127.0.0.1:1",
                LlmApiKey = "test",
                LlmBaseUrl = "https://api.example",
                LlmModel = "deepseek-chat",
                HttpAddress = "127.0.0.1:5180",
            },
            "Data Source=:memory:");

        // ValidateOnBuild：构建期就检查每个构造函数的依赖；ValidateScopes：避免单例抓作用域服务
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider);
    }
}
