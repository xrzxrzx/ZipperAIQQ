using System.Runtime.CompilerServices;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Server.Commands;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 命令目录与命令类的一致性：<c>CommandCatalog</c> 是权限默认值的登记处，
/// 命令类的 <see cref="ICommand.MinRole"/> 是运行时值 —— 两边漂移会让
/// <c>setToolPolicy</c> 显示的默认角色与实际权限对不上。
/// </summary>
public sealed class CommandCatalogTests
{
    [Fact]
    public void Every_command_is_registered_with_the_same_min_role()
    {
        var commands = typeof(HelpCommand).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(ICommand).IsAssignableFrom(t))
            .Select(t => (ICommand)RuntimeHelpers.GetUninitializedObject(t))
            .ToList();

        Assert.NotEmpty(commands);

        foreach (var command in commands)
        {
            var entry = CommandCatalog.All
                .FirstOrDefault(c => string.Equals(c.Name, command.Name, StringComparison.Ordinal));

            Assert.False(
                string.IsNullOrWhiteSpace(entry.Name),
                $"命令 {command.GetType().Name}（{command.Name}）没有登记进 CommandCatalog");
            Assert.Equal(entry.MinRole, command.MinRole);
        }
    }

    [Fact]
    public void Catalog_has_no_duplicate_or_unknown_entries()
    {
        var names = CommandCatalog.All.Select(c => c.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(CommandCatalog.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
        Assert.All(CommandCatalog.All, c => Assert.True(Enum.IsDefined(c.MinRole)));
    }
}
