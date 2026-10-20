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
    private string? _gameDir;
    private string _gameDirSource = "";
    private VersionItem? _selected;
    private LoaderOption? _loader;
    private List<FabricApiVersion> _fabricApiVersions = new();
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

        if (!_config.DisclaimerAccepted)
        {
            var accepted = NoticeDialog.Show(this, "使用前请了解",
                "· 本程序是个人开发的第三方工具，与 PCL（Plain Craft Launcher）、HMCL 等游戏启动器及其作者" +
                "没有任何关系，也没有获得任何授权。\n\n" +
                "· 程序下载的文件来自 Mojang 官方服务器或公开镜像，仅供学习和研究使用，请在 24 小时内删除。\n\n" +
                "· 请支持正版 Minecraft，通过官方渠道购买游戏。",
                "我已了解并继续");
            if (!accepted)
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }
            _config.DisclaimerAccepted = true;
            _config.Save();
        }

        await RestoreLaunchersAsync().ConfigureAwait(true);
        await LoadManifestAsync().ConfigureAwait(true);
        UpdateSettingsCard();
    }

    // ---------- 启动器与游戏目录 ----------

    private async Task RestoreLaunchersAsync()
    {
        var pclPath = _config.PclExe;
        if (string.IsNullOrEmpty(pclPath) || !File.Exists(pclPath))
        {
            SetStatus("正在查找启动器…");
            pclPath = await Task.Run(() => PclInstall.Scan(TimeSpan.FromSeconds(3)).FirstOrDefault())
                .ConfigureAwait(true);
        }
        if (!string.IsNullOrEmpty(pclPath) && File.Exists(pclPath)) ApplyPcl(pclPath);

        var hmcl = _config.HmclExe;
        if (string.IsNullOrEmpty(hmcl) || !File.Exists(hmcl))
        {
            hmcl = await Task.Run(() => HmclInstall.Scan(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
        }
        if (!string.IsNullOrEmpty(hmcl) && File.Exists(hmcl))
        {
            _config.HmclExe = hmcl;
            _config.Save();
        }

        if (!string.IsNullOrEmpty(_config.GameDir) && Directory.Exists(_config.GameDir))
        {
            SetGameDir(_config.GameDir!, "手动选择");
        }
        else if (_pcl != null)
        {
            SetGameDir(_pcl.McDir, "来自 PCL 启动器");
        }
        else if (!string.IsNullOrEmpty(hmcl))
        {
            var dir = HmclInstall.ResolveGameDir(hmcl);
            if (!string.IsNullOrEmpty(dir)) SetGameDir(dir, "来自 HMCL 启动器");
        }

        if (_gameDir == null)
        {
            SetStatus("没有找到启动器，请在设置里选择游戏目录");
            GameDirText.Text = "尚未选择";
            GameDirSource.Text = "没有找到 PCL 或 HMCL，请在设置里指定游戏目录";
        }
    }

    private void ApplyPcl(string exePath)
    {
        var install = PclInstall.Load(exePath);
        if (install == null) return;
        _pcl = install;
        _config.PclExe = install.ExePath;
        _config.Save();
        SetStatus($"已连接 PCL {install.VersionText}");
        Dispatcher.BeginInvoke(new Action(() => { _ = PromptFixAsync(); }),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void SetGameDir(string path, string source)
    {
        _gameDir = path;
        _gameDirSource = source;
        SidebarPath.Text = path;
        GameDirText.Text = path;
        GameDirSource.Text = $"来源：{source}";
        RefreshInstalled();
        RefreshList();
    }

    private async Task PromptFixAsync()
    {
        if (_pcl == null) return;
        if (_pcl.Restricted)
        {
            if (_config.FixPrompted) return;
            _config.FixPrompted = true;
            _config.Save();
            NoticeDialog.Show(this, "当前启动器无法阻挠更新",
                $"检测到 {_pcl.Name} {_pcl.VersionText} 已经移除了游戏下载入口。\n\n" +
                "本程序会直接把游戏文件安装到游戏目录，装完在启动器里照常启动即可。",
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
        var fix = NoticeDialog.Show(this, "是否阻挠启动器更新？",
            $"检测到 {_pcl.Name} {_pcl.VersionText}。\n\n" +
            "启动器默认会在有新版本时自动下载更新，升级后可能移除游戏下载入口。" +
            "阻挠更新会把更新提示设为关闭，让当前版本保持可下载。",
            "阻挠更新", "暂不阻挠");
        if (fix != true) return;
        ApplyFix();
    }

    private void ApplyFix()
    {
        if (_pcl == null) return;
        if (PclInstall.IsRunning(_pcl.ExePath))
        {
            NoticeDialog.Show(this, "启动器正在运行", "请先退出启动器，再执行阻挠更新。", "知道了");
            return;
        }
        try
        {
            _pcl.ApplyFix();
            UpdateSettingsCard();
            SetStatus("已阻挠更新，重新打开启动器后生效");
        }
        catch (Exception ex)
        {
            NoticeDialog.Show(this, "阻挠更新失败", ex.Message, "知道了");
        }
    }

    private void UpdateSettingsCard()
    {
        FixButton.IsEnabled = false;
        if (_pcl == null)
        {
            LauncherStateText.Text = "未检测到 PCL 启动器。HMCL 不需要阻挠更新，直接下载游戏即可。";
            FixButton.Content = "未检测到 PCL";
        }
        else if (_pcl.Restricted)
        {
            LauncherStateText.Text = $"PCL {_pcl.VersionText} 已移除游戏下载入口，本程序可以直接安装游戏。";
            FixButton.Content = "该版本无法阻挠";
        }
        else if (_pcl.Fixed)
        {
            LauncherStateText.Text = $"PCL {_pcl.VersionText}：已阻挠更新，启动器不会自动升级。";
            FixButton.Content = "已阻挠更新";
        }
        else
        {
            LauncherStateText.Text = $"PCL {_pcl.VersionText}：尚未阻挠更新，启动器可能会自动升级。";
            FixButton.Content = "阻挠更新";
            FixButton.IsEnabled = true;
        }

        if (_gameDir == null)
        {
            GameDirText.Text = "尚未选择";
            GameDirSource.Text = "没有找到 PCL 或 HMCL，请在下方手动指定游戏目录";
        }
        else
        {
            GameDirText.Text = _gameDir;
            GameDirSource.Text = $"来源：{_gameDirSource}";
        }
    }

    private void RefreshInstalled()
    {
        _installed.Clear();
        if (_gameDir == null) return;
        var root = Path.Combine(_gameDir, "versions");
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
            var isFabric = option.Kind == LoaderKind.Fabric;
            FabricApiCheck.Visibility = isFabric ? Visibility.Visible : Visibility.Collapsed;
            if (!isFabric) FabricApiVersionBox.Visibility = Visibility.Collapsed;
            StartButton.IsEnabled = !needsVersion;
            if (!needsVersion) return;

            await LoadLoaderVersionsAsync(option).ConfigureAwait(true);
            if (isFabric && FabricApiCheck.IsChecked == true) await LoadFabricApiVersionsAsync().ConfigureAwait(true);
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

    private async Task LoadFabricApiVersionsAsync()
    {
        FabricApiVersionBox.ItemsSource = new[] { "正在获取版本…" };
        FabricApiVersionBox.SelectedIndex = 0;
        FabricApiVersionBox.IsEnabled = false;
        FabricApiVersionBox.Visibility = Visibility.Visible;
        if (_selected == null) return;
        var versionId = _selected.Id;
        List<FabricApiVersion> list;
        try
        {
            list = await LoaderCatalog.FabricApiVersionsAsync(_http, versionId, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            list = new List<FabricApiVersion>();
            SetStatus(ex.Message);
        }
        if (_loader?.Kind != LoaderKind.Fabric || _selected?.Id != versionId) return;
        _fabricApiVersions = list;
        FabricApiVersionBox.ItemsSource = list.Count > 0 ? list : new[] { "没有可用版本" };
        FabricApiVersionBox.IsEnabled = list.Count > 0;
        FabricApiVersionBox.SelectedIndex = list.Count > 0 ? 0 : -1;
        if (list.Count == 0) SetStatus("没有找到与该版本匹配的 Fabric API");
    }

    private async void FabricApiCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // 初始化阶段 XAML 里的 IsChecked 也会触发一次
        try
        {
            if (FabricApiCheck.IsChecked == true)
            {
                FabricApiVersionBox.Visibility = Visibility.Visible;
                if (_fabricApiVersions.Count == 0) await LoadFabricApiVersionsAsync();
            }
            else
            {
                FabricApiVersionBox.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SetStatus(ex.Message);
        }
    }

    // ---------- 安装 ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null || _loader == null) return;
        if (string.IsNullOrEmpty(_gameDir))
        {
            NoticeDialog.Show(this, "还没有游戏目录",
                "没有找到 PCL 或 HMCL 启动器。请先在「设置」里选择游戏目录，再回来下载。", "知道了");
            return;
        }

        var options = new InstallOptions
        {
            Entry = _selected.Entry,
            Loader = _loader.Kind,
            LoaderVersion = LoaderVersionBox.SelectedItem as string,
            FabricApi = _loader.Kind == LoaderKind.Fabric && FabricApiCheck.IsChecked == true
                ? FabricApiVersionBox.SelectedItem as FabricApiVersion
                : null
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
            var installer = new Installer(_gameDir!, _http, progress,
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
        if (!string.IsNullOrEmpty(_gameDir)) OpenPath(_gameDir!);
    }

    private void BackToDownload_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstalled();
        RefreshList();
        ShowPage(PageDownload);
    }

    // ---------- 设置与导航 ----------

    private void ChooseGameDir_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 Minecraft 的游戏目录（通常是 .minecraft 文件夹）"
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        _config.GameDir = dialog.SelectedPath;
        _config.Save();
        SetGameDir(dialog.SelectedPath, "手动选择");
        SetStatus("游戏目录已更新");
    }

    private void OpenGameDir_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_gameDir)) OpenPath(_gameDir!);
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
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
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
