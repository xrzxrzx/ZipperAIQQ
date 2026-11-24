namespace ZipperAIQQ.Server.Ai.DeepSeekTools;

/// <summary>
/// WAV 切片：给 ASR 的 ≤120 s / ≤8 MiB 硬限制用。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要自己切：平台单次 **≤120 秒 / ≤8 MiB**，超了直接拒。分段之间留**重叠**
/// （默认 1 秒）是因为按帧硬切会把一句话切成两半 —— 重叠让跨边界那句至少在一段里是完整的，
/// 代价只是那 1 秒的文本会重复出现（上层按需去重，比丢字便宜）。
/// </para>
/// <para>
/// 只处理线性 PCM：其它编码（ADPCM / 浮点 / mp3-in-wav）算不准帧边界，硬切就是噪声。
/// 判不出就返回 <c>false</c>，由调用方决定「整段发」还是报错。
/// </para>
/// </remarks>
public static class WavSlicer
{
    private const int WavHeaderBytes = 44;

    /// <summary>解析 WAV 头；不是合法 PCM WAV 时返回 <c>false</c>（**任何畸形输入都不抛**）。</summary>
    public static bool TryParse(byte[] wav, out WavInfo? info)
    {
        info = null;

        if (wav.Length < WavHeaderBytes ||
            !Matches(wav, 0, "RIFF") ||
            !Matches(wav, 8, "WAVE"))
        {
            return false;
        }

        // 用 long 算位置：块长是外部输入，一个声明 0x7FFFFFFF 的坏块就能让 int 回绕成负数
        long pos = 12;
        int audioFormat = 0, channels = 0, sampleRate = 0, blockAlign = 0, bitsPerSample = 0;
        var dataOffset = -1;
        var dataLength = 0;

        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, (int)pos, 4);
            var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                wav.AsSpan((int)pos + 4, 4));
            var body = pos + 8;

            // 先做**减法**比较再判越界：body + size 在这里会溢出
            if (size < 0 || size > wav.Length - body)
            {
                return false;
            }

            if (id == "fmt " && size >= 16)
            {
                audioFormat = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    wav.AsSpan((int)body, 2));
                channels = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    wav.AsSpan((int)body + 2, 2));
                sampleRate = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    wav.AsSpan((int)body + 4, 4));
                blockAlign = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    wav.AsSpan((int)body + 12, 2));
                bitsPerSample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    wav.AsSpan((int)body + 14, 2));
            }
            else if (id == "data")
            {
                dataOffset = (int)body;
                dataLength = size;
            }

            // 块按偶数对齐（RIFF 规定）
            pos = body + size + (size % 2);
        }

        if (dataOffset < 0 || dataLength <= 0 || blockAlign <= 0 || sampleRate <= 0 || audioFormat != 1)
        {
            return false;
        }

        info = new WavInfo(audioFormat, channels, sampleRate, blockAlign, bitsPerSample, dataOffset, dataLength);
        return true;
    }

    /// <summary>
    /// 按时长切片；<paramref name="maxSeconds"/> ≤ 0 或音频本来就短 → 原样返回一段。
    /// </summary>
    /// <remarks>
    /// 只按**时长**切。字节上限（平台的 ≤8 MiB）由调用方按 <see cref="WavInfo.BytesPerSecond"/>
    /// 折算成秒数一起传进来 —— 两个上限必须同时满足，缺一条就会在 44.1 kHz 立体声上炸（实测 60 s 就有 10 MiB）。
    /// </remarks>
    public static IReadOnlyList<WavSlice> Slice(
        byte[] wav, WavInfo info, double maxSeconds, double overlapSeconds = 1.0)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(info);

        if (maxSeconds <= 0 || info.BytesPerSecond <= 0)
        {
            return [new WavSlice(wav, 0, info.DurationMs)];
        }

        var totalFrames = info.DataLength / info.BlockAlign;
        var maxFrames = (int)Math.Floor(maxSeconds * info.SampleRate);
        var overlapFrames = Math.Max(0, (int)(overlapSeconds * info.SampleRate));

        if (maxFrames <= 0 || totalFrames <= maxFrames)
        {
            return [new WavSlice(wav, 0, info.DurationMs)];
        }

        // 步长必须为正，否则死循环
        var step = Math.Max(1, maxFrames - overlapFrames);

        var slices = new List<WavSlice>();
        for (var start = 0; start < totalFrames; start += step)
        {
            var end = Math.Min(start + maxFrames, totalFrames);
            var byteStart = start * info.BlockAlign;
            var byteLength = (end - start) * info.BlockAlign;

            slices.Add(new WavSlice(
                BuildWav(info, wav.AsSpan(info.DataOffset + byteStart, byteLength)),
                start * 1000L / info.SampleRate,
                end * 1000L / info.SampleRate));

            if (end >= totalFrames)
            {
                break;
            }
        }

        return slices;
    }

    /// <summary>
    /// 按原格式重打包成「只有 fmt + data」的干净 WAV（丢掉 JUNK / 封面 / 歌词这类元数据块）。
    /// </summary>
    /// <remarks>
    /// 为什么需要：有些录音笔/剪辑软件会在 WAV 里塞几 MB 的封面图。音频只有几十毫秒，
    /// 文件却有 9 MiB —— 直接整段发会被平台按体积拒掉，而实际音频完全没问题。
    /// </remarks>
    public static byte[] Repack(byte[] wav, WavInfo info)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(info);

        return BuildWav(info, wav.AsSpan(info.DataOffset, info.DataLength));
    }

    /// <summary>用原始 fmt 字段 + 一段 data 重新拼一个规范的 44 字节头 WAV。</summary>
    private static byte[] BuildWav(WavInfo info, ReadOnlySpan<byte> data)
    {
        var result = new byte[WavHeaderBytes + data.Length];
        var span = result.AsSpan();

        System.Text.Encoding.ASCII.GetBytes("RIFF", span[..4]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + data.Length);
        System.Text.Encoding.ASCII.GetBytes("WAVE", span.Slice(8, 4));
        System.Text.Encoding.ASCII.GetBytes("fmt ", span.Slice(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), (short)info.AudioFormat);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), (short)info.Channels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), info.SampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), info.BytesPerSecond);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), (short)info.BlockAlign);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), (short)info.BitsPerSample);
        System.Text.Encoding.ASCII.GetBytes("data", span.Slice(36, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), data.Length);
        data.CopyTo(span[WavHeaderBytes..]);

        return result;
    }

    private static bool Matches(byte[] buffer, int offset, string ascii)
    {
        if (buffer.Length < offset + ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (buffer[offset + i] != ascii[i])
            {
                return false;
            }
        }

        return true;
    }
}
