using Microsoft.Extensions.DependencyInjection;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Commands;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// <c>/help</c> 出图（决策八 / S3-D 的 D3）：角色过滤 + 图片 + 文本兜底。
/// </summary>
/// <remarks>
/// 图片内容不做像素级断言（渲染细节会随字体/版本变），只钉三件事：
/// ① 真的产出了 PNG；② 条数按角色过滤；③ 文本版始终可用（渲染失败时的退路）。
/// </remarks>
public sealed class HelpCommandTests
{
    private sealed class FakeCommand(string name, Role minRole) : ICommand
    {
        public string Name => name;

        public string Description => $"{name} 的说明";

        public Role MinRole => minRole;

        public Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default) =>
            Task.FromResult($"/{name} 已执行");
    }

    private static CommandContext Context(Role role) => new(
        Chat: new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
        Sender: new SenderInfo { UserId = "10001", Nickname = "测试" },
        Role: role,
        Now: DateTimeOffset.UnixEpoch);

    private static HelpCommand CreateHelp(params ICommand[] commands)
    {
        var services = new ServiceCollection();
        foreach (var command in commands)
        {
            services.AddSingleton(command);
        }

        return new HelpCommand(services.BuildServiceProvider());
    }

    private static readonly ICommand[] AllCommands =
    [
        new FakeCommand("help", Role.Normal),
        new FakeCommand("status", Role.Normal),
        new FakeCommand("purge", Role.Admin),
    ];

    [Fact]
    public async Task ExecuteWithImageAsync_NormalRole_ReturnsPngWithOnlyVisibleCommands()
    {
        var help = CreateHelp(AllCommands);

        var (text, png) = await help.ExecuteWithImageAsync(Context(Role.Normal), string.Empty);

        Assert.NotNull(png);
        Assert.NotEmpty(png!);
        // PNG 魔数：确认是图片而不是别的东西
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png![..4]);

        // 普通成员只该看到 help / status 两条（purge 是 Admin）
        Assert.Contains("2 条", text, StringComparison.Ordinal);
        Assert.DoesNotContain("purge", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteWithImageAsync_AdminRole_IncludesAdminOnlyCommand()
    {
        var help = CreateHelp(AllCommands);

        var (text, png) = await help.ExecuteWithImageAsync(Context(Role.Admin), string.Empty);

        Assert.NotNull(png);
        Assert.Contains("3 条", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_TextVersion_StillListsEveryVisibleCommand()
    {
        // 文本版是"渲染失败时的退路"，必须始终完整（不是只给一句说明）
        var help = CreateHelp(AllCommands);

        var text = await help.ExecuteAsync(Context(Role.Normal), string.Empty);

        Assert.Contains("/help", text, StringComparison.Ordinal);
        Assert.Contains("/status", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/purge", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteWithImageAsync_NoCommands_StillRendersHeader()
    {
        // 一个命令都没注册（理论上不会，但别让 /help 自己炸）
        var help = CreateHelp();

        var (text, png) = await help.ExecuteWithImageAsync(Context(Role.Normal), string.Empty);

        Assert.NotNull(png);
        Assert.Contains("0 条", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpCommand_ImplementsImageCommand()
    {
        // 路由靠这个接口决定走"文本+图片"路径（CommandRouter.cs:120）
        Assert.IsAssignableFrom<IImageCommand>(CreateHelp());
    }
}
