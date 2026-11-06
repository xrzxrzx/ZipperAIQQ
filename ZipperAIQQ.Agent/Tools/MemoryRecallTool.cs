using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 跨会话语义召回（R-09 评估后的替代方案）：向量 + 关键词 + 时间混合打分。
/// </summary>
/// <remarks>
/// 与 <c>search_other_sessions</c> 的分工：那个是**关键词**精确匹配；
/// 这个是**语义**召回（「记得之前聊过类似的」），并带相关度与来源会话标签。
/// 同属敏感能力（读别的会话），默认管理员。
/// </remarks>
public static class MemoryRecallTool
{
    public const string ToolName = "recall_related";

    public static IEnumerable<AITool> Create(
        MemoryRetriever retriever,
        ToolCallContext context,
        IExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        yield return AIFunctionFactory.Create(
            async (string query, int? topK, CancellationToken ct) =>
            {
                if (!IsEnabled(policy, context.Role, out var denial))
                {
                    return denial;
                }

                if (string.IsNullOrWhiteSpace(query))
                {
                    return "query 不能为空。";
                }

                var take = Math.Clamp(topK ?? 5, 1, 10);
                var hits = await retriever
                    .RecallAcrossSessionsAsync(context.Chat.SessionId, query, take, ct)
                    .ConfigureAwait(false);

                if (hits.Count == 0)
                {
                    return "没有跨会话的相关记录（也可能是其它会话还没建向量）。";
                }

                var lines = hits.Select(h =>
                    $"- [会话 {h.Message.Message.Chat.SessionId}] " +
                    $"{h.Message.Message.OccurredAt:yyyy-MM-dd} " +
                    $"{h.Message.Message.Sender.DisplayName}：" +
                    $"{Clip(h.Message.Message.PlainText)}（相关度 {h.Score:F2}）");

                return "跨会话相关记录：\n" + string.Join('\n', lines);
            },
            new AIFunctionFactoryOptions
            {
                Name = ToolName,
                Description = "按语义跨会话召回相关聊天记录（不带当前会话）：query 为要回忆的内容，topK 最多 10 条。",
            });
    }

    private static string Clip(string text) =>
        text.Length <= 120 ? text : string.Concat(text.AsSpan(0, 120), "…");

    private static bool IsEnabled(IExecutionPolicy policy, Role role, out string denial)
    {
        if (!policy.IsEnabled(PolicyCategory.Tool, ToolName))
        {
            denial = $"工具「{ToolName}」已在客户端被禁用。";
            return false;
        }

        var capability = ToolCatalog.CapabilityOf(ToolName);
        var fallback = capability is null ? Role.Normal : PermissionGate.DefaultRoleOf(capability.Value);
        var required = policy.MinRole(PolicyCategory.Tool, ToolName, fallback);

        if (role < required)
        {
            denial = $"权限不足：工具「{ToolName}」需要 {required} 及以上角色，当前是 {role}。"
                     + "可以让主人/管理员调整你的角色，或者请他们代为执行。";
            return false;
        }

        denial = string.Empty;
        return true;
    }
}
