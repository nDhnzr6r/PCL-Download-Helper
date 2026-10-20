using System.IO;
using Newtonsoft.Json.Linq;

namespace PclDownloadHelper.Core;

/// <summary>HMCL 启动器定位与游戏目录解析。</summary>
public static class HmclInstall
{
    public static string? FindIn(string folder)
    {
        if (File.Exists(folder)) return folder;
        if (!Directory.Exists(folder)) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.exe"))
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (name.Contains("hmcl")) return file;
            }
            foreach (var file in Directory.EnumerateFiles(folder, "*.jar"))
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (name.StartsWith("hmcl")) return file;
            }
        }
        catch (Exception)
        {
            // 忽略权限问题
        }
        return null;
    }

    public static string? Scan(TimeSpan budget)
    {
        foreach (var path in PclInstall.Scan(budget, name => name.Contains("hmcl")))
        {
            return path;
        }
        return null;
    }

    /// <summary>解析 HMCL 使用的游戏目录；找不到时返回 null。</summary>
    public static string? ResolveGameDir(string exePath)
    {
        var root = Path.GetDirectoryName(exePath) ?? "";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var configRoots = new List<string>
        {
            Path.Combine(root, "config"),
            Path.Combine(appData, "hmcl", "config"),
            Path.Combine(root, ".hmcl"),
            Path.Combine(home, ".hmcl")
        };

        foreach (var configRoot in configRoots)
        {
            var settings = Path.Combine(configRoot, "launcher-settings.json");
            var custom = ReadCommonDirectory(settings);
            if (!string.IsNullOrEmpty(custom) && Directory.Exists(custom)) return custom;

            var directories = Path.Combine(configRoot, "game-directories.json");
            var first = ReadFirstGameDirectory(directories);
            if (!string.IsNullOrEmpty(first) && Directory.Exists(first)) return first;
        }

        foreach (var file in new[]
                 {
                     Path.Combine(root, ".hmcl.json"),
                     Path.Combine(root, "hmcl.json"),
                     Path.Combine(appData, "hmcl.json"),
                     Path.Combine(appData, ".hmcl.json"),
                     Path.Combine(home, ".hmcl.json")
                 })
        {
            var dir = ReadGameDirKey(file);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }

        var local = Path.Combine(root, ".minecraft");
        if (Directory.Exists(local)) return local;
        var roaming = Path.Combine(appData, ".minecraft");
        if (Directory.Exists(roaming)) return roaming;
        return null;
    }

    private static string? ReadCommonDirectory(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = JObject.Parse(File.ReadAllText(path));
            var type = json["commonDirectoryType"]?.ToString();
            var value = json["commonDirectory"]?.ToString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!string.IsNullOrEmpty(type) && !type.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("2", StringComparison.OrdinalIgnoreCase))
                return null;
            return value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadFirstGameDirectory(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = JToken.Parse(File.ReadAllText(path));
            var list = json is JArray array
                ? array
                : (json["gameDirectories"] as JArray ?? json["directories"] as JArray);
            if (list == null) return null;
            foreach (var item in list)
            {
                var value = item["path"]?.ToString() ?? item["gameDir"]?.ToString() ?? item.ToString();
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) return value;
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    private static string? ReadGameDirKey(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = JObject.Parse(File.ReadAllText(path));
            foreach (var key in new[] { "gameDir", "commonDirectory", "selectedGameDir", "minecraftDir" })
            {
                var value = json[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }
}
