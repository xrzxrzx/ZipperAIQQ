using System.Globalization;
using System.Text;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Commands;

/// <summary>
/// <c>/owner</c> —— 查看或转移「主人」身份（**只有主人能用**）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这条命令：主人（<see cref="Role.Owner"/>）在权限模型里是最高级，
/// 但之前**没有任何只有主人能做的事** —— 从行为上根本看不出"谁被设成了主人"。
/// 这条命令把主人身份变成**可观察、可验证**的东西：
/// </para>
/// <list type="bullet">
/// <item>普通成员 / 管理员发 → 路由层直接拒（<see cref="MinRole"/> 是 Owner）</item>
/// <item>主人发 <c>/owner</c> → 看到当前主人是谁</item>
/// <item>主人发 <c>/owner 12345</c> → 转移主人（**不可逆**）</item>
/// </list>
/// <para>
/// 为什么选「转移主人」：这是**只有主人能做、管理员做不到**的最小操作 ——
/// 管理员能改名单、能执行命令，但不能把别人变成主人（否则等于自举提权）。
/// </para>
/// </remarks>
public sealed class OwnerCommand : ICommand
{
    /// <summary>与 <c>GetOwnerAction</c> 同一套键（S3-C：按平台）。</summary>
    private const string Actor = "command:owner";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly IConfigStore _configs;

    public OwnerCommand(IRuntimeSettingsStore settings, IRoleResolver roles, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _roles = roles;
        _configs = configs;
    }

    public string Name => "owner";

    public string Description => "查看 / 转移主人（仅主人可用）";

    /// <summary>**关键**：设成 Owner，管理员也进不来 —— 这才是"主人专属"的可验证形态。</summary>
    public Role MinRole => Role.Owner;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        // 主人是"某个平台上的某个人"（S3-C）：在 QQ 会话里转移的只能是 QQ 的主人
        var platform = AccessKeys.Normalize(context.Chat.Platform);
        var current = await AccessSettings.GetOwnerAsync(_settings, platform, ct).ConfigureAwait(false);
        var trimmed = args.Trim();

        // ── 不带参数：报当前主人 ──
        if (trimmed.Length == 0)
        {
            var who = string.IsNullOrWhiteSpace(current) ? "（未设置）" : current;
            return $"""
                当前主人（{platform}）：{who}
                你：{context.Sender.UserId}（{context.Role}）

                转移主人：/owner <账号>
                ⚠️ 转移后你自己就失去主人权限，不可撤销。
                """;
        }

        // ── 带参数：转移 ──
        var first = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        // QQ 号必须是纯数字；其它平台（如抖音 uid）允许字母等字符
        string newOwner;
        if (string.Equals(platform, AccessKeys.DefaultPlatform, StringComparison.Ordinal))
        {
            if (!long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target) ||
                target <= 0)
            {
                return "参数看起来不是 QQ 号。用法：/owner <QQ号>";
            }

            newOwner = target.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (first.Length == 0)
            {
                return $"参数看起来不是有效的 {platform} 账号。用法：/owner <账号>";
            }

            newOwner = first;
        }

        if (string.Equals(newOwner, current, StringComparison.Ordinal))
        {
            return $"主人已经是 {newOwner} 了，不用改。";
        }

        await _settings.SetAsync(AccessKeys.Owner(platform), newOwner, ct).ConfigureAwait(false);
        if (AccessKeys.HasLegacyKeys(platform))
        {
            // 旧键同步一份：回退到旧版程序时主人不会消失
            await _settings.SetAsync(AccessKeys.LegacyOwnerKey, newOwner, ct).ConfigureAwait(false);
        }

        // 立即生效：权限解析器持有内存里的各平台 owner
        if (_roles is ConfiguredRoleResolver configured)
        {
            configured.SetOwner(platform, newOwner);
        }

        await _configs
            .AppendAuditAsync(
                "transferOwner",
                Actor,
                $"{platform}:{(string.IsNullOrWhiteSpace(current) ? "（空）" : current)} → {newOwner}",
                ct)
            .ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"主人已转移（{platform}）：{(string.IsNullOrWhiteSpace(current) ? "（空）" : current)} → {newOwner}");
        sb.AppendLine();
        sb.AppendLine("⚠️ 你（原主人）现在只是普通权限了。");
        sb.AppendLine("新主人下一条命令起就生效（内存里的 owner 已同步）。");

        return sb.ToString().TrimEnd();
    }
}
