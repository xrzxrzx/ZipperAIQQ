using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent;

/// <summary>
/// 语音段给模型看的文字（B00001）。
/// </summary>
/// <remarks>
/// <para>
/// 三种情形必须分开：
/// ① 转写成功 → <c>[语音转写:…]</c>；
/// ② 试过但没拿到 → <c>[语音（没能转成文字）]</c> —— 模型该追问一句「语音没听清」；
/// ③ 没试（转写关闭） → <c>[语音]</c>。
/// </para>
/// <para>
/// 只给一个 <c>[语音]</c> 时，模型会把「没听清」和「对方发了段没内容的语音」混成一件事 ——
/// 而这两种情况该说的话完全不同。
/// </para>
/// <para>
/// 抽成公开纯函数（而不是留在 <c>ChatAgent</c> 的私有方法里）：这段判断有 3 个分支，
/// 而它唯一的消费者是提示词文本 —— 藏起来就只能靠端到端碰运气。
/// </para>
/// </remarks>
public static class RecordDisplay
{
    /// <summary>渲染一条语音段。</summary>
    public static string Render(RecordSegment record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!string.IsNullOrWhiteSpace(record.Text))
        {
            return "[语音转写:" + record.Text + "]";
        }

        return record.TranscriptionFailed ? "[语音（没能转成文字）]" : "[语音]";
    }
}
