using Microsoft.Extensions.AI;

namespace ZipperAIQQ.Agent.Faces;

/// <summary>
/// <c>list_faces</c> 工具：让 AI **按需**查询可用表情，而不是把整张表塞进提示词。
/// </summary>
public static class ListFacesTool
{
    public const string ToolName = "list_faces";

    public static AIFunction Create(FaceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return AIFunctionFactory.Create(
            (string? keyword) =>
            {
                var matches = catalog.Search(keyword, limit: 15);
                if (matches.Count == 0)
                {
                    return "没有匹配的表情。可以不传关键词看常用表情。";
                }

                var lines = matches.Select(f =>
                    f.Kind == "system"
                        ? $"- {f.Name}"
                        : $"- {f.Name}（自定义表情）");

                return "可用表情：" + Environment.NewLine + string.Join(Environment.NewLine, lines);
            },
            name: ToolName,
            description:
                "查询可用表情。不传关键词返回常用表情；传关键词按名称/标签搜索。" +
                "查到名字后，用 [表情:名字] 写在 send_message 的消息里即可发送。" +
                "**单独一条只写 [表情:名字] 会作为大表情发送**（有特效）。");
    }
}