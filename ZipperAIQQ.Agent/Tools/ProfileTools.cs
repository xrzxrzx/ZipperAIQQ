using System.ComponentModel;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 写自己资料 / 戳别人的工具：<c>set_my_signature</c> / <c>poke_someone</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这几个是**对外可见的写操作**：改了签名，所有加我的人都看得到；戳了谁，对方会收到提示。
/// 所以设计上和文件工具一样走权限门禁，而且**必须留审计**（谁让改的、改成了什么）。
/// </para>
/// <para>
/// 只在权限允许时才把工具给模型 —— 否则它会去调一个自己没有的能力，然后幻觉"我改了"。
/// </para>
/// </remarks>
public static class ProfileTools
{
    public const string SetSignatureToolName = "set_my_signature";
    public const string PokeToolName = "poke_someone";

    private const string Actor = "tool:profile";

    public static IEnumerable<AITool> Create(
        IContactDirectory directory,
        ToolCallContext context,
        IPermissionGate gate,
        IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(gate);

        // ── 改自己的签名 ──
        if (gate.IsAllowed(ToolCapability.WriteProfile, context.Role))
        {
            yield return AIFunctionFactory.Create(
                async (string signature, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(signature))
                    {
                        return "签名不能为空。";
                    }

                    var decision = gate.Check(ToolCapability.WriteProfile, context);
                    if (!decision.Allowed)
                    {
                        return decision.Reason ?? "没有权限改签名。";
                    }

                    var trimmed = signature.Trim();

                    try
                    {
                        await directory.SetSignatureAsync(trimmed, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return $"改签名失败：{ex.Message}";
                    }

                    await configs
                        .AppendAuditAsync("setSignature", Actor, $"「{Truncate(trimmed, 60)}」", ct)
                        .ConfigureAwait(false);

                    return $"签名已改成：{trimmed}";
                },
                name: SetSignatureToolName,
                description:
                    "改**你自己的** QQ 个性签名（对外可见，所有加了你的人都能看到）。\n" +
                    "只在对方明确要求时用；不要自己找理由改。\n" +
                    "参数：signature（新签名内容）。");
        }

        // ── 戳一戳 ──
        if (gate.IsAllowed(ToolCapability.SendPoke, context.Role))
        {
            yield return AIFunctionFactory.Create(
                async (string userId, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return "要戳谁？给我一个 QQ 号。";
                    }

                    var decision = gate.Check(ToolCapability.SendPoke, context);
                    if (!decision.Allowed)
                    {
                        return decision.Reason ?? "没有权限戳别人。";
                    }

                    // 群里戳要带群号，私聊留空
                    var groupId = context.Chat.Type == ChatType.Group ? context.Chat.Id : null;

                    try
                    {
                        await directory.SendPokeAsync(userId.Trim(), groupId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return $"戳失败：{ex.Message}";
                    }

                    await configs
                        .AppendAuditAsync("sendPoke", Actor, $"user={userId} group={groupId ?? "-"}", ct)
                        .ConfigureAwait(false);

                    return $"戳了 {userId} 一下。";
                },
                name: PokeToolName,
                description:
                    "戳一戳某个 QQ 用户（对方会收到「拍了拍」提示）。\n" +
                    "适合撒娇、提醒、开玩笑；**别频繁用**，那是骚扰。\n" +
                    "参数：userId（对方 QQ 号）。");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
