using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace TranslationTool.Services;

public sealed record ModelDownloadProgress(string FileName, double OverallFraction, string Message);

/// <summary>
/// 管理本地离线模型文件（MarianMT 量化 ONNX），并支持首次运行时从 HuggingFace 镜像下载。
/// 下载完成后即可完全离线使用。
/// </summary>
public static class ModelRepository
{
    public const string ZhEnPair = "zh-en";
    public const string EnZhPair = "en-zh";

    private sealed record ModelFile(string LocalName, string RemotePath);

    private static readonly Dictionary<string, string> Repos = new()
    {
        [ZhEnPair] = "Xenova/opus-mt-zh-en",
        [EnZhPair] = "Xenova/opus-mt-en-zh",
    };

    private static readonly ModelFile[] Files =
    {
        new("tokenizer.json", "tokenizer.json"),
        new("generation_config.json", "generation_config.json"),
        new("encoder_model.onnx", "onnx/encoder_model_quantized.onnx"),
        // 解码器用 fp16：比 int8 动态量化在部分句子上明显更稳定
        new("decoder_model.onnx", "onnx/decoder_model_fp16.onnx"),
    };

    private static readonly string[] Mirrors =
    {
        "https://huggingface.co",
        "https://hf-mirror.com",
    };

    private static readonly HttpClient Client = CreateClient();
    private static string? _preferredMirror;

    public static string Root => Path.Combine(AppContext.BaseDirectory, "models");

    public static string DirectoryFor(string pair) => Path.Combine(Root, pair);

    public static bool IsReady(string pair) =>
        Files.All(file => File.Exists(Path.Combine(DirectoryFor(pair), file.LocalName)));

    public static string PairDisplayName(string pair) => pair == ZhEnPair ? "中 → 英" : "英 → 中";

    public static async Task EnsureAsync(
        string pair,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Repos.TryGetValue(pair, out string? repo))
            throw new ArgumentException($"未知的翻译方向：{pair}", nameof(pair));

        string directory = DirectoryFor(pair);
        Directory.CreateDirectory(directory);

        for (int index = 0; index < Files.Length; index++)
        {
            var file = Files[index];
            string targetPath = Path.Combine(directory, file.LocalName);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 1024)
            {
                progress?.Report(new ModelDownloadProgress(file.LocalName, (index + 1d) / Files.Length, "已存在，跳过"));
                continue;
            }

            await DownloadAsync(repo, file, directory, index, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DownloadAsync(
        string repo,
        ModelFile file,
        string directory,
        int fileIndex,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string targetPath = Path.Combine(directory, file.LocalName);
        string tempPath = targetPath + ".download";
        Exception? lastError = null;

        foreach (string mirror in await OrderMirrorsAsync(repo, cancellationToken).ConfigureAwait(false))
        {
            string url = $"{mirror}/{repo}/resolve/main/{file.RemotePath}?download=true";
            try
            {
                using var response = await Client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    continue;
                }

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = File.Create(tempPath);

                var buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    double fileProgress = totalBytes > 0 ? (double)received / totalBytes : 0;
                    double overall = Math.Clamp((fileIndex + fileProgress) / Files.Length, 0, 1);
                    progress?.Report(new ModelDownloadProgress(
                        file.LocalName,
                        overall,
                        $"{received / 1024d / 1024d:F1} MB" + (totalBytes > 0 ? $" / {totalBytes / 1024d / 1024d:F1} MB" : string.Empty)));
                }

                destination.Close();
                File.Move(tempPath, targetPath, overwrite: true);
                progress?.Report(new ModelDownloadProgress(file.LocalName, (fileIndex + 1d) / Files.Length, "完成"));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _preferredMirror = null;
                TryDelete(tempPath);
            }
        }

        throw new InvalidOperationException(
            $"下载 {file.RemotePath} 失败，请检查网络后重试。（{lastError?.Message}）", lastError);
    }

    /// <summary>先探测可用镜像（避免直连 HuggingFace 不可达时长时间等待）。</summary>
    private static async Task<IReadOnlyList<string>> OrderMirrorsAsync(string repo, CancellationToken cancellationToken)
    {
        if (_preferredMirror is null)
        {
            foreach (string mirror in Mirrors)
            {
                if (await ProbeAsync(mirror, repo, cancellationToken).ConfigureAwait(false))
                {
                    _preferredMirror = mirror;
                    break;
                }
            }
        }

        if (_preferredMirror is null) return Mirrors;
        return Mirrors.OrderBy(m => m == _preferredMirror ? 0 : 1).ToList();
    }

    private static async Task<bool> ProbeAsync(string mirror, string repo, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{mirror}/{repo}/resolve/main/tokenizer.json");
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略临时文件清理失败
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OfflineTranslator", "1.0"));
        return client;
    }
}
