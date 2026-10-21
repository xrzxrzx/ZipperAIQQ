using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent.Security;

/// <summary>
/// 会话工作区：路径解析 + **路径穿越拦截**（FR-7.1 / 7.2 / 7.6）。
/// </summary>
/// <remarks>
/// <para>
/// 目录布局：<c>{root}/{platform}/{g|p}{id}/</c>。
/// </para>
/// <para>
/// ⚠️ 校验的是**解析后的真实路径**，不是原始入参 ——
/// 只看字符串前缀，一个符号链接就能绕过去。
/// </para>
/// </remarks>
public sealed class WorkspacePathResolver
{
    private readonly string _root;

    public WorkspacePathResolver(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <summary>工作区根目录（绝对路径）。</summary>
    public string Root => _root;

    /// <summary>该会话的目录（绝对路径）。</summary>
    public string GetSessionDirectory(ChatRef chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var sub = chat.Type == ChatType.Group ? "g" + chat.Id : "p" + chat.Id;
        return Path.Combine(_root, chat.Platform, sub);
    }

    /// <summary>
    /// 把模型给的相对路径解析成会话目录内的绝对路径；任何逃逸都拒绝。
    /// </summary>
    /// <param name="absolute">解析成功时的绝对路径（**已规范化**）。</param>
    /// <param name="error">拒绝原因（会回填给模型）。</param>
    public bool TryResolve(ChatRef chat, string? relative, out string absolute, out string? error)
    {
        absolute = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relative))
        {
            error = "路径不能为空。";
            return false;
        }

        if (relative.Contains('\0'))
        {
            error = "路径包含非法字符。";
            return false;
        }

        // 绝对路径一律拒绝（盘符 / UNC / Unix 风格都不接受）
        if (Path.IsPathRooted(relative))
        {
            error = "拒绝绝对路径：只接受相对于本会话工作区的路径。";
            return false;
        }

        var sessionDir = GetSessionDirectory(chat);
        var combined = Path.GetFullPath(Path.Combine(sessionDir, relative));

        if (!IsUnder(combined, sessionDir))
        {
            error = "拒绝路径穿越：解析后的路径不在本会话工作区内。";
            return false;
        }

        // 符号链接：逐段解析真实目标，确认真实路径仍在本会话工作区内
        var realTarget = ResolveLinks(combined);
        var realSessionDir = ResolveLinks(sessionDir);
        if (!IsUnder(realTarget, realSessionDir))
        {
            error = "拒绝符号链接逃逸：真实路径不在本会话工作区内。";
            return false;
        }

        absolute = combined;
        return true;
    }

    /// <summary>路径是否在根内（带分隔符比较，避免 <c>chat1</c> 命中 <c>chat10</c>）。</summary>
    private static bool IsUnder(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var trimmedPath = Path.TrimEndingDirectorySeparator(path);

        if (string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = trimmedRoot + Path.DirectorySeparatorChar;
        return trimmedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从根开始**逐段**解析符号链接，返回最终真实路径。
    /// </summary>
    /// <remarks>
    /// 只解析"最后一级"是不够的：父目录本身就可能是指向别处的链接。
    /// </remarks>
    private static string ResolveLinks(string absolute)
    {
        var root = Path.GetPathRoot(absolute) ?? string.Empty;
        var rest = absolute[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        var built = root;
        foreach (var part in rest)
        {
            built = Path.Combine(built, part);

            if (!File.Exists(built) && !Directory.Exists(built))
            {
                // 还不存在的段（新建文件场景）没有链接可解析
                continue;
            }

            FileSystemInfo info = Directory.Exists(built) ? new DirectoryInfo(built) : new FileInfo(built);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                built = target.FullName;
            }
        }

        return Path.GetFullPath(built);
    }
}
