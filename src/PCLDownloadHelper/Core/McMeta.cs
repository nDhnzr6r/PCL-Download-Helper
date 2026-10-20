using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PclDownloadHelper.Core;

public sealed class VersionEntry
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public DateTime ReleaseTime { get; init; }

    public bool IsRelease => Type == "release";
    public bool IsSnapshot => Type == "snapshot";

    public string DateText => ReleaseTime == default ? "" : ReleaseTime.ToString("yyyy/MM/dd HH:mm");

    public string TypeName => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古测试版",
        "old_alpha" => "远古版本",
        _ => Type
    };
}

public sealed class RemoteFile
{
    public string Url { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Sha1 { get; init; }
    public long? Size { get; init; }
    public bool VerifyHash { get; init; } = true;
}

public static class McMeta
{
    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly string OsVersion = Environment.OSVersion.Version.ToString();

    public static List<VersionEntry> ParseManifest(JObject json)
    {
        var result = new List<VersionEntry>();
        if (json["versions"] is not JArray versions) return result;
        foreach (var item in versions)
        {
            if (item is not JObject entry) continue;
            DateTime.TryParse(entry["releaseTime"]?.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time);
            result.Add(new VersionEntry
            {
                Id = entry["id"]?.ToString() ?? "",
                Type = entry["type"]?.ToString() ?? "",
                Url = entry["url"]?.ToString() ?? "",
                Sha1 = entry["sha1"]?.ToString() ?? "",
                ReleaseTime = time.ToLocalTime()
            });
        }
        return result;
    }

    public static bool Allowed(JArray? rules)
    {
        if (rules == null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules.OfType<JObject>())
        {
            var os = rule["os"] as JObject;
            var name = os?["name"]?.ToString();
            if (!string.IsNullOrEmpty(name) && name != "windows") continue;
            var arch = os?["arch"]?.ToString();
            if (!string.IsNullOrEmpty(arch) && arch != "x86") continue; // 规则针对 32 位 Java
            var versionRule = os?["version"]?.ToString();
            if (!string.IsNullOrEmpty(versionRule) && !Regex.IsMatch(OsVersion, versionRule)) continue;
            if (rule["features"] is JObject features && features.HasValues) continue;
            allowed = rule["action"]?.ToString() == "allow";
        }
        return allowed;
    }

    public static List<RemoteFile> Libraries(JObject version)
    {
        var result = new List<RemoteFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (version["libraries"] is not JArray libraries) return result;

        void Add(RemoteFile file)
        {
            if (file.Path.Length == 0 || !seen.Add(file.Path)) return;
            result.Add(file);
        }

        foreach (var library in libraries.OfType<JObject>())
        {
            if (!Allowed(library["rules"] as JArray)) continue;
            var downloads = library["downloads"] as JObject;
            var artifact = downloads?["artifact"] as JObject;
            var name = library["name"]?.ToString() ?? "";

            if (artifact != null && artifact["url"] != null)
            {
                Add(new RemoteFile
                {
                    Url = artifact["url"]!.ToString(),
                    Path = artifact["path"]?.ToString() ?? MavenPath(name),
                    Sha1 = artifact["sha1"]?.ToString(),
                    Size = artifact["size"]?.Value<long>()
                });
            }
            else if (downloads == null && name.Length > 0 && library["url"] != null)
            {
                var path = MavenPath(name);
                var baseUrl = library["url"]!.ToString();
                if (!baseUrl.EndsWith("/")) baseUrl += "/";
                Add(new RemoteFile
                {
                    Url = baseUrl + path,
                    Path = path,
                    Sha1 = library["sha1"]?.ToString(),
                    Size = library["size"]?.Value<long>()
                });
            }

            var classifier = library["natives"]?["windows"]?.ToString();
            if (string.IsNullOrEmpty(classifier)) continue;
            classifier = classifier.Replace("${arch}", "64");
            if (downloads?["classifiers"]?[classifier] is not JObject native) continue;
            Add(new RemoteFile
            {
                Url = native["url"]!.ToString(),
                Path = native["path"]!.ToString(),
                Sha1 = native["sha1"]?.ToString(),
                Size = native["size"]?.Value<long>()
            });
        }
        return result;
    }

    public static List<RemoteFile> AssetObjects(JObject index, out bool isVirtual, out bool mapToResources)
    {
        isVirtual = index["virtual"]?.Value<bool>() ?? false;
        mapToResources = index["map_to_resources"]?.Value<bool>() ?? false;
        var seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (index["objects"] is JObject objects)
        {
            foreach (var property in objects.Properties())
            {
                var hash = property.Value["hash"]?.ToString();
                if (string.IsNullOrEmpty(hash) || seen.ContainsKey(hash)) continue;
                seen[hash] = property.Value["size"]?.Value<long>() ?? 0;
            }
        }
        return seen.Select(pair => new RemoteFile
        {
            Url = $"https://resources.download.minecraft.net/{pair.Key.Substring(0, 2)}/{pair.Key}",
            Path = $"{pair.Key.Substring(0, 2)}/{pair.Key}",
            Sha1 = pair.Key,
            Size = pair.Value == 0 ? null : pair.Value,
            VerifyHash = false
        }).ToList();
    }

    public static string MavenPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return "";
        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group.Replace('.', '/')}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    public static string JavaRequirement(JObject version)
    {
        var major = version["javaVersion"]?["majorVersion"]?.Value<int?>();
        return major.HasValue ? $"Java {major}" : "";
    }
}
