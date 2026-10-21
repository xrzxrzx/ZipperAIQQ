using System.Text;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 文件工具 6 件套（FR-7.5）：列 / 读 / 写 / 改 / 移 / 删。
/// </summary>
/// <remarks>
/// <para>
/// **两道闸**：① 不可用的工具**根本不给模型看**（省 token，也少试探）；
/// ② 每个工具执行前**再过一次门禁**（防止角色判定被绕过）。
/// </para>
/// <para>
/// **路径一律走 <see cref="WorkspacePathResolver"/>** —— 校验的是解析后的真实路径，
/// 不是模型给的原始字符串（FR-7.6）。
/// </para>
/// <para>
/// 失败一律**返回说明**而不是抛异常：模型需要知道「为什么不行」才能换个方式做（FR-6.4）。
/// </para>
/// </remarks>
public static class FileTools
{
    public static IReadOnlyList<AITool> Create(
        ToolCallContext context,
        IPermissionGate gate,
        WorkspacePathResolver workspace,
        int maxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(workspace);

        var limit = Math.Max(1, maxFileBytes);
        var tools = new List<AITool>();

        if (gate.IsAllowed(ToolCapability.ReadFile, context.Role))
        {
            tools.Add(CreateList(context, gate, workspace));
            tools.Add(CreateRead(context, gate, workspace, limit));
        }

        if (gate.IsAllowed(ToolCapability.WriteFile, context.Role))
        {
            tools.Add(CreateWrite(context, gate, workspace, limit));
            tools.Add(CreateEdit(context, gate, workspace, limit));
            tools.Add(CreateMove(context, gate, workspace));
        }

        if (gate.IsAllowed(ToolCapability.DeleteFile, context.Role))
        {
            tools.Add(CreateDelete(context, gate, workspace));
        }

        return tools;
    }

    // ───────────── 查 ─────────────

    private static AIFunction CreateList(ToolCallContext context, IPermissionGate gate, WorkspacePathResolver workspace)
    {
        return AIFunctionFactory.Create(
            (string path) =>
            {
                if (Deny(gate, ToolCapability.ReadFile, context) is { } denied)
                {
                    return denied;
                }

                if (!workspace.TryResolve(context.Chat, path, out var dir, out var error))
                {
                    return error!;
                }

                if (!Directory.Exists(dir))
                {
                    return $"目录不存在：{path}";
                }

                var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().Take(200).ToList();
                if (entries.Count == 0)
                {
                    return $"目录为空：{path}";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"目录 {path} 共 {entries.Count} 项：");
                foreach (var entry in entries)
                {
                    var kind = entry is DirectoryInfo ? "目录" : "文件";
                    var size = entry is FileInfo file ? $"  {file.Length} 字节" : string.Empty;
                    sb.AppendLine($"- {entry.Name}（{kind}）{size}");
                }

                if (entries.Count >= 200)
                {
                    sb.AppendLine("（只列出前 200 项）");
                }

                return sb.ToString().TrimEnd();
            },
            name: "file_list",
            description:
                "列出**本会话工作区**内某个目录的内容。path 传 \".\" 表示工作区根目录。" +
                "只接受相对路径；越界路径会被拒绝。");
    }

    private static AIFunction CreateRead(
        ToolCallContext context, IPermissionGate gate, WorkspacePathResolver workspace, int maxBytes)
    {
        return AIFunctionFactory.Create(
            (string path) =>
            {
                if (Deny(gate, ToolCapability.ReadFile, context) is { } denied)
                {
                    return denied;
                }

                if (!workspace.TryResolve(context.Chat, path, out var file, out var error))
                {
                    return error!;
                }

                if (!File.Exists(file))
                {
                    return $"文件不存在：{path}";
                }

                var length = new FileInfo(file).Length;
                if (length > maxBytes)
                {
                    // FR-7.7：超限**拒绝**，不静默截断 —— 半截文件比没读到更误导
                    return $"文件过大：{length} 字节，超过上限 {maxBytes} 字节，拒绝读取。";
                }

                return File.ReadAllText(file, Encoding.UTF8);
            },
            name: "file_read",
            description:
                "读取**本会话工作区**内的文件内容。只接受相对路径；超过大小上限会拒绝（不会截断）。");
    }

    // ───────────── 增 / 改 ─────────────

    private static AIFunction CreateWrite(
        ToolCallContext context, IPermissionGate gate, WorkspacePathResolver workspace, int maxBytes)
    {
        return AIFunctionFactory.Create(
            (string path, string content) =>
            {
                if (Deny(gate, ToolCapability.WriteFile, context) is { } denied)
                {
                    return denied;
                }

                if (!workspace.TryResolve(context.Chat, path, out var file, out var error))
                {
                    return error!;
                }

                if (content is null)
                {
                    return "内容不能为空（要清空请写空字符串）。";
                }

                var bytes = Encoding.UTF8.GetByteCount(content);
                if (bytes > maxBytes)
                {
                    return $"内容过大：{bytes} 字节，超过上限 {maxBytes} 字节，拒绝写入。";
                }

                var parent = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return $"已写入 {bytes} 字节：{path}";
            },
            name: "file_write",
            description:
                "在**本会话工作区**新建或**整文件覆盖**写入。只接受相对路径；" +
                "只改一小部分请优先用 file_edit。");
    }

    private static AIFunction CreateEdit(
        ToolCallContext context, IPermissionGate gate, WorkspacePathResolver workspace, int maxBytes)
    {
        return AIFunctionFactory.Create(
            (string path, string oldText, string newText) =>
            {
                if (Deny(gate, ToolCapability.WriteFile, context) is { } denied)
                {
                    return denied;
                }

                if (!workspace.TryResolve(context.Chat, path, out var file, out var error))
                {
                    return error!;
                }

                if (!File.Exists(file))
                {
                    return $"文件不存在：{path}";
                }

                if (string.IsNullOrEmpty(oldText))
                {
                    return "要替换的片段（oldText）不能为空。";
                }

                var original = File.ReadAllText(file, Encoding.UTF8);

                var first = original.IndexOf(oldText, StringComparison.Ordinal);
                if (first < 0)
                {
                    return $"没有找到要替换的片段（文件 {path}）。请先用 file_read 确认原文。";
                }

                // 必须唯一：多处命中时盲目替换容易改坏"它没看见的那部分"
                if (original.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
                {
                    return "要替换的片段在文件里出现了多次，拒绝替换。请给出更长的、唯一的片段。";
                }

                var updated = string.Concat(original.AsSpan(0, first), newText ?? string.Empty, original.AsSpan(first + oldText.Length));

                var bytes = Encoding.UTF8.GetByteCount(updated);
                if (bytes > maxBytes)
                {
                    return $"修改后内容过大：{bytes} 字节，超过上限 {maxBytes} 字节，拒绝写入。";
                }

                File.WriteAllText(file, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return $"已替换 1 处：{path}";
            },
            name: "file_edit",
            description:
                "在**本会话工作区**内做**定向替换**（oldText → newText）。" +
                "oldText 必须在文件中**唯一出现**；整文件重写请用 file_write。");
    }

    private static AIFunction CreateMove(ToolCallContext context, IPermissionGate gate, WorkspacePathResolver workspace)
    {
        return AIFunctionFactory.Create(
            (string from, string to) =>
            {
                if (Deny(gate, ToolCapability.WriteFile, context) is { } denied)
                {
                    return denied;
                }

                if (!workspace.TryResolve(context.Chat, from, out var source, out var error))
                {
                    return error!;
                }

                if (!workspace.TryResolve(context.Chat, to, out var target, out error))
                {
                    return error!;
                }

                if (!File.Exists(source))
                {
                    return $"源文件不存在：{from}";
                }

                if (File.Exists(target) || Directory.Exists(target))
                {
                    // 不覆盖：覆盖是不可逆的意外，交给模型显式删了再移
                    return $"目标已存在：{to}。要覆盖请先删除目标。";
                }

                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Move(source, target);
                return $"已移动：{from} → {to}";
            },
            name: "file_move",
            description:
                "在**本会话工作区**内移动或重命名文件。目标已存在时**拒绝覆盖**。");
    }

    // ───────────── 删 ─────────────

    private static AIFunction CreateDelete(ToolCallContext context, IPermissionGate gate, WorkspacePathResolver workspace)
    {
        return AIFunctionFactory.Create(
            (string path) =>
            {
                if (Deny(gate, ToolCapability.DeleteFile, context) is { } denied)
                {
                    return denied;
                }

                if (!workspace.TryResolve(context.Chat, path, out var file, out var error))
                {
                    return error!;
                }

                if (Directory.Exists(file))
                {
                    // 目录删除要递归，风险远高于单文件 —— M1 不做
                    return "拒绝删除目录：目前只支持删除单个文件。";
                }

                if (!File.Exists(file))
                {
                    return $"文件不存在：{path}";
                }

                File.Delete(file);
                return $"已删除：{path}";
            },
            name: "file_delete",
            description:
                "删除**本会话工作区**内的单个文件。**没有 undo**，请确认后再调用；不接受目录。");
    }

    private static string? Deny(IPermissionGate gate, ToolCapability capability, ToolCallContext context)
    {
        var decision = gate.Check(capability, context);
        return decision.Allowed ? null : decision.Reason;
    }
}
