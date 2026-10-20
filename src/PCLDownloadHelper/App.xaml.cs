using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.IO;

namespace PclDownloadHelper;

public partial class App : System.Windows.Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCLDownloadHelper", "error.log");

    static App()
    {
        // Newtonsoft.Json 以资源形式内嵌，保证发布时只有一个可执行文件
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new System.Reflection.AssemblyName(args.Name).Name;
            if (name != "Newtonsoft.Json") return null;
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Newtonsoft.Json.dll");
            if (stream == null) return null;
            var buffer = new byte[stream.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count <= 0) break;
                read += count;
            }
            return System.Reflection.Assembly.Load(buffer);
        };
    }

    public static Window? Shell { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n");
        }
        catch (Exception)
        {
            // 忽略日志写入失败
        }
    }

    /// <summary>Windows 11 上启用圆角窗口。</summary>
    public static void EnableRoundedCorners(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // 旧系统忽略
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
