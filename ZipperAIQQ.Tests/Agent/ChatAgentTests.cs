using System.Reflection;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// ChatAgent 的历史窗口行为（拆分前的第一批测试）。
/// </summary>
/// <remarks>
/// 只测不碰外部依赖的纯内存路径：<see cref="ChatAgent.RememberCommand"/> 与
/// <see cref="ChatAgent.ResetHistory"/>。RunAsync 的完整链路需要 LLM / 通道 / 库的假实现，
/// 留到后续批次再补，先给「拆分前必须有的安全网」打底。
/// </remarks>
public sealed class ChatAgentTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void RememberCommand_keeps_history_within_hot_window()
    {
        var agent = NewAgent(hotWindow: 5);

        for (var i = 0; i < 10; i++)
        {
            agent.RememberCommand($"用户说 {i}", $"命令结果 {i}");
        }

        var history = HistoryOf(agent);
        Assert.True(history.Count <= 5, $"历史应被裁剪到热窗 5 条以内，实际 {history.Count}");
        Assert.All(history, m => Assert.Equal(ChatRole.User, m.Role));
    }

    [Fact]
    public void RememberCommand_ignores_empty_command_response()
    {
        var agent = NewAgent();
        var before = HistoryOf(agent).Count;

        agent.RememberCommand("用户说点什么", "   ");

        Assert.Equal(before, HistoryOf(agent).Count);
    }

    [Fact]
    public void RememberCommand_marks_command_result_so_model_does_not_fake_it()
    {
        var agent = NewAgent();

        agent.RememberCommand("/status", "当前模型 deepseek-flash");

        var history = HistoryOf(agent);
        var last = history[^1];
        Assert.Contains("[命令结果]", last.Text);
        Assert.Contains("deepseek-flash", last.Text);
    }

    [Fact]
    public void ResetHistory_clears_memory_and_marks_loaded()
    {
        var agent = NewAgent();
        agent.RememberCommand("a", "b");
        Assert.NotEmpty(HistoryOf(agent));

        agent.ResetHistory();

        Assert.Empty(HistoryOf(agent));

        var loadTask = typeof(ChatAgent)
            .GetField("_historyLoadTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent) as Task;
        Assert.True(loadTask?.IsCompletedSuccessfully);
    }

    private static ChatAgent NewAgent(int hotWindow = 20)
    {
        // 这些依赖只在 RunAsync 路径使用；本组测试只走内存历史，传 null 不会被执行到。
        var deps = new ChatAgentDependencies
        {
            Client = null!,
            Channel = null!,
            Store = null!,
            Faces = null!,
            Retriever = null!,
            Summaries = null!,
            SummaryStore = null!,
            Personas = null!,
            Metrics = null!,
            Events = null!,
            PermissionGate = null!,
            Policy = null!,
            Roles = null!,
            Workspace = null!,
            Schedules = null!,
            Options = new ChatAgentOptions { HotWindowMessages = hotWindow },
            Logger = Logger,
        };

        return new ChatAgent("qq:p1", deps, null!, null!);
    }

    private static List<ChatMessage> HistoryOf(ChatAgent agent) =>
        (List<ChatMessage>)typeof(ChatAgent)
            .GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
}
