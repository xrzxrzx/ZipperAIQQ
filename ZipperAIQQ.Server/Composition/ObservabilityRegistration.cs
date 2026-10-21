using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Server.Configuration;

namespace ZipperAIQQ.Server.Composition;

/// <summary>日志与可观测性装配。</summary>
/// <remarks>
/// 与业务装配分开：这两块是**横切**的，改动理由和业务完全不同
/// （一个是"日志往哪写"，一个是"服务怎么组装"）。
/// </remarks>
public static class ObservabilityRegistration
{
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    private const string FileTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// 配置 Serilog 并交给 Host 托管生命周期。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>文件日志用 <b>UTF-8 无 BOM</b>：带 BOM 会让 tail/less 把首行显示成乱码。</item>
    /// <item>文件日志<b>不依赖控制台代码页</b>：之前靠重定向看日志，PowerShell 按 GBK 转码，中文全乱。</item>
    /// <item>组件一律通过构造参数注入 <see cref="ILogger"/>，不碰静态 <c>Log.Logger</c>。</item>
    /// </list>
    /// </remarks>
    public static IHostApplicationBuilder AddZipperLogging(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 同时赋给静态 Log.Logger：DI 容器建好之前（建库、配置校验）也要能记日志。
        // 之后所有权交给 Host（dispose: false，由 finally 里的 CloseAndFlush 收尾）。
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            .WriteTo.File(
                path: Path.Combine("logs", "zipper-.log"),
                rollingInterval: RollingInterval.Day,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                outputTemplate: FileTemplate,
                shared: true)
            .CreateLogger();

        builder.Services.AddSerilog(Log.Logger, dispose: false);

        return builder;
    }

    /// <summary>
    /// 接入 OpenTelemetry 追踪（FR-10）。
    /// </summary>
    /// <remarks>
    /// Console 导出默认关闭（会把控制台刷满），需要时设 <c>ZIPPER_OTEL_CONSOLE=1</c>。
    /// </remarks>
    public static IHostApplicationBuilder AddZipperTelemetry(
        this IHostApplicationBuilder builder,
        ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.Services.AddOpenTelemetry()
            // 没有它，接到 Grafana/Jaeger 后所有数据会显示成 unknown_service
            .ConfigureResource(resource => resource.AddService("ZipperAIQQ.Server"))
            .WithTracing(tracing =>
            {
                tracing.AddSource(AgentTelemetry.SourceName)
                       .AddHttpClientInstrumentation();

                if (options.OtelConsole)
                {
                    tracing.AddConsoleExporter();
                }
            });

        return builder;
    }
}
