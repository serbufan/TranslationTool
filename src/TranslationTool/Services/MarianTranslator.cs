using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using HfTokenizer = Tokenizers.DotNet.Tokenizer;

namespace TranslationTool.Services;

/// <summary>
/// 基于 MarianMT (opus-mt) ONNX 模型的离线翻译引擎。
/// 采用贪心解码：encoder 计算一次，decoder 逐步自回归生成。
/// </summary>
public sealed class MarianTranslator : IDisposable
{
    /// <summary>源文本单次送入模型的最大 token 数。</summary>
    public const int MaxSourceTokens = 480;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly HfTokenizer _tokenizer;

    private readonly string _encIdsName;
    private readonly string _encMaskName;
    private readonly string _encOutputName;
    private readonly string _decIdsName;
    private readonly string _decHiddenName;
    private readonly string _decMaskName;
    private readonly string _decLogitsName;

    private readonly int _eosId;
    private readonly int _decoderStartId;
    private readonly int _padId;
    private readonly HashSet<int> _bannedIds;

    private readonly bool _targetIsChinese;

    public string ModelName { get; }
    public int MaxNewTokens { get; init; } = 256;

    private MarianTranslator(
        InferenceSession encoder,
        InferenceSession decoder,
        HfTokenizer tokenizer,
        IReadOnlyDictionary<string, Microsoft.ML.OnnxRuntime.NodeMetadata> encInputs,
        IReadOnlyCollection<string> encOutputs,
        IReadOnlyDictionary<string, Microsoft.ML.OnnxRuntime.NodeMetadata> decInputs,
        IReadOnlyCollection<string> decOutputs,
        GenerationSettings settings,
        string modelName,
        bool targetIsChinese)
    {
        _encoder = encoder;
        _decoder = decoder;
        _tokenizer = tokenizer;
        ModelName = modelName;
        _targetIsChinese = targetIsChinese;

        _encIdsName = Pick(encInputs.Keys, "input_ids")
            ?? throw new NotSupportedException("编码器模型缺少 input_ids 输入。");
        _encMaskName = Pick(encInputs.Keys, "attention_mask")
            ?? throw new NotSupportedException("编码器模型缺少 attention_mask 输入。");
        _encOutputName = encOutputs.FirstOrDefault(n => n.Contains("hidden", StringComparison.OrdinalIgnoreCase))
            ?? encOutputs.First();

        _decIdsName = Pick(decInputs.Keys, "input_ids")
            ?? throw new NotSupportedException("解码器模型缺少 input_ids 输入。");
        _decHiddenName = Pick(decInputs.Keys, "encoder_hidden_states", "encoder_last_hidden_state")
            ?? throw new NotSupportedException("解码器模型缺少 encoder_hidden_states 输入。");
        _decMaskName = Pick(decInputs.Keys, "encoder_attention_mask")
            ?? throw new NotSupportedException("解码器模型缺少 encoder_attention_mask 输入。");
        _decLogitsName = decOutputs.FirstOrDefault(n => n.Contains("logits", StringComparison.OrdinalIgnoreCase))
            ?? decOutputs.First();

        if (decInputs.Keys.Any(k => k.Contains("past_key_values", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("暂不支持带 KV Cache 的解码器模型，请使用 decoder_model.onnx。");

        _eosId = settings.EosId;
        _decoderStartId = settings.DecoderStartId;
        _padId = settings.PadId;
        _bannedIds = new HashSet<int>(settings.BannedIds) { settings.PadId };
    }

    /// <summary>从模型目录加载分词器与两个 ONNX 会话。</summary>
    public static MarianTranslator Load(string modelDir, string modelName)
    {
        string tokenizerSourcePath = Path.Combine(modelDir, "tokenizer.json");
        string encoderPath = Path.Combine(modelDir, "encoder_model.onnx");
        string decoderPath = Path.Combine(modelDir, "decoder_model.onnx");
        string generationConfigPath = Path.Combine(modelDir, "generation_config.json");

        foreach (var path in new[] { tokenizerSourcePath, encoderPath, decoderPath })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"缺少模型文件：{Path.GetFileName(path)}", path);
        }

        string tokenizerPath = PrepareTokenizerFile(tokenizerSourcePath);

        var settings = GenerationSettings.Read(generationConfigPath);
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
            InterOpNumThreads = 1,
        };

        var tokenizer = new HfTokenizer(tokenizerPath);
        InferenceSession? encoder = null;
        InferenceSession? decoder = null;
        try
        {
            var sw = Stopwatch.StartNew();
            encoder = new InferenceSession(encoderPath, options);
            decoder = new InferenceSession(decoderPath, options);
            Trace.WriteLine($"模型 {modelName} 加载耗时 {sw.ElapsedMilliseconds} ms");

            bool targetIsChinese = modelName.Contains("en-zh", StringComparison.OrdinalIgnoreCase);

            return new MarianTranslator(
                encoder, decoder, tokenizer,
                encoder.InputMetadata, encoder.OutputMetadata.Keys.ToList(),
                decoder.InputMetadata, decoder.OutputMetadata.Keys.ToList(),
                settings, modelName, targetIsChinese);
        }
        catch
        {
            encoder?.Dispose();
            decoder?.Dispose();
            tokenizer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 部分 HuggingFace 导出的 tokenizer.json 里 SentencePiece 的 precompiled_charsmap 为 null，
    /// 新版 tokenizers 会直接 panic，这里把 Precompiled 归一化器替换为等价的 NFKC 归一化并缓存补丁副本。
    /// </summary>
    private static string PrepareTokenizerFile(string sourcePath)
    {
        string json = File.ReadAllText(sourcePath);
        if (!json.Contains("precompiled_charsmap", StringComparison.Ordinal)) return sourcePath;

        string patched = Regex.Replace(
            json,
            @"""normalizer""\s*:\s*\{[^{}]*""type""\s*:\s*""Precompiled""[^{}]*\}",
            @"""normalizer"": { ""type"": ""Sequence"", ""normalizers"": [ { ""type"": ""NFKC"" } ] }");

        if (string.Equals(patched, json, StringComparison.Ordinal)) return sourcePath;

        string patchedPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "tokenizer.patched.json");
        File.WriteAllText(patchedPath, patched);
        return patchedPath;
    }

    /// <summary>翻译任意长度文本（按句/按语种自动分段）。</summary>
    public string Translate(string text, CancellationToken cancellationToken = default)
    {
        // 中英混排时，本已属于译文的片段原样保留，避免模型把英文段再"翻译"成乱码
        bool mixed = ContainsCjk(text) && ContainsLatin(text);
        var segments = mixed ? SplitMixed(text) : new[] { text };

        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            foreach (var chunk in SplitIntoChunks(segment))
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool keepOriginal = mixed && (_targetIsChinese ? !ContainsLatin(chunk) : !ContainsCjk(chunk));
                string piece = keepOriginal ? chunk : TranslateChunk(chunk, cancellationToken);

                if (piece.Length == 0) continue;
                if (builder.Length > 0 && NeedsSpaceBetween(builder, piece)) builder.Append(' ');
                builder.Append(piece);
            }
        }

        return PostProcess(builder.ToString());
    }

    /// <summary>按句子与语种把中英混排文本切成同语种片段。</summary>
    private static IEnumerable<string> SplitMixed(string text)
    {
        foreach (var sentence in Regex.Split(text, @"(?<=[。！？；!?;\n])"))
        {
            var buffer = new StringBuilder();
            char lastClass = '\0';

            foreach (char c in sentence)
            {
                char cls = CharClass(c);
                if (cls == 'n')
                {
                    buffer.Append(c);
                    continue;
                }

                if (lastClass != '\0' && cls != lastClass && buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                buffer.Append(c);
                lastClass = cls;
            }

            if (buffer.Length > 0) yield return buffer.ToString();
        }
    }

    private static char CharClass(char c)
    {
        if (c >= '\u4e00' && c <= '\u9fff') return 'c';
        return c < '\u2e80' && char.IsLetter(c) ? 'l' : 'n';
    }

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        }

        return false;
    }

    private static bool ContainsLatin(string text)
    {
        foreach (char c in text)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
        }

        return false;
    }

    private string TranslateChunk(string text, CancellationToken cancellationToken)
    {
        uint[] rawIds = _tokenizer.Encode(text);
        var sourceIds = new List<long>(rawIds.Length);
        foreach (uint id in rawIds)
        {
            int value = (int)id;
            if (value == _padId) continue;
            sourceIds.Add(value);
        }

        if (sourceIds.Count == 0) return string.Empty;
        if (sourceIds.Count > MaxSourceTokens) sourceIds = sourceIds.Take(MaxSourceTokens).ToList();

        int sourceLength = sourceIds.Count;
        var maskData = new long[sourceLength];
        Array.Fill(maskData, 1L);

        using var runOptions = new RunOptions();
        using var encoderIds = OrtValue.CreateTensorValueFromMemory(sourceIds.ToArray(), new long[] { 1, sourceLength });
        using var maskValue = OrtValue.CreateTensorValueFromMemory(maskData, new long[] { 1, sourceLength });

        using var encoderResults = _encoder.Run(
            runOptions,
            new[] { _encIdsName, _encMaskName },
            new[] { encoderIds, maskValue },
            new[] { _encOutputName });

        OrtValue hiddenStates = encoderResults[0];
        var hiddenSpan = hiddenStates.GetTensorDataAsSpan<float>();
        long[] hiddenShape = hiddenStates.GetTensorTypeAndShape().Shape;

        using var hiddenValue = OrtValue.CreateTensorValueFromMemory(hiddenSpan.ToArray(), hiddenShape);

        var generatedIds = new List<long> { _decoderStartId };
        var outputIds = new List<int>(MaxNewTokens);

        for (int step = 0; step < MaxNewTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var decoderIds = OrtValue.CreateTensorValueFromMemory(
                generatedIds.ToArray(), new long[] { 1, generatedIds.Count });

            using var decoderResults = _decoder.Run(
                runOptions,
                new[] { _decIdsName, _decHiddenName, _decMaskName },
                new[] { decoderIds, hiddenValue, maskValue },
                new[] { _decLogitsName });

            OrtValue logits = decoderResults[0];
            long[] shape = logits.GetTensorTypeAndShape().Shape;
            int vocabSize = (int)shape[^1];
            int lastPosition = (int)shape[^2] - 1;
            var span = logits.GetTensorDataAsSpan<float>();
            int offset = lastPosition * vocabSize;

            int bestId = -1;
            float bestScore = float.NegativeInfinity;
            for (int v = 0; v < vocabSize; v++)
            {
                if (_bannedIds.Contains(v)) continue;
                float score = span[offset + v];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = v;
                }
            }

            if (bestId < 0 || bestId == _eosId) break;

            outputIds.Add(bestId);
            generatedIds.Add(bestId);
        }

        if (outputIds.Count == 0) return string.Empty;

        string decoded = _tokenizer.Decode(outputIds.Select(id => (uint)id).ToArray());
        return decoded.Replace("<pad>", string.Empty).Replace("</s>", string.Empty).Replace("<unk>", string.Empty).Trim();
    }

    private static bool NeedsSpaceBetween(StringBuilder builder, string next)
    {
        char last = builder[^1];
        char first = next[0];
        return !char.IsWhiteSpace(last) && !char.IsWhiteSpace(first);
    }

    private string PostProcess(string text)
    {
        text = text.Trim();
        if (!_targetIsChinese) return text;

        // 去掉汉字之间多余的空格，并把模型偶尔输出的半角标点修正为全角
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", string.Empty);
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff]),(?=[\u4e00-\u9fff])", "，");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\?(?=[\u4e00-\u9fff]|\s*$)", "？");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])!(?=[\u4e00-\u9fff]|\s*$)", "！");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff]):(?=[\u4e00-\u9fff])", "：");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff]);(?=[\u4e00-\u9fff])", "；");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\.(?=\s*$)", "。");
        text = Regex.Replace(text, @"\s+(?=[，。！？；：、）】」”’])", string.Empty);
        return text.Trim();
    }

    /// <summary>按行、按句切分，保证每段不超过 <see cref="MaxSourceTokens"/> 个 token。</summary>
    private IEnumerable<string> SplitIntoChunks(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (CountTokens(line) <= MaxSourceTokens)
            {
                yield return line;
                continue;
            }

            foreach (var chunk in SplitLongLine(line))
                yield return chunk;
        }
    }

    private IEnumerable<string> SplitLongLine(string line)
    {
        var sentenceParts = Regex.Split(line, @"(?<=[。！？；!?;\.])");
        var buffer = new StringBuilder();

        foreach (var part in sentenceParts)
        {
            string sentence = part.Trim();
            if (sentence.Length == 0) continue;

            if (CountTokens(sentence) > MaxSourceTokens)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                foreach (var hardChunk in SplitByWords(sentence))
                    yield return hardChunk;
                continue;
            }

            if (buffer.Length > 0 && CountTokens(buffer + sentence) > MaxSourceTokens)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            buffer.Append(sentence);
        }

        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private IEnumerable<string> SplitByWords(string sentence)
    {
        var buffer = new StringBuilder();
        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (buffer.Length > 0 && CountTokens(buffer + " " + word) > MaxSourceTokens)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(word);
        }

        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private int CountTokens(string text) => _tokenizer.Encode(text).Length;

    private static string? Pick(IEnumerable<string> names, params string[] candidates)
    {
        var list = names.ToList();
        foreach (var candidate in candidates)
        {
            var exact = list.FirstOrDefault(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        foreach (var candidate in candidates)
        {
            var partial = list.FirstOrDefault(n => n.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial;
        }

        return null;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _tokenizer.Dispose();
    }

    private sealed record GenerationSettings(int EosId, int DecoderStartId, int PadId, int[] BannedIds)
    {
        public static GenerationSettings Read(string path)
        {
            int eosId = 0;
            int startId = -1;
            int padId = -1;
            var banned = new List<int>();

            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;

                if (root.TryGetProperty("eos_token_id", out var eos))
                    eosId = ReadTokenId(eos, eosId);
                if (root.TryGetProperty("decoder_start_token_id", out var start))
                    startId = ReadTokenId(start, startId);
                if (root.TryGetProperty("pad_token_id", out var pad))
                    padId = ReadTokenId(pad, padId);
                if (root.TryGetProperty("bad_words_ids", out var bad) && bad.ValueKind == JsonValueKind.Array)
                {
                    foreach (var group in bad.EnumerateArray())
                    {
                        if (group.ValueKind != JsonValueKind.Array) continue;
                        foreach (var id in group.EnumerateArray())
                        {
                            if (id.TryGetInt32(out int value)) banned.Add(value);
                        }
                    }
                }
            }

            if (startId < 0) startId = padId >= 0 ? padId : 65000;
            return new GenerationSettings(eosId, startId, padId, banned.ToArray());
        }

        private static int ReadTokenId(JsonElement element, int fallback)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int single)) return single;
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.TryGetInt32(out int itemValue)) return itemValue;
                }
            }

            return fallback;
        }
    }
}
