using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PclDownloadHelper.Core;

public sealed class JavaRuntime
{
    public string ExePath { get; init; } = "";
    public int Major { get; init; }
    public string VersionText { get; init; } = "";

    public override string ToString() => $"Java {Major} ({VersionText})";
}

/// <summary>查找本机可用的 Java。</summary>
public static class JavaFinder
{
    private static List<JavaRuntime>? _cache;

    public static IReadOnlyList<JavaRuntime> Discover(bool refresh = false)
    {
        if (_cache != null && !refresh) return _cache;
        var result = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            var path = exe.Trim('"');
            if (!File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            if (!seen.Add(full)) return;
            var home = Path.GetDirectoryName(Path.GetDirectoryName(full));
            var (major, text) = ReadVersion(home, full);
            if (major <= 0) return;
            result.Add(new JavaRuntime { ExePath = full, Major = major, VersionText = text });
        }

        foreach (var home in RegistryHomes()) AddCandidate(Path.Combine(home, "bin", "javaw.exe"));
        foreach (var root in ProgramFilesRoots())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    if (!string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase)) continue;
                    AddCandidate(Path.Combine(dir, "javaw.exe"));
                    if (result.Count > 60) break;
                }
            }
            catch (Exception)
            {
                // 无权限访问时跳过
            }
        }
        foreach (var runtimeRoot in RuntimeRoots())
        {
            if (!Directory.Exists(runtimeRoot)) continue;
            try
            {
                foreach (var exe in Directory.EnumerateFiles(runtimeRoot, "javaw.exe", SearchOption.AllDirectories))
            {
                    if (string.Equals(Path.GetFileName(Path.GetDirectoryName(exe)), "bin", StringComparison.OrdinalIgnoreCase))
                        AddCandidate(exe);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }
        AddCandidate(FindOnPath());

        result.Sort((a, b) => b.Major.CompareTo(a.Major));
        _cache = result;
        return result;
    }

    /// <summary>按 Minecraft 需要的 Java 版本挑选，优先精确匹配。</summary>
    public static JavaRuntime? Pick(int? required)
    {
        var all = Discover();
        if (all.Count == 0) return null;
        if (required.HasValue)
        {
            var exact = all.FirstOrDefault(java => java.Major == required.Value);
            if (exact != null) return exact;
            var bigger = all.Where(java => java.Major >= required.Value).OrderBy(java => java.Major).FirstOrDefault();
            if (bigger != null) return bigger;
        }
        return all.FirstOrDefault(java => java.Major >= 8) ?? all[0];
    }

    private static IEnumerable<string> RegistryHomes()
    {
        var keys = new[]
        {
            @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\Eclipse Adoptium\JDK", @"SOFTWARE\Eclipse Foundation\JDK",
            @"SOFTWARE\Microsoft\JDK"
        };
        foreach (var key in keys)
        {
            foreach (var home in ReadKeyHomes(key)) yield return home;
        }
        var env = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(env)) yield return env;
    }

    private static List<string> ReadKeyHomes(string key)
    {
        var homes = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = baseKey.OpenSubKey(key);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(name);
                    var home = sub?.GetValue("JavaHome") as string ?? sub?.GetValue("Path") as string;
                    if (!string.IsNullOrEmpty(home)) homes.Add(home);
                }
            }
            catch (Exception)
            {
                // 忽略注册表读取失败
            }
        }
        return homes;
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        var bases = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var root in bases)
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var name in new[]
                     {
                         "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft",
                         "Amazon Corretto", "RedHat", "Semeru", "JetBrains"
                     })
            {
                yield return Path.Combine(root, name);
            }
        }
    }

    private static IEnumerable<string> RuntimeRoots()
    {
        foreach (var appData in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (string.IsNullOrEmpty(appData)) continue;
            yield return Path.Combine(appData, ".minecraft", "runtime");
        }
    }

    private static string? FindOnPath()
    {
        try
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var name in new[] { "javaw.exe", "java.exe" })
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch (Exception)
        {
            // 忽略
        }
        return null;
    }

    private static (int Major, string Text) ReadVersion(string? home, string exe)
    {
        try
        {
            if (!string.IsNullOrEmpty(home))
            {
                var release = Path.Combine(home, "release");
                if (File.Exists(release))
                {
                    foreach (var line in File.ReadAllLines(release))
                    {
                        if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal)) continue;
                        var text = line.Substring("JAVA_VERSION=".Length).Trim('"');
                        var major = ParseMajor(text);
                        if (major > 0) return (major, text);
                    }
                }
            }
            var match = Regex.Match(exe, @"(?:jdk|jre|java)[-_]?(\d+)");
            if (match.Success)
            {
                var value = match.Groups[1].Value;
                return value == "1" ? (8, "1.8") : (int.Parse(value), value);
            }
        }
        catch (Exception)
        {
            // 忽略
        }
        return (0, "");
    }

    private static int ParseMajor(string text)
    {
        var parts = text.Split('.', '-', '_', '+');
        if (parts.Length == 0) return 0;
        if (parts[0] == "1" && parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
        return int.TryParse(parts[0], out var major) ? major : 0;
    }
}
