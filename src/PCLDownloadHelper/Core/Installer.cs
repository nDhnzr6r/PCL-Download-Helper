using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PclDownloadHelper.Core;

public sealed class InstallOptions
{
    public VersionEntry Entry { get; init; } = new();
    public LoaderKind Loader { get; init; }
    public string? LoaderVersion { get; init; }
    public bool InstallFabricApi { get; init; }
}

public sealed class InstallResult
{
    public string Id { get; init; } = "";
    public string VanillaId { get; init; } = "";
    public string Directory { get; init; } = "";
    public string Comment { get; init; } = "";
}

/// <summary>把原版与加载器安装到 PCL 的游戏目录。</summary>
public sealed class Installer
{
    private static readonly string[] StageNames =
    {
        "读取版本信息", "下载客户端", "下载依赖库", "下载索引文件", "下载游戏资源", "安装加载器"
    };

    private readonly string _mcDir;
    private readonly Http _http;
    private readonly ProgressTracker _tracker;
    private readonly Action<string>? _note;

    public Installer(string mcDir, Http http, IProgress<InstallProgress> progress, Action<string>? note = null)
    {
        _mcDir = mcDir;
        _http = http;
        _note = note;
        _tracker = new ProgressTracker(progress);
    }

    public async Task<InstallResult> InstallAsync(InstallOptions options, CancellationToken token)
    {
        var entry = options.Entry;
        _tracker.EnterStage(StageNames[0], 0, 0.02);
        _tracker.SetDetail($"Minecraft {entry.Id}");
        var raw = await _http.GetBytesAsync(entry.Url, token).ConfigureAwait(false);
        var version = JObject.Parse(Encoding.UTF8.GetString(raw));
        var versionId = version["id"]?.ToString() ?? entry.Id;
        var versionDir = Path.Combine(_mcDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);
        await WriteBytesAsync(Path.Combine(versionDir, versionId + ".json"), raw, token).ConfigureAwait(false);
        _tracker.SetWithin(1);

        _tracker.EnterStage(StageNames[1], 0.02, 0.16);
        var client = version["downloads"]?["client"] as JObject;
        var jarPath = Path.Combine(versionDir, versionId + ".jar");
        if (client?["url"] != null)
        {
            var size = client["size"]?.Value<long>() ?? 0;
            var received = 0L;
            var bytesProgress = new Progress<long>(count =>
            {
                received += count;
                _tracker.AddBytes(count);
                if (size > 0) _tracker.SetWithin((double)received / size);
            });
            await _http.DownloadAsync(client["url"]!.ToString(), jarPath, client["sha1"]?.ToString(),
                size > 0 ? size : null, bytesProgress, token).ConfigureAwait(false);
        }
        _tracker.SetWithin(1);

        var logging = version["logging"]?["client"]?["file"] as JObject;
        if (logging?["url"] != null)
        {
            await _http.DownloadAsync(logging["url"]!.ToString(),
                Path.Combine(_mcDir, "assets", "log_configs", logging["id"]?.ToString() ?? "client"),
                logging["sha1"]?.ToString(), logging["size"]?.Value<long>(), null, token).ConfigureAwait(false);
        }

        _tracker.EnterStage(StageNames[2], 0.18, 0.22);
        await DownloadGroupAsync(McMeta.Libraries(version), Path.Combine(_mcDir, "libraries"), 8, token)
            .ConfigureAwait(false);

        _tracker.EnterStage(StageNames[3], 0.40, 0.10);
        var indexInfo = version["assetIndex"] as JObject;
        var objects = new List<RemoteFile>();
        var isVirtual = false;
        JObject? index = null;
        if (indexInfo?["url"] != null)
        {
            var indexId = indexInfo["id"]?.ToString() ?? "legacy";
            var indexPath = Path.Combine(_mcDir, "assets", "indexes", indexId + ".json");
            await _http.DownloadAsync(indexInfo["url"]!.ToString(), indexPath, indexInfo["sha1"]?.ToString(),
                indexInfo["size"]?.Value<long>(), null, token).ConfigureAwait(false);
            index = JObject.Parse(File.ReadAllText(indexPath));
            objects = McMeta.AssetObjects(index, out isVirtual, out _);
        }
        _tracker.SetWithin(1);

        _tracker.EnterStage(StageNames[4], 0.50, 0.38);
        await DownloadGroupAsync(objects, Path.Combine(_mcDir, "assets", "objects"), 16, token)
            .ConfigureAwait(false);
        if (isVirtual && index != null) BuildVirtualAssets(index);

        var installedId = versionId;
        if (options.Loader != LoaderKind.None)
        {
            _tracker.EnterStage(StageNames[5], 0.88, 0.12);
            installedId = options.Loader switch
            {
                LoaderKind.Fabric or LoaderKind.Quilt => await InstallFabricLikeAsync(options, versionId, token)
                    .ConfigureAwait(false),
                LoaderKind.Forge or LoaderKind.NeoForge => await InstallForgelikeAsync(options, versionId, version,
                    token).ConfigureAwait(false),
                _ => versionId
            };
        }

        _tracker.SetWithin(1);
        _tracker.Report(true);
        return new InstallResult
        {
            Id = installedId,
            VanillaId = versionId,
            Directory = Path.Combine(_mcDir, "versions", installedId),
            Comment = McMeta.JavaRequirement(version)
        };
    }

    // ---- 文件下载 ----

    private async Task DownloadGroupAsync(List<RemoteFile> files, string baseDir, int workers, CancellationToken token)
    {
        var unique = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Path.Length > 0) unique[file.Path] = file;
        }
        var items = unique.Values.ToList();
        var total = items.Count;
        if (total == 0) return;
        var done = 0;
        _tracker.ResetFiles();
        _tracker.SetFiles(0, total);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var semaphore = new SemaphoreSlim(workers);
        Exception? failure = null;
        var tasks = items.Select(async file =>
        {
            await semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (linked.IsCancellationRequested) return;
                var dest = Path.Combine(baseDir, file.Path.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar));
                await _http.DownloadAsync(file.Url, dest, file.Sha1, file.Size,
                    new Progress<long>(_tracker.AddBytes), linked.Token, file.VerifyHash).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                linked.Cancel();
                return;
            }
            finally
            {
                semaphore.Release();
            }
            var count = Interlocked.Increment(ref done);
            _tracker.SetFiles(count, total);
            _tracker.SetWithin((double)count / total);
        });
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        if (failure != null) throw failure;
    }

    private static Task WriteBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, bytes);
        return Task.CompletedTask;
    }

    private void BuildVirtualAssets(JObject index)
    {
        if (index["objects"] is not JObject objects) return;
        var targetRoot = Path.Combine(_mcDir, "assets", "virtual", "legacy");
        var objectRoot = Path.Combine(_mcDir, "assets", "objects");
        foreach (var property in objects.Properties())
        {
            var hash = property.Value["hash"]?.ToString();
            if (string.IsNullOrEmpty(hash)) continue;
            var target = Path.Combine(targetRoot, property.Name.Replace('/', '\\'));
            if (File.Exists(target)) continue;
            var source = Path.Combine(objectRoot, hash.Substring(0, 2), hash);
            if (!File.Exists(source)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, false);
            }
            catch (Exception)
            {
                // 资源映射失败不影响启动
            }
        }
    }

    // ---- 加载器 ----

    private async Task<string> InstallFabricLikeAsync(InstallOptions options, string versionId, CancellationToken token)
    {
        var loaderVersion = options.LoaderVersion;
        if (string.IsNullOrEmpty(loaderVersion))
        {
            var versions = options.Loader == LoaderKind.Quilt
                ? await LoaderCatalog.QuiltVersionsAsync(_http, versionId, token).ConfigureAwait(false)
                : await LoaderCatalog.FabricVersionsAsync(_http, versionId, token).ConfigureAwait(false);
            loaderVersion = versions.FirstOrDefault();
        }
        if (string.IsNullOrEmpty(loaderVersion))
            throw new IOException("该 Minecraft 版本没有可用的加载器版本");

        var display = options.Loader == LoaderKind.Quilt ? "Quilt" : "Fabric";
        _tracker.SetDetail($"正在安装 {display} {loaderVersion}");
        var profile = await LoaderCatalog.ProfileAsync(_http, options.Loader, versionId, loaderVersion, token)
            .ConfigureAwait(false);
        var folder = $"{versionId}-{display} {loaderVersion}";
        profile["id"] = folder;
        profile["inheritsFrom"] = versionId;

        var files = new List<RemoteFile>();
        var defaultMaven = options.Loader == LoaderKind.Quilt
            ? "https://maven.quiltmc.org/repository/release/"
            : "https://maven.fabricmc.net/";
        if (profile["libraries"] is JArray libraries)
        {
            foreach (var library in libraries.OfType<JObject>())
            {
                var name = library["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var path = McMeta.MavenPath(name);
                if (path.Length == 0) continue;
                var baseUrl = library["url"]?.ToString() ?? defaultMaven;
                if (!baseUrl.EndsWith("/")) baseUrl += "/";
                files.Add(new RemoteFile
                {
                    Url = baseUrl + path,
                    Path = path,
                    Sha1 = library["sha1"]?.ToString(),
                    Size = library["size"]?.Value<long>()
                });
            }
        }
        _tracker.SetWithin(0.3);
        await DownloadGroupAsync(files, Path.Combine(_mcDir, "libraries"), 6, token).ConfigureAwait(false);

        var targetDir = Path.Combine(_mcDir, "versions", folder);
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, folder + ".json"),
            profile.ToString(Formatting.None), new UTF8Encoding(false));

        if (options.InstallFabricApi && options.Loader == LoaderKind.Fabric)
        {
            _tracker.SetDetail("正在安装 Fabric API");
            var api = await LoaderCatalog.FabricApiAsync(_http, versionId, token).ConfigureAwait(false);
            if (api == null)
            {
                _note?.Invoke("没有找到匹配的 Fabric API，已跳过");
            }
            else
            {
                var modsDir = Path.Combine(targetDir, "mods");
                Directory.CreateDirectory(modsDir);
                await _http.DownloadAsync(api.Value.Url, Path.Combine(modsDir, api.Value.FileName), null, null,
                    new Progress<long>(_tracker.AddBytes), token).ConfigureAwait(false);
            }
        }
        return folder;
    }

    private async Task<string> InstallForgelikeAsync(InstallOptions options, string versionId, JObject vanilla,
        CancellationToken token)
    {
        var display = options.Loader == LoaderKind.NeoForge ? "NeoForge" : "Forge";
        var loaderVersion = options.LoaderVersion;
        if (string.IsNullOrEmpty(loaderVersion))
        {
            var versions = options.Loader == LoaderKind.NeoForge
                ? await LoaderCatalog.NeoForgeVersionsAsync(_http, versionId, token).ConfigureAwait(false)
                : await LoaderCatalog.ForgeVersionsAsync(_http, versionId, token).ConfigureAwait(false);
            loaderVersion = versions.FirstOrDefault();
        }
        if (string.IsNullOrEmpty(loaderVersion))
            throw new IOException($"该 Minecraft 版本没有可用的 {display} 版本");

        var installerUrl = options.Loader == LoaderKind.NeoForge
            ? LoaderCatalog.NeoForgeInstallerUrl(versionId, loaderVersion)
            : LoaderCatalog.ForgeInstallerUrl(versionId, loaderVersion);
        var tempDir = Path.Combine(Path.GetTempPath(), "PCLDownloadHelper");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir,
            $"{display.ToLowerInvariant()}-{versionId}-{loaderVersion}-installer.jar");

        _tracker.SetDetail($"正在下载 {display} {loaderVersion} 安装包");
        await _http.DownloadAsync(installerUrl, installerPath, null, null,
            new Progress<long>(_tracker.AddBytes), token).ConfigureAwait(false);

        var required = vanilla["javaVersion"]?["majorVersion"]?.Value<int?>();
        var java = JavaFinder.Pick(required);
        if (java == null) throw new IOException("没有找到可用的 Java，无法运行加载器安装程序");

        _tracker.SetDetail($"正在运行 {display} 安装程序（{java.Major}）");
        var before = SnapshotVersionFolders();
        var output = new StringBuilder();
        var psi = new ProcessStartInfo(java.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _mcDir,
            Arguments = $"-Djava.net.useSystemProxies=true -jar \"{installerPath}\" --installClient \"{_mcDir}\""
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lines = 0;
        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            output.AppendLine(args.Data);
            var count = Interlocked.Increment(ref lines);
            _tracker.SetWithin(Math.Min(0.95, count / 240.0));
            var text = args.Data.Trim();
            if (text.Length > 0 && text.Length < 120) _tracker.SetDetail($"正在安装 {display}：{text}");
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) output.AppendLine(args.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var finished = await Task.Run(() => process.WaitForExit(900000), token).ConfigureAwait(false);
        if (!finished)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // 忽略
            }
            throw new IOException($"{display} 安装程序运行超时");
        }
        if (process.ExitCode != 0)
        {
            var outputLines = output.ToString().Split('\n');
            var tail = string.Join(Environment.NewLine, outputLines.Skip(Math.Max(0, outputLines.Length - 6)));
            throw new IOException($"{display} 安装程序返回 {process.ExitCode}：{tail.Trim()}");
        }

        var created = DetectNewVersion(before, display);
        if (created == null) throw new IOException($"{display} 安装完成但没有找到生成的版本文件");

        var folder = $"{versionId}-{display}_{loaderVersion}";
        var finalDir = Path.Combine(_mcDir, "versions", folder);
        if (Directory.Exists(finalDir) && !string.Equals(created, finalDir, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(finalDir, true);
        if (!string.Equals(created, finalDir, StringComparison.OrdinalIgnoreCase))
            Directory.Move(created, finalDir);

        var jsonPath = Path.Combine(finalDir, Path.GetFileName(created) + ".json");
        var newJson = Path.Combine(finalDir, folder + ".json");
        if (File.Exists(jsonPath))
        {
            var content = JObject.Parse(File.ReadAllText(jsonPath));
            content["id"] = folder;
            File.WriteAllText(newJson, content.ToString(Formatting.None), new UTF8Encoding(false));
            File.Delete(jsonPath);
        }
        return folder;
    }

    private HashSet<string> SnapshotVersionFolders()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(_mcDir, "versions");
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root)) result.Add(dir);
        return result;
    }

    private string? DetectNewVersion(HashSet<string> before, string kind)
    {
        var root = Path.Combine(_mcDir, "versions");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (before.Contains(dir)) continue;
            var name = Path.GetFileName(dir).ToLowerInvariant();
            if (name.Contains(kind.ToLowerInvariant()) || name.Contains("forge")) return dir;
        }
        return null;
    }
}
