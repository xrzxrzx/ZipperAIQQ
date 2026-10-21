using System.Text;
using System.Text.Json;

namespace ZipperAIQQ.Agent.Embedding;

/// <summary>
/// BERT WordPiece 分词器（从 HuggingFace <c>tokenizer.json</c> 读取词表）。
/// </summary>
/// <remarks>
/// <para>
/// 只实现 BGE 系列需要的部分：CJK 逐字 + 空白切分 + WordPiece 贪心最长匹配。
/// 不做 normalizer 链（NFD/小写等）—— BERT 中文词表本身不含大写字母变体。
/// </para>
/// <para>
/// **为什么 CJK 要逐字切**：BERT 中文词表里每个汉字是独立 token，
/// 把它当英文单词做 WordPiece 反而会切出 <c>[UNK]</c>。
/// </para>
/// <para>
/// 这套实现先在 spike 里验证过（<c>temp/bge-spike</c>），确认 embedding 质量可用后才移植进来。
/// </para>
/// </remarks>
public sealed class BertTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _unkId;
    private readonly string _unkToken;
    private readonly string _continuingPrefix;
    private readonly int _maxLength;

    private BertTokenizer(
        Dictionary<string, int> vocab, int clsId, int sepId, int unkId,
        string unkToken, string continuingPrefix, int maxLength)
    {
        _vocab = vocab;
        _clsId = clsId;
        _sepId = sepId;
        _unkId = unkId;
        _unkToken = unkToken;
        _continuingPrefix = continuingPrefix;
        _maxLength = maxLength;
    }

    public static BertTokenizer Load(string tokenizerJsonPath, int maxLength = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerJsonPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var model = doc.RootElement.GetProperty("model");

        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in model.GetProperty("vocab").EnumerateObject())
        {
            vocab[prop.Name] = prop.Value.GetInt32();
        }

        var unkToken = model.TryGetProperty("unk_token", out var u) ? (u.GetString() ?? "[UNK]") : "[UNK]";
        var prefix = model.TryGetProperty("continuing_subword_prefix", out var p) ? (p.GetString() ?? "##") : "##";

        int Lookup(string t, int fallback) => vocab.TryGetValue(t, out var id) ? id : fallback;

        return new BertTokenizer(
            vocab,
            clsId: Lookup("[CLS]", 101),
            sepId: Lookup("[SEP]", 102),
            unkId: Lookup(unkToken, 100),
            unkToken: unkToken,
            continuingPrefix: prefix,
            maxLength: maxLength);
    }

    /// <summary>编码为 (input_ids, attention_mask, token_type_ids)。</summary>
    public (long[] Ids, long[] Mask, long[] Types) Encode(string text)
    {
        var ids = new List<long> { _clsId };

        foreach (var token in BasicTokenize(text))
        {
            if (ids.Count >= _maxLength - 1) break;
            ids.Add(_vocab.TryGetValue(token, out var id) ? id : _unkId);
        }

        ids.Add(_sepId);

        return (ids.ToArray(), Enumerable.Repeat(1L, ids.Count).ToArray(), new long[ids.Count]);
    }

    private IReadOnlyList<string> BasicTokenize(string text)
    {
        var result = new List<string>();
        var word = new StringBuilder();

        void FlushWord()
        {
            if (word.Length == 0) return;
            result.AddRange(WordPiece(word.ToString()));
            word.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                FlushWord();
                continue;
            }

            if (IsCjk(ch))
            {
                FlushWord();
                result.Add(ch.ToString());
                continue;
            }

            if (char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                FlushWord();
                result.Add(ch.ToString());
                continue;
            }

            word.Append(ch);
        }

        FlushWord();
        return result;
    }

    /// <summary>WordPiece 贪心最长匹配。</summary>
    private IEnumerable<string> WordPiece(string word)
    {
        if (word.Length > 100)
        {
            yield return _unkToken;
            yield break;
        }

        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            string? matched = null;

            while (start < end)
            {
                var piece = word[start..end];
                if (start > 0) piece = _continuingPrefix + piece;

                if (_vocab.ContainsKey(piece))
                {
                    matched = piece;
                    break;
                }
                end--;
            }

            if (matched is null)
            {
                yield return _unkToken;
                yield break;
            }

            yield return matched;
            start = end;
        }
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u4E00' and <= '\u9FFF'
        or >= '\u3400' and <= '\u4DBF'
        or >= '\uF900' and <= '\uFAFF'
        or >= '\u3000' and <= '\u303F'
        or >= '\u3040' and <= '\u30FF'
        or >= '\uAC00' and <= '\uD7AF';
}
