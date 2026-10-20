using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PclDownloadHelper.Core;

public enum LoaderKind
{
    None,
    Forge,
    NeoForge,
    Fabric,
    Quilt
}

/// <summary>一个可选的 Fabric API 版本。</summary>
public sealed class FabricApiVersion
{
    public string Version { get; init; } = "";
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";

    public override string ToString() => Version;
}

/// <summary>加载器版本列表与安装包地址。</summary>
public static class LoaderCatalog
{
    private const string ForgeListMirror = "https://bmclapi2.bangbang93.com/forge/minecraft/{0}";
    private const string ForgeListOfficial = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json";
    private const string NeoForgeList = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/{0}";
    private const string FabricList = "https://meta.fabricmc.net/v2/versions/loader/{0}";
    private const string QuiltList = "https://meta.quiltmc.org/v3/versions/loader/{0}";
    private const string FabricProfile = "https://meta.fabricmc.net/v2/versions/loader/{0}/{1}/profile/json";
    private const string QuiltProfile = "https://meta.quiltmc.org/v3/versions/loader/{0}/{1}/profile/json";
    private const string ModrinthFabricApi = "https://api.modrinth.com/v2/project/fabric-api/version?game_versions=[\"{0}\"]&loaders=[\"fabric\"]";

    public static string ForgeInstallerUrl(string mcId, string version) =>
        $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcId}-{version}/forge-{mcId}-{version}-installer.jar";

    public static string NeoForgeInstallerUrl(string mcId, string version)
    {
        // 1.20.1 及更早的 NeoForge 仍发布在 net.neoforged:forge 之下
        var legacy = version.StartsWith(mcId + "-", StringComparison.OrdinalIgnoreCase);
        return legacy
            ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{version}/forge-{version}-installer.jar"
            : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar";
    }

    public static async Task<List<string>> ForgeVersionsAsync(Http http, string mcId, CancellationToken token)
    {
        try
        {
            var data = await http.GetTokenAsync(string.Format(ForgeListMirror, mcId), token).ConfigureAwait(false);
            if (data is JArray array)
            {
                return array.Select(item => item["version"]?.ToString())
                    .Where(value => !string.IsNullOrEmpty(value)).Cast<string>().ToList();
            }
            if (data is JObject obj && obj["versions"] is JArray versions)
            {
                return versions.Select(item => item.ToString()).ToList();
            }
        }
        catch (Exception)
        {
            // 镜像失败时回退官方元数据
        }
        try
        {
            var metadata = await http.GetJsonAsync(ForgeListOfficial, token).ConfigureAwait(false);
            if (metadata[mcId] is JArray list)
                return list.Select(item => item.ToString()).Reverse().ToList();
        }
        catch (Exception)
        {
            // 交给上层提示
        }
        return new List<string>();
    }

    public static async Task<List<string>> NeoForgeVersionsAsync(Http http, string mcId, CancellationToken token)
    {
        var result = new List<string>();
        var prefixes = new List<string> { mcId + "-", mcId + "." };
        if (mcId.StartsWith("1.", StringComparison.Ordinal))
            prefixes.Add(mcId.Substring(2) + ".");

        foreach (var artifact in new[] { "neoforge", "forge" })
        {
            try
            {
                var data = await http.GetJsonAsync(string.Format(NeoForgeList, artifact), token).ConfigureAwait(false);
                if (data["versions"] is not JArray versions) continue;
                foreach (var item in versions)
                {
                    var version = item.ToString();
                    if (prefixes.Any(prefix => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        result.Add(version);
                }
            }
            catch (Exception)
            {
                // 忽略单个源失败
            }
            if (result.Count > 0) break;
        }
        return result.Distinct().OrderByDescending(VersionSort).ToList();
    }

    public static async Task<List<string>> FabricVersionsAsync(Http http, string mcId, CancellationToken token)
    {
        var data = await http.GetTokenAsync(string.Format(FabricList, mcId), token).ConfigureAwait(false);
        if (data is not JArray array) return new List<string>();
        return array.Select(item => item["loader"]?["version"]?.ToString())
            .Where(value => !string.IsNullOrEmpty(value)).Cast<string>().ToList();
    }

    public static async Task<List<string>> QuiltVersionsAsync(Http http, string mcId, CancellationToken token)
    {
        var data = await http.GetTokenAsync(string.Format(QuiltList, mcId), token).ConfigureAwait(false);
        if (data is not JArray array) return new List<string>();
        return array.Select(item => item["loader"]?["version"]?.ToString())
            .Where(value => !string.IsNullOrEmpty(value)).Cast<string>().ToList();
    }

    public static async Task<JObject> ProfileAsync(Http http, LoaderKind kind, string mcId, string loaderVersion,
        CancellationToken token)
    {
        var pattern = kind == LoaderKind.Quilt ? QuiltProfile : FabricProfile;
        return await http.GetJsonAsync(string.Format(pattern, mcId, loaderVersion), token).ConfigureAwait(false);
    }

    /// <summary>列出与该 Minecraft 版本兼容的 Fabric API 版本，最新的在前。</summary>
    public static async Task<List<FabricApiVersion>> FabricApiVersionsAsync(Http http, string mcId,
        CancellationToken token)
    {
        var result = new List<FabricApiVersion>();
        try
        {
            var url = string.Format(ModrinthFabricApi, mcId);
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "PCLDownloadHelper/2.0 (github.com/nDhnzr6r)");
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return result;
            var json = JArray.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var entry in json)
            {
                var version = entry["version_number"]?.ToString();
                var file = entry["files"]?.FirstOrDefault(item => item["primary"]?.Value<bool>() == true)
                           ?? entry["files"]?.FirstOrDefault();
                var download = file?["url"]?.ToString();
                var name = file?["filename"]?.ToString();
                if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(download)) continue;
                result.Add(new FabricApiVersion
                {
                    Version = version,
                    Url = download,
                    FileName = name ?? $"fabric-api-{version}.jar"
                });
            }
        }
        catch (Exception)
        {
            // 获取失败时返回空列表，由界面提示
        }
        return result;
    }

    private static Version VersionSort(string value)
    {
        var text = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsDigit(ch) || ch == '.') text.Append(ch);
            else text.Append('.');
        }
        var parts = text.ToString().Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>();
        foreach (var part in parts.Take(4))
            numbers.Add(int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0);
        while (numbers.Count < 4) numbers.Add(0);
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }
}
