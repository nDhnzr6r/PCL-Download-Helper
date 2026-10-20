using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PclDownloadHelper.Core;

namespace PclDownloadHelper.UI;

public partial class MainWindow : Window
{
    private static readonly string[] TypeNames = { "全部版本", "正式版", "快照", "远古版本" };

    private readonly AppConfig _config;
    private readonly SourceState _source;
    private readonly Http _http;

    private List<VersionEntry> _versions = new();
    private HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private List<LoaderOption> _loaders = new();
    private PclInstall? _pcl;
    private VersionItem? _selected;
    private LoaderOption? _loader;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _suppressSelection;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load();
        _source = new SourceState { PreferMirror = _config.PreferMirror };
        _source.Notified += text => Dispatcher.BeginInvoke(new Action(() => SetStatus(text)));
        _http = new Http(_source);
        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            _config.WindowWidth = ActualWidth;
            _config.WindowHeight = ActualHeight;
            _config.Save();
            _http.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.EnableRoundedCorners(this);
        if (_config.WindowWidth >= MinWidth && _config.WindowHeight >= MinHeight)
        {
            Width = _config.WindowWidth;
            Height = _config.WindowHeight;
        }

        TypeFilter.ItemsSource = TypeNames;
        TypeFilter.SelectedIndex = 1;
        MirrorCheck.IsChecked = _config.PreferMirror;
        NavDownload.IsChecked = true;

        await RestoreLauncherAsync().ConfigureAwait(true);
        await LoadManifestAsync().ConfigureAwait(true);
        UpdateSettingsCard();
    }

    // ---------- 启动器 ----------

    private async Task RestoreLauncherAsync()
    {
        var saved = _config.PclExe;
        if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
        {
            SetLauncher(saved);
            return;
        }
        SetStatus("正在查找启动器…");
        var found = await Task.Run(() => PclInstall.Scan(TimeSpan.FromSeconds(4))).ConfigureAwait(true);
        if (found.Count > 0)
        {
            SetLauncher(found[0]);
            return;
        }
        SetStatus("未找到启动器，可在「设置」中选择所在文件夹");
    }

    private void SetLauncher(string exePath)
    {
        var install = PclInstall.Load(exePath);
        if (install == null)
        {
            SetStatus("读取启动器信息失败");
            return;
        }
        _pcl = install;
        _config.PclExe = install.ExePath;
        _config.Save();
        RefreshInstalled();
        SidebarPath.Text = install.McDir;
        UpdateSettingsCard();
        SetStatus($"已连接启动器 {install.Name} {install.VersionText}");
        _ = PromptFixAsync();
    }

    private async Task PromptFixAsync()
    {
        if (_pcl == null) return;
        if (_pcl.Restricted)
        {
            if (_config.FixPrompted) return;
            _config.FixPrompted = true;
            _config.Save();
            NoticeDialog.Show(this, "当前启动器无法修复",
                $"检测到 {_pcl.Name} {_pcl.VersionText} 已移除游戏下载入口。\n\n" +
                "本程序会直接把游戏文件安装到启动器目录，装完在启动器里照常启动即可。",
                "开始下载");
            SetStatus("该启动器版本没有下载入口，可直接用本程序安装游戏");
            return;
        }
        if (_config.FixPrompted || _pcl.Fixed)
        {
            _config.FixPrompted = true;
            _config.Save();
            return;
        }

        _config.FixPrompted = true;
        _config.Save();
        var fix = NoticeDialog.Show(this, "是否启用下载修复？",
            $"检测到 {_pcl.Name} {_pcl.VersionText}。\n\n" +
            "启动器默认会在有新版本时自动下载更新，升级后会移除游戏下载入口。" +
            "启用修复会把更新提示设为关闭，让当前版本保持可下载。",
            "启用修复", "暂不修复");
        if (fix != true) return;
        ApplyFix();
    }

    private void ApplyFix()
    {
        if (_pcl == null) return;
        if (PclInstall.IsRunning(_pcl.ExePath))
        {
            NoticeDialog.Show(this, "启动器正在运行", "请先退出启动器，再执行修复。", "知道了");
            return;
        }
        try
        {
            _pcl.ApplyFix();
            UpdateSettingsCard();
            SetStatus("已启用下载修复，重新打开启动器后生效");
        }
        catch (Exception ex)
        {
            NoticeDialog.Show(this, "修复失败", ex.Message, "知道了");
        }
    }

    private void UpdateSettingsCard()
    {
        if (_pcl == null)
        {
            SettingsLauncherName.Text = "尚未选择启动器";
            SettingsLauncherState.Text = "选择 PCL 所在的文件夹，程序会把游戏安装到它的 .minecraft 目录。";
            FixButton.IsEnabled = false;
            return;
        }
        SettingsLauncherName.Text = $"{_pcl.Name}  {_pcl.VersionText}   ·   {_pcl.ExePath}";
        SettingsLauncherState.Text = _pcl.Restricted
            ? "该版本已移除游戏下载入口，本程序可直接安装游戏。"
            : _pcl.Fixed
                ? "下载修复已生效：启动器不会自动升级，下载入口保持可用。"
                : "下载修复未启用：启动器可能自动升级到 2.99 并失去下载入口。";
        FixButton.IsEnabled = _pcl.NeedsFix;
        FixButton.Content = _pcl.Fixed ? "修复已生效" : "修复下载功能";
        SidebarPath.Text = _pcl.McDir;
    }

    private void RefreshInstalled()
    {
        _installed.Clear();
        if (_pcl == null) return;
        var root = Path.Combine(_pcl.McDir, "versions");
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (File.Exists(Path.Combine(dir, name + ".json"))) _installed.Add(name);
            }
        }
        catch (Exception)
        {
            // 忽略读取失败
        }
    }

    // ---------- 版本列表 ----------

    private async Task LoadManifestAsync()
    {
        ListPlaceholder.Text = "正在获取版本列表…";
        ListPlaceholder.Visibility = Visibility.Visible;
        VersionList.ItemsSource = null;
        try
        {
            var json = await _http.GetJsonAsync(McMeta.ManifestUrl, CancellationToken.None).ConfigureAwait(true);
            _versions = McMeta.ParseManifest(json);
            SetStatus($"已获取 {_versions.Count} 个版本");
        }
        catch (Exception ex)
        {
            _versions = new List<VersionEntry>();
            ListPlaceholder.Text = "获取版本列表失败，请检查网络后点右上角重试";
            SetStatus(ex.Message);
        }
        RefreshList();
    }

    private void RefreshList()
    {
        var keyword = SearchBox.Text.Trim();
        if (keyword == "搜索版本号") keyword = "";
        var mode = TypeFilter.SelectedIndex;
        var items = _versions
            .Where(version => mode switch
            {
                1 => version.Type == "release",
                2 => version.Type == "snapshot",
                3 => version.Type is "old_beta" or "old_alpha",
                _ => true
            })
            .Where(version => keyword.Length == 0 ||
                              version.Id.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(version => new VersionItem(version, _installed.Contains(version.Id)))
            .ToList();

        _suppressSelection = true;
        VersionList.ItemsSource = items;
        _suppressSelection = false;

        ListPlaceholder.Text = _versions.Count == 0 ? ListPlaceholder.Text : "没有匹配的版本";
        ListPlaceholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenDetail(VersionItem item)
    {
        _selected = item;
        DetailVersionName.Text = item.Id;
        DetailVersionInfo.Text = $"{item.Subtitle}   ·   {(item.Installed ? "已安装" : "未安装")}";
        DetailIcon.Content = item;
        DetailIcon.ContentTemplateSelector = (DataTemplateSelector)FindResource("VersionIcon");
        BuildLoaders(item);
        ShowPage(PageDetail);
    }

    private void BuildLoaders(VersionItem item)
    {
        string StatusFor(string prefix) =>
            _installed.Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ? "已安装" : "";

        _loaders = new List<LoaderOption>
        {
            new()
            {
                Kind = LoaderKind.None, Name = "原版",
                Description = "只安装 Minecraft 本体",
                Status = item.Installed ? "已安装" : "可以添加"
            },
            new()
            {
                Kind = LoaderKind.Forge, Name = "Forge",
                Description = "经典模组加载器，适合大多数模组包",
                Status = StatusFor($"{item.Id}-Forge")
            },
            new()
            {
                Kind = LoaderKind.NeoForge, Name = "NeoForge",
                Description = "Forge 的新分支，1.20.2 之后的主流选择",
                Status = StatusFor($"{item.Id}-NeoForge")
            },
            new()
            {
                Kind = LoaderKind.Fabric, Name = "Fabric",
                Description = "轻量加载器，启动快，可同时安装 Fabric API",
                Status = StatusFor($"{item.Id}-Fabric")
            },
            new()
            {
                Kind = LoaderKind.Quilt, Name = "Quilt",
                Description = "Fabric 的衍生加载器",
                Status = StatusFor($"{item.Id}-Quilt")
            }
        };
        LoaderList.ItemsSource = _loaders;
        LoaderList.SelectedIndex = 0;
    }

    private async void LoaderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (LoaderList.SelectedItem is not LoaderOption option)
            {
                _loader = null;
                return;
            }
            _loader = option;
            var needsVersion = option.Kind != LoaderKind.None;
            LoaderVersionBox.Visibility = needsVersion ? Visibility.Visible : Visibility.Collapsed;
            FabricApiCheck.Visibility = option.Kind == LoaderKind.Fabric ? Visibility.Visible : Visibility.Collapsed;
            StartButton.IsEnabled = !needsVersion;
            if (!needsVersion) return;

            await LoadLoaderVersionsAsync(option).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SetStatus(ex.Message);
        }
    }

    private async Task LoadLoaderVersionsAsync(LoaderOption option)
    {
        LoaderVersionBox.ItemsSource = new[] { "正在获取版本…" };
        LoaderVersionBox.SelectedIndex = 0;
        LoaderVersionBox.IsEnabled = false;
        StartButton.IsEnabled = false;
        if (_selected == null) return;
        var versionId = _selected.Id;
        List<string> list;
        try
        {
            list = option.Kind switch
            {
                LoaderKind.Forge => await LoaderCatalog.ForgeVersionsAsync(_http, versionId, CancellationToken.None)
                    .ConfigureAwait(true),
                LoaderKind.NeoForge => await LoaderCatalog.NeoForgeVersionsAsync(_http, versionId, CancellationToken.None)
                    .ConfigureAwait(true),
                LoaderKind.Fabric => await LoaderCatalog.FabricVersionsAsync(_http, versionId, CancellationToken.None)
                    .ConfigureAwait(true),
                LoaderKind.Quilt => await LoaderCatalog.QuiltVersionsAsync(_http, versionId, CancellationToken.None)
                    .ConfigureAwait(true),
                _ => new List<string>()
            };
        }
        catch (Exception ex)
        {
            list = new List<string>();
            SetStatus(ex.Message);
        }
        if (_loader != option) return; // 期间切换了加载器
        LoaderVersionBox.ItemsSource = list.Count > 0 ? list : new[] { "没有可用版本" };
        LoaderVersionBox.IsEnabled = list.Count > 0;
        LoaderVersionBox.SelectedIndex = list.Count > 0 ? 0 : -1;
        StartButton.IsEnabled = list.Count > 0;
        if (list.Count == 0)
            SetStatus($"该 Minecraft 版本没有可用的 {option.Name} 版本");
    }

    // ---------- 安装 ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null || _loader == null) return;
        if (_pcl == null)
        {
            NoticeDialog.Show(this, "还没有选择启动器", "请先在「设置」中选择 PCL 所在的文件夹。", "知道了");
            return;
        }

        var options = new InstallOptions
        {
            Entry = _selected.Entry,
            Loader = _loader.Kind,
            LoaderVersion = LoaderVersionBox.SelectedItem as string,
            InstallFabricApi = FabricApiCheck.IsChecked == true
        };

        _busy = true;
        _cts = new CancellationTokenSource();
        ShowPage(PageProgress);
        ProgressTitle.Text = $"正在安装 {options.Entry.Id}";
        ProgressStage.Text = "准备中…";
        ProgressDetail.Text = "";
        ProgressBar.Value = 0;
        ProgressActions.Visibility = Visibility.Visible;
        DoneActions.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;

        var progress = new Progress<InstallProgress>(OnProgress);
        try
        {
            var installer = new Installer(_pcl.McDir, _http, progress,
                text => Dispatcher.BeginInvoke(new Action(() => SetStatus(text))));
            var result = await Task.Run(() => installer.InstallAsync(options, _cts.Token)).ConfigureAwait(true);
            _busy = false;
            ProgressTitle.Text = "安装完成";
            ProgressStage.Text = result.Comment.Length > 0
                ? $"{result.Id} 已安装（需要 {result.Comment}）"
                : $"{result.Id} 已安装";
            ProgressDetail.Text = result.Directory;
            ProgressBar.Value = 1;
            ProgressActions.Visibility = Visibility.Collapsed;
            DoneActions.Visibility = Visibility.Visible;
            RefreshInstalled();
            SetStatus($"已安装 {result.Id}，重新打开启动器即可在版本列表中看到");
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            ShowPage(PageDetail);
            SetStatus("已取消安装");
        }
        catch (Exception ex)
        {
            _busy = false;
            App.Log(ex);
            ShowPage(PageDetail);
            SetStatus($"安装失败：{ex.Message}");
            NoticeDialog.Show(this, "安装失败", ex.Message, "知道了");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(InstallProgress progress)
    {
        ProgressBar.Value = progress.Overall;
        ProgressStage.Text = progress.Stage;
        var parts = new List<string>();
        if (progress.FilesTotal > 0) parts.Add($"{progress.FilesDone:N0} / {progress.FilesTotal:N0} 个文件");
        if (progress.Speed > 1024) parts.Add($"{FormatSize(progress.Speed)}/s");
        parts.Add($"{progress.Overall * 100:0}%");
        if (!string.IsNullOrEmpty(progress.Detail)) parts.Add(progress.Detail);
        ProgressDetail.Text = string.Join("   ·   ", parts);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressStage.Text = "正在取消…";
    }

    private void OpenMcDir_Click(object sender, RoutedEventArgs e)
    {
        if (_pcl != null) OpenPath(_pcl.McDir);
    }

    private void BackToDownload_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstalled();
        RefreshList();
        ShowPage(PageDownload);
    }

    // ---------- 设置与导航 ----------

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 PCL 启动器所在的文件夹"
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var exe = PclInstall.FindIn(dialog.SelectedPath);
        if (exe == null)
        {
            NoticeDialog.Show(this, "没有找到启动器", "该文件夹里没有 PCL 启动器。", "知道了");
            return;
        }
        SetLauncher(exe);
    }

    private void OpenLauncherDir_Click(object sender, RoutedEventArgs e)
    {
        if (_pcl != null) OpenPath(_pcl.Root);
    }

    private void Fix_Click(object sender, RoutedEventArgs e)
    {
        ApplyFix();
    }

    private void MirrorCheck_Changed(object sender, RoutedEventArgs e)
    {
        var value = MirrorCheck.IsChecked == true;
        _source.PreferMirror = value;
        _config.PreferMirror = value;
        _config.Save();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == NavSettings) ShowPage(PageSettings);
        else ShowPage(PageDownload);
    }

    private void ShowPage(FrameworkElement page)
    {
        foreach (var element in new FrameworkElement[] { PageDownload, PageDetail, PageProgress, PageSettings })
            element.Visibility = element == page ? Visibility.Visible : Visibility.Collapsed;
        if (page == PageDownload)
        {
            _suppressSelection = true;
            VersionList.SelectedItem = null;
            _suppressSelection = false;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstalled();
        RefreshList();
        ShowPage(PageDownload);
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _busy) return;
        if (VersionList.SelectedItem is VersionItem item) OpenDetail(item);
    }

    private void VersionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VersionList.SelectedItem is VersionItem item) OpenDetail(item);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshList();
    }

    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshList();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await LoadManifestAsync();
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 忽略打开失败
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    // ---------- 标题栏 ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
