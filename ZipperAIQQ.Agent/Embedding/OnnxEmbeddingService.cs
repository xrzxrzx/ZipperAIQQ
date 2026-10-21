using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ZipperAIQQ.Agent.Embedding;

/// <summary>把文本转成向量。**检索的入口**，与具体模型解耦。</summary>
public interface IEmbeddingService
{
    /// <summary>向量维度（bge-small-zh 为 512）。</summary>
    int Dimension { get; }

    /// <summary>单条编码。返回**已 L2 归一化**的向量（余弦相似度 = 点积）。</summary>
    ReadOnlyMemory<float> Embed(string text);
}

/// <summary>
/// 基于 ONNX Runtime 的 BGE 中文 embedding。
/// </summary>
/// <remarks>
/// <para>
/// 模型：<c>bge-small-zh-v1.5</c>（512 维，int8 量化后约 23MB）。
/// **模型文件不入库**（见 AGENTS.md §七），由部署流程单独提供。
/// </para>
/// <para>
/// **池化用 CLS**（取首 token 的隐状态），不是 mean pooling —— BGE 系列的训练方式就是 CLS。
/// 用错了不会报错，只是召回质量会莫名变差。
/// </para>
/// <para>
/// **输出做 L2 归一化**：这样余弦相似度退化成点积，后续算分更快，也不用再除模长。
/// </para>
/// </remarks>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _hasTokenTypeIds;
    private bool _disposed;

    public OnnxEmbeddingService(string modelDirectory, int dimension = 512, int maxLength = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        var modelPath = Path.Combine(modelDirectory, "model_quantized.onnx");
        var tokenizerPath = Path.Combine(modelDirectory, "tokenizer.json");

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"找不到 embedding 模型：{modelPath}", modelPath);
        }
        if (!File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException($"找不到 tokenizer：{tokenizerPath}", tokenizerPath);
        }

        _tokenizer = BertTokenizer.Load(tokenizerPath, maxLength);
        _session = new InferenceSession(modelPath);
        _hasTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        Dimension = dimension;
    }

    public int Dimension { get; }

    public ReadOnlyMemory<float> Embed(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (ids, mask, types) = _tokenizer.Encode(text ?? string.Empty);
        var length = ids.Length;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, length])),
        };

        // 有些导出版本没有 token_type_ids，硬塞会报错
        if (_hasTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "token_type_ids", new DenseTensor<long>(types, [1, length])));
        }

        using var results = _session.Run(inputs);
        var output = results[0].AsTensor<float>();

        // output 形状 [1, seq, dim]；CLS 池化取 [0, 0, :]
        var vector = new float[Dimension];
        for (var i = 0; i < Dimension; i++)
        {
            vector[i] = output[0, 0, i];
        }

        NormalizeInPlace(vector);
        return vector;
    }

    /// <summary>L2 归一化，让余弦相似度 = 点积。</summary>
    private static void NormalizeInPlace(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var v in vector)
        {
            sumOfSquares += (double)v * v;
        }

        if (sumOfSquares <= 0)
        {
            return;
        }

        var norm = (float)Math.Sqrt(sumOfSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
