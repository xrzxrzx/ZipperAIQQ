using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZipperAIQQ.Agent.Express;

namespace ZipperAIQQ.Server.Express;

/// <summary>
/// 档位快照的 JSON 形态（表达档位设计 §7.1 / §7.2）——**运行期键与快照表共用同一份**。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要共用：回滚要「逐字节一致」（A-24）。如果运行期键与快照各写一套序列化，
/// 回滚后字段顺序 / 缺省值 / 大小写任何一处不同，A-24 就成了「看起来回滚了」。
/// </para>
/// <para>
/// 规则段（<c>promptExpress</c>）**也在这份 JSON 里**，不另存 <c>prompt.express</c> 键：
/// 两份真相必然漂移，而漂移的表现是「回滚后规则段还是新的」这种最难查的一类。
/// </para>
/// </remarks>
public static class ExpressSnapshotPayload
{
    /// <summary>把档位序列化成规范 JSON（字段顺序固定，便于逐字节比对）。</summary>
    public static string ToJson(ExpressSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var sessions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sessionId, level) in settings.SessionOverrides)
        {
            sessions[sessionId] = ExpressLevelText.ToKey(level);
        }

        return JsonSerializer.Serialize(new
        {
            level = ExpressLevelText.ToKey(settings.Level),
            groupCap = ExpressLevelText.ToKey(settings.GroupCap),
            sessions,
            hygiene = new
            {
                enabled = settings.HygieneEnabled,
                regenerate = settings.HygieneRegenerate,
            },
            promptExpress = settings.CustomRules,
            updatedAtMs = settings.UpdatedAtMs,
            updatedBy = settings.UpdatedBy,
        });
    }

    /// <summary>
    /// 解析快照 JSON。
    /// </summary>
    /// <param name="json">快照正文。</param>
    /// <param name="settings">解析结果；失败时是默认值（日常档）。</param>
    /// <param name="error">失败原因；成功时为 <c>null</c>。</param>
    /// <returns>是否完整解析成功。</returns>
    /// <remarks>
    /// 单项认不出（例如手改成一个不存在的档位）**不算全局失败**：那一项回默认、
    /// 其余照用，并把原因带出去由调用方记日志。整段不是 JSON 才算失败。
    /// </remarks>
    public static bool TryParse(string? json, out ExpressSettings settings, out string? error)
    {
        settings = new ExpressSettings();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "快照为空";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "快照不是 JSON 对象";
                return false;
            }

            var unknown = new List<string>();
            var level = ReadLevel(root, "level", ExpressLevel.Daily, unknown);
            var cap = ReadLevel(root, "groupCap", ExpressLevel.Explicit, unknown);

            var sessions = new Dictionary<string, ExpressLevel>(StringComparer.Ordinal);
            if (root.TryGetProperty("sessions", out var sessionsNode) &&
                sessionsNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in sessionsNode.EnumerateObject())
                {
                    if (ExpressLevelText.TryParse(entry.Value.GetString(), out var over))
                    {
                        sessions[entry.Name] = over;
                    }
                    else
                    {
                        unknown.Add($"sessions.{entry.Name}");
                    }
                }
            }

            var enabled = true;
            var regenerate = true;
            if (root.TryGetProperty("hygiene", out var hygiene) && hygiene.ValueKind == JsonValueKind.Object)
            {
                enabled = ReadBool(hygiene, "enabled", true);
                regenerate = ReadBool(hygiene, "regenerate", true);
            }

            settings = new ExpressSettings
            {
                Level = level,
                GroupCap = cap,
                SessionOverrides = sessions,
                HygieneEnabled = enabled,
                HygieneRegenerate = regenerate,
                CustomRules = root.TryGetProperty("promptExpress", out var prompt) &&
                              prompt.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrWhiteSpace(prompt.GetString())
                    ? prompt.GetString()
                    : null,
                UpdatedAtMs = root.TryGetProperty("updatedAtMs", out var at) && at.TryGetInt64(out var ms) ? ms : 0,
                UpdatedBy = root.TryGetProperty("updatedBy", out var by) && by.ValueKind == JsonValueKind.String
                    ? by.GetString()
                    : null,
            };

            if (unknown.Count > 0)
            {
                error = "以下项的档位值认不出，已按默认处理：" + string.Join("、", unknown);
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "快照无法解析：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 快照校验和（SHA-256 十六进制小写）。
    /// </summary>
    /// <remarks>
    /// 回滚前拿它比对：不一致说明库里那份被手改过，
    /// 此时"写回去"等于把一份来路不明的配置当真相，宁可拒绝。
    /// </remarks>
    public static string ChecksumOf(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexStringLower(bytes);
    }

    private static ExpressLevel ReadLevel(
        JsonElement root, string name, ExpressLevel fallback, List<string> unknown)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        if (ExpressLevelText.TryParse(node.GetString(), out var level))
        {
            return level;
        }

        unknown.Add(name);
        return fallback;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var node))
        {
            return fallback;
        }

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }
}
