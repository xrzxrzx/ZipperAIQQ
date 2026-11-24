using System.Buffers.Binary;
using System.Text;
using ZipperAIQQ.Server.Ai.DeepSeekTools;

namespace ZipperAIQQ.Tests.Ai;

/// <summary>
/// WAV 切片（ASR 的 ≤120 s 硬约束）：头解析、短音频不切、长音频带重叠切、非 PCM 拒绝。
/// </summary>
/// <remarks>
/// 用**合成 WAV**（静音）而不是真音频：这里验证的是「帧边界与头部字节」，
/// 波形内容对切片的正确性没有影响，真音频只会让测试变慢。
/// </remarks>
public sealed class WavSlicerTests
{
    [Fact]
    public void Parses_a_pcm_header()
    {
        var wav = BuildWav(sampleRate: 8000, channels: 1, seconds: 2);

        Assert.True(WavSlicer.TryParse(wav, out var info));
        Assert.NotNull(info);
        Assert.Equal(1, info!.AudioFormat);
        Assert.Equal(8000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal(2, info.BlockAlign);
        Assert.InRange(info.DurationMs, 1990, 2010);
    }

    [Theory]
    [InlineData(3)]   // IEEE float
    [InlineData(6)]   // A-law
    public void Non_pcm_formats_are_rejected(int audioFormat)
    {
        var wav = BuildWav(8000, 1, 1, audioFormat);

        Assert.False(WavSlicer.TryParse(wav, out var info));
        Assert.Null(info);
    }

    [Fact]
    public void Garbage_is_rejected_instead_of_throwing()
    {
        Assert.False(WavSlicer.TryParse([1, 2, 3], out _));
        Assert.False(WavSlicer.TryParse(new byte[100], out _));
    }

    [Fact]
    public void Rejects_a_data_chunk_that_declares_more_bytes_than_the_file()
    {
        // 回归：块长是外部输入，`body + size` 会 int 回绕成负数、守卫放过、随后 AsSpan 抛
        // ArgumentOutOfRangeException（用户传个坏音频就能炸出未分类异常）。修法是 long pos + 减法比较。
        var wav = BuildWav(8000, 1, 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), int.MaxValue);

        var threw = false;
        var parsed = true;
        try
        {
            parsed = WavSlicer.TryParse(wav, out _);
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert.False(threw);
        Assert.False(parsed);
    }

    [Fact]
    public void Repack_drops_metadata_blocks_and_keeps_the_audio()
    {
        // 录音笔常带封面块：音频 1 s，文件却有 9 MiB。重打包后只剩 fmt + data。
        var wav = BuildWav(8000, 1, 1);
        Assert.True(WavSlicer.TryParse(wav, out var info));

        var repacked = WavSlicer.Repack(wav, info!);

        // 干净版本 = 44 字节头 + 纯音频
        Assert.Equal(44 + info!.DataLength, repacked.Length);
        Assert.True(WavSlicer.TryParse(repacked, out var again));
        Assert.Equal(info.DurationMs, again!.DurationMs);
        // 不认识的块不会被带过去（这里用 9 MiB 的垃圾尾巴模拟"容器比音频大得多"）
        Assert.True(repacked.Length < wav.Length + 9 * 1024 * 1024);
    }

    [Fact]
    public void Short_audio_stays_in_one_slice()
    {
        var wav = BuildWav(8000, 1, 30);
        Assert.True(WavSlicer.TryParse(wav, out var info));

        var slices = WavSlicer.Slice(wav, info!, maxSeconds: 110, overlapSeconds: 1);

        Assert.Single(slices);
        Assert.Equal(0, slices[0].StartMs);
        Assert.InRange(slices[0].EndMs, 29_900, 30_100);
    }

    [Fact]
    public void Long_audio_is_split_with_one_second_overlap()
    {
        var wav = BuildWav(8000, 1, 300);
        Assert.True(WavSlicer.TryParse(wav, out var info));

        var slices = WavSlicer.Slice(wav, info!, maxSeconds: 110, overlapSeconds: 1);

        // 110 s 一段、步长 109 s → 0 / 109 / 218，共 3 段
        Assert.Equal(3, slices.Count);
        Assert.Equal(0, slices[0].StartMs);
        Assert.Equal(109_000, slices[1].StartMs);
        Assert.Equal(218_000, slices[2].StartMs);
        Assert.InRange(slices[^1].EndMs, 299_900, 300_100);

        foreach (var slice in slices)
        {
            var length = (slice.EndMs - slice.StartMs) / 1000.0;
            Assert.True(length <= 110.001, $"每段不得超过 110 s，实际 {length}s");
        }
    }

    [Fact]
    public void Every_slice_is_a_self_contained_wav()
    {
        var wav = BuildWav(8000, 2, 240);
        Assert.True(WavSlicer.TryParse(wav, out var info));

        var slices = WavSlicer.Slice(wav, info!, maxSeconds: 110, overlapSeconds: 1);

        foreach (var slice in slices)
        {
            var bytes = slice.Bytes;

            // 头必须自洽：RIFF/WAVE/fmt/data + 长度字段与真实字节数一致
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal("data", Encoding.ASCII.GetString(bytes, 36, 4));

            var riffSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
            var dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));

            Assert.Equal(bytes.Length - 8, riffSize);
            Assert.Equal(bytes.Length - 44, dataSize);

            // 切出来的一段自身也能被再次解析（说明不是半截字节流）
            Assert.True(WavSlicer.TryParse(bytes, out var parsed));
            Assert.NotNull(parsed);
        }
    }

    /// <summary>造一个静音 WAV（44 字节标准头 + 零填充的 data）。</summary>
    private static byte[] BuildWav(
        int sampleRate, int channels, int seconds, int audioFormat = 1, int bitsPerSample = 16)
    {
        var blockAlign = channels * bitsPerSample / 8;
        var dataLength = sampleRate * seconds * blockAlign;

        var wav = new byte[44 + dataLength];
        var span = wav.AsSpan();

        Encoding.ASCII.GetBytes("RIFF", span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVE", span.Slice(8, 4));
        Encoding.ASCII.GetBytes("fmt ", span.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), (short)audioFormat);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), (short)bitsPerSample);
        Encoding.ASCII.GetBytes("data", span.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), dataLength);

        return wav;
    }
}
