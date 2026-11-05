using System.Diagnostics;

namespace TranslationTool.Services;

public enum TranslationDirection
{
    Auto,
    ZhToEn,
    EnToZh,
}

public sealed record TranslationResult(string Text, string Pair, TimeSpan Elapsed);

/// <summary>
/// 翻译服务：负责按需加载各方向的引擎，并串行化推理请求。
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly Dictionary<string, MarianTranslator> _engines = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);

    public bool IsLoaded(string pair)
    {
        lock (_engines) return _engines.ContainsKey(pair);
    }

    public async Task LoadAsync(string pair, CancellationToken cancellationToken = default)
    {
        if (IsLoaded(pair)) return;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsLoaded(pair)) return;
            var engine = await Task
                .Run(() => MarianTranslator.Load(ModelRepository.DirectoryFor(pair), pair), cancellationToken)
                .ConfigureAwait(false);

            lock (_engines) _engines[pair] = engine;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken cancellationToken)
    {
        string pair = ResolvePair(text, direction);
        await LoadAsync(pair, cancellationToken).ConfigureAwait(false);

        MarianTranslator engine;
        lock (_engines) engine = _engines[pair];

        var stopwatch = Stopwatch.StartNew();
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string translated = await Task
                .Run(() => engine.Translate(text, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return new TranslationResult(translated, pair, stopwatch.Elapsed);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public static string ResolvePair(string text, TranslationDirection direction) => direction switch
    {
        TranslationDirection.ZhToEn => ModelRepository.ZhEnPair,
        TranslationDirection.EnToZh => ModelRepository.EnZhPair,
        _ => ContainsChinese(text) ? ModelRepository.ZhEnPair : ModelRepository.EnZhPair,
    };

    public static bool ContainsChinese(string text)
    {
        foreach (char c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        }

        return false;
    }

    public void Dispose()
    {
        lock (_engines)
        {
            foreach (var engine in _engines.Values) engine.Dispose();
            _engines.Clear();
        }

        _loadGate.Dispose();
        _inferenceGate.Dispose();
    }
}
