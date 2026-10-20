using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PclDownloadHelper.Core;

/// <summary>镜像地址改写与下载源状态。</summary>
public sealed class SourceState
{
    private static readonly (string Prefix, string Replacement)[] Rewrites =
    {
        ("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
        ("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://maven.fabricmc.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://maven.minecraftforge.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://maven.neoforged.net/releases/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://meta.fabricmc.net/", "https://bmclapi2.bangbang93.com/fabric-meta/"),
        ("https://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://launcher.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/")
    };

    private int _failures;

    public bool PreferMirror { get; set; } = true;

    public event Action<string>? Notified;

    public static string? MirrorUrl(string url)
    {
        foreach (var (prefix, replacement) in Rewrites)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return replacement + url.Substring(prefix.Length);
        }
        return null;
    }

    public List<string> Candidates(string url)
    {
        var mirrored = MirrorUrl(url);
        if (mirrored == null || mirrored == url) return new List<string> { url };
        return PreferMirror ? new List<string> { mirrored, url } : new List<string> { url, mirrored };
    }

    public void MirrorFailed()
    {
        if (Interlocked.Increment(ref _failures) < 6) return;
        if (!PreferMirror) return;
        PreferMirror = false;
        Notified?.Invoke("镜像节点响应异常，已改用官方源继续下载");
    }
}

/// <summary>带镜像回退、重试、卡死回收与哈希校验的下载客户端。</summary>
public sealed class Http : IDisposable
{
    private const int BufferSize = 81920;

    private readonly HttpClient _client;
    private readonly SourceState _source;

    public Http(SourceState source)
    {
        _source = source;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PCLDownloadHelper/2.0");
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken token, int retries = 3)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < retries; attempt++)
        {
            foreach (var candidate in _source.Candidates(url))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await _client.GetAsync(candidate, HttpCompletionOption.ResponseContentRead, token)
                        .ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    last = new IOException($"HTTP {(int)response.StatusCode}：{candidate}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
                if (candidate != url) _source.MirrorFailed();
            }
            if (attempt < retries - 1) await Task.Delay(400 * (attempt + 1), token).ConfigureAwait(false);
        }
        throw new IOException($"无法访问：{url}（{last?.Message}）");
    }

    public async Task<JObject> GetJsonAsync(string url, CancellationToken token)
    {
        var bytes = await GetBytesAsync(url, token).ConfigureAwait(false);
        return JObject.Parse(Encoding.UTF8.GetString(bytes));
    }

    public async Task<JToken> GetTokenAsync(string url, CancellationToken token)
    {
        var bytes = await GetBytesAsync(url, token).ConfigureAwait(false);
        return JToken.Parse(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>下载到目标路径，已存在且校验通过时直接返回。</summary>
    public async Task<long> DownloadAsync(
        string url,
        string dest,
        string? sha1,
        long? size,
        IProgress<long>? progress,
        CancellationToken token,
        bool verifyHash = true)
    {
        if (IsComplete(dest, sha1, size, verifyHash)) return 0;
        var directory = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var part = dest + ".part";

        Exception? last = null;
        for (var round = 0; round < 2; round++)
        {
            foreach (var candidate in _source.Candidates(url))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return await FetchOneAsync(candidate, part, dest, sha1, size, progress, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(part);
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    TryDelete(part);
                    if (candidate != url) _source.MirrorFailed();
                }
            }
        }
        throw new IOException($"下载失败：{Path.GetFileName(new Uri(url).LocalPath)}（{last?.Message}）");
    }

    private async Task<long> FetchOneAsync(
        string url,
        string part,
        string dest,
        string? sha1,
        long? size,
        IProgress<long>? progress,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new IOException($"HTTP {(int)response.StatusCode}");

        var total = response.Content.Headers.ContentLength;
        if (size.HasValue && total.HasValue && total.Value != size.Value)
            throw new IOException("文件大小与记录不符");

        long written = 0;
        using var hash = SHA1.Create();
        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readCts.CancelAfter(TimeSpan.FromSeconds(30));
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, 0, buffer.Length, readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new IOException("连接长时间没有数据");
                }
                if (read <= 0) break;
                await target.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                hash.TransformBlock(buffer, 0, read, null, 0);
                written += read;
                progress?.Report(read);
            }
        }

        if (size.HasValue && written != size.Value) throw new IOException("文件大小与记录不符");
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (!string.IsNullOrEmpty(sha1))
        {
            var actual = BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
            if (actual != sha1.ToLowerInvariant()) throw new IOException("文件校验失败");
        }

        if (File.Exists(dest)) File.Delete(dest);
        File.Move(part, dest);
        return written;
    }

    private static bool IsComplete(string dest, string? sha1, long? size, bool verifyHash)
    {
        var info = new FileInfo(dest);
        if (!info.Exists) return false;
        if (size.HasValue && info.Length != size.Value) return false;
        if (!string.IsNullOrEmpty(sha1) && verifyHash && !size.HasValue) return Sha1File(dest) == sha1.ToLowerInvariant();
        return size.HasValue || !string.IsNullOrEmpty(sha1);
    }

    public static string Sha1File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA1.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略清理失败
        }
    }

    public void Dispose() => _client.Dispose();
}
