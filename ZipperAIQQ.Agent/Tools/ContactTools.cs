using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Channel;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 让 AI 自主查询平台信息（第 19 项）：好友、群、群成员。
/// </summary>
/// <remarks>
/// <para>
/// 用户要的是「该调什么 API 由 AI 自己判断」—— 所以给一个**通用查询工具**，
/// 而不是十几个专用工具。
/// </para>
/// <para>
/// 但**不能是"什么都能调"的口子**：action 走白名单，只放只读查询；
/// 发消息、退群、禁言这类写操作必须走有权限门禁的专用工具。
/// </para>
/// </remarks>
public static class ContactTools
{
    public const string ToolName = "query_napcat_api";

    public static IEnumerable<AITool> Create(IContactDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        yield return AIFunctionFactory.Create(
            async (string action, string? parameters, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(action))
                {
                    return "请给出要查询的 action。";
                }

                JsonObject? parsed = null;
                if (!string.IsNullOrWhiteSpace(parameters))
                {
                    try
                    {
                        parsed = JsonNode.Parse(parameters) as JsonObject ?? new JsonObject();
                    }
                    catch (Exception)
                    {
                        return "parameters 不是合法 JSON（要形如 {\"group_id\":\"123456\"}）。";
                    }
                }

                try
                {
                    var data = await directory.InvokeApiAsync(action.Trim(), parsed, ct).ConfigureAwait(false);
                    return data is null
                        ? $"查询 {action} 没有返回数据（可能参数不对或平台拒绝）。"
                        : $"`{action}` 结果：\n{data.ToJsonString()}";
                }
                catch (InvalidOperationException ex)
                {
                    // 白名单拒绝：把「能查什么」直接告诉它，避免它反复试
                    return $"{ex.Message}\n可用 action：get_friend_list / get_group_list / " +
                           "get_group_info / get_group_member_list / get_group_member_info / " +
                           "get_stranger_info / get_login_info / get_version_info";
                }
            },
            name: ToolName,
            description:
                "查询 QQ 侧的信息（只读）。可用 action：\n" +
                "- get_friend_list：好友列表（userId/nickname/remark）\n" +
                "- get_group_list：我所在的群列表\n" +
                "- get_group_info：群信息，参数 {\"group_id\":\"...\"}\n" +
                "- get_group_member_list：群成员（含群名片 card、角色 role、**群头衔 title**）\n" +
                "- get_group_member_info：单个成员，参数 {\"group_id\":\"...\",\"user_id\":\"...\"}\n" +
                "- get_stranger_info：陌生人资料，参数 {\"user_id\":\"...\"}\n" +
                "parameters 是 JSON 字符串。用户问「这个群有多少人 / 某人什么头衔 / 他是管理吗」时用它。");
    }
}
