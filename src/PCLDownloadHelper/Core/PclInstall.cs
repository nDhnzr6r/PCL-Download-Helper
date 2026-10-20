using System.Diagnostics;
using System.IO;

namespace PclDownloadHelper.Core;

/// <summary>PCL 启动器定位、版本识别与设置修复。</summary>
public sealed class PclInstall
{
    /// <summary>关闭游戏下载功能的起始版本。</summary>
    public static readonly Version RestrictedFrom = new(2, 99, 0, 0);

    public const string UpdateKey = "SystemSystemUpdate";
    public const string UpdateDisabled = "3";

    private static readonly string[] LauncherNames =
    {
        "plain craft launcher 2.exe", "plain craft launcher.exe", "pcl.exe", "pcl2.exe"
    };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "windows.old", "program files", "program files (x86)", "programdata", "$recycle.bin",
        "system volume information", "recovery", "perflogs", "msocache", "appdata", "node_modules"
    };

    public string ExePath { get; private set; } = "";
    public Version? Version { get; private set; }
    public SetupIni Setup { get; private set; } = null!;
    public string SetupPath { get; private set; } = "";
    public string McDir { get; private set; } = "";
    public string Root => Path.GetDirectoryName(ExePath) ?? "";

    public string Name => Path.GetFileNameWithoutExtension(ExePath);

    public string VersionText => Version == null ? "未知" : Version.ToString();

    /// <summary>该版本是否已经移除了游戏下载功能。</summary>
    public bool Restricted => Version != null && Version >= RestrictedFrom;

    public string? UpdateValue => Setup.Get(UpdateKey) ?? "0";

    public bool Fixed => UpdateValue == UpdateDisabled;

    public bool NeedsFix => !Restricted && !Fixed;

    public static PclInstall? Load(string exePath)
    {
        try
        {
            var full = Path.GetFullPath(exePath);
            if (!File.Exists(full)) return null;
            var root = Path.GetDirectoryName(full) ?? "";
            var setupPath = Path.Combine(root, "PCL", "Setup.ini");
            var setup = new SetupIni(setupPath);
            return new PclInstall
            {
                ExePath = full,
                Version = ReadVersion(full),
                Setup = setup,
                SetupPath = setupPath,
                McDir = ResolveMcDir(root, setup.Get("LaunchFolderSelect"))
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>在指定目录里寻找启动器。</summary>
    public static string? FindIn(string folder)
    {
        if (File.Exists(folder)) return folder;
        if (!Directory.Exists(folder)) return null;
        foreach (var name in LauncherNames)
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.exe"))
            {
                var name = Path.GetFileName(file).ToLowerInvariant();
                if (name.Contains("pcl") || name.Contains("plain craft launcher")) return file;
            }
        }
        catch (Exception)
        {
            // 无权限访问时忽略
        }
        return null;
    }

    /// <summary>在本地磁盘浅层目录中查找启动器。</summary>
    public static List<string> Scan(TimeSpan budget)
    {
        var result = new List<string>();
        var deadline = DateTime.UtcNow + budget;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            if (DateTime.UtcNow > deadline) break;
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((drive.RootDirectory.FullName, 0));
            while (queue.Count > 0)
            {
                if (DateTime.UtcNow > deadline) break;
                var (folder, depth) = queue.Dequeue();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        var name = Path.GetFileName(file).ToLowerInvariant();
                        if (LauncherNames.Contains(name)) result.Add(file);
                    }
                }
                catch (Exception)
                {
                    // 跳过无权访问的目录
                }
                if (depth >= 2) continue;
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(folder))
                    {
                        var name = Path.GetFileName(dir).ToLowerInvariant();
                        if (SkipDirs.Contains(name) || name.StartsWith(".") || name.StartsWith("$")) continue;
                        queue.Enqueue((dir, depth + 1));
                    }
                }
                catch (Exception)
                {
                    // 跳过无权访问的目录
                }
            }
        }
        return result;
    }

    public static bool IsRunning(string exePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>写入 SystemSystemUpdate:3，阻止启动器自动升级。</summary>
    public void ApplyFix()
    {
        Setup.Set(UpdateKey, UpdateDisabled);
        Setup.Save();
        Setup = new SetupIni(SetupPath);
        if (!Fixed) throw new IOException("设置写入后校验失败");
    }

    private static Version? ReadVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (Version.TryParse(info.FileVersion, out var version)) return version;
            if (Version.TryParse(info.ProductVersion, out version)) return version;
        }
        catch (Exception)
        {
            // 读取失败时视为未知版本
        }
        return null;
    }

    private static string ResolveMcDir(string root, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Path.Combine(root, ".minecraft");
        raw = raw.Trim();
        if (raw.StartsWith("$"))
        {
            var relative = raw.Substring(1).TrimStart('\\', '/');
            return relative.Length == 0 ? Path.Combine(root, ".minecraft") : Path.Combine(root, relative);
        }
        return Path.IsPathRooted(raw) ? raw.TrimEnd('\\', '/') : Path.Combine(root, raw);
    }
}
