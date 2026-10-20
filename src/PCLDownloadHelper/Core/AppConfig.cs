using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace PclDownloadHelper.Core;

/// <summary>本地配置，保存在当前用户的本地应用数据目录。</summary>
public sealed class AppConfig
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCLDownloadHelper");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public string? PclExe { get; set; }
    public string? HmclExe { get; set; }
    public string? GameDir { get; set; }
    public bool DisclaimerAccepted { get; set; }
    public bool FixPrompted { get; set; }
    public bool PreferMirror { get; set; } = true;
    public string? LastVersion { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath, Encoding.UTF8);
                var config = JsonConvert.DeserializeObject<AppConfig>(text);
                if (config != null) return config;
            }
        }
        catch (Exception)
        {
            // 配置损坏时退回默认值
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 忽略写入失败
        }
    }
}
