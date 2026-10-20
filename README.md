# PCL修复下载

**PCL = Play Minecraft Lever**

把 Minecraft 直接装进启动器的游戏目录，并在需要时关掉启动器的自动更新，保住下载入口。

> **本程序与游戏启动器 PCL（Plain Craft Launcher）没有任何关系。**
> 它不是 PCL 的官方组件、分支、插件或附属工具，与 PCL 作者也没有隶属、授权或合作关系。名称里的 PCL 指 **Play Minecraft Lever**。程序只会按你的选择读写本机启动器的配置文件与游戏目录。

![界面](docs/screenshot.png)

![加载器](docs/screenshot-loader.png)

## 功能

| 模块 | 说明 |
| --- | --- |
| 版本下载 | 正式版、快照、远古版本，支持搜索，实时进度、速度与取消 |
| 加载器 | Forge、NeoForge、Fabric、Quilt；Fabric 可勾选同时安装 Fabric API |
| 下载源 | 默认优先 BMCLAPI 国内镜像，节点异常自动切回官方源 |
| 完整性 | SHA-1 与文件大小校验，已存在的文件跳过，只补缺失部分 |
| 下载修复 | 可修复的启动器一键把更新提示设为关闭，避免自动升级后失去下载入口 |
| 界面 | 与 PCL 同一套视觉语言：蓝色标题栏、浅蓝背景、白色圆角卡片 |

## 使用

1. 运行 `PCL修复下载.exe`，不需要安装。
2. 首次启动会自动查找 PCL 启动器，也可以点左下角「设置」里的「选择文件夹」手动指定。
3. 启动器状态可修复时会弹一次提示：可以选「启用修复」，也可以选「暂不修复」，选择后就再也不会打扰你。
4. 在「下载游戏」里选一个版本，进入加载器页面，选好加载器（和版本）后点「开始下载」。
5. 装完重新打开启动器，新版本就在版本选择列表里。

## 关于「修复下载」

PCL 2.99 正式版移除了游戏下载入口。旧版本本身下载功能完好，只是默认会在有新版本时自动升级。
修复做的事只有一件：把 `PCL\Setup.ini` 里的 `SystemSystemUpdate` 写成 `3`（关闭更新提示），让启动器停在当前版本。

对已经是 2.99 的启动器没有可用的配置开关，这种情况直接用本程序下载游戏即可。

源码层面的对比见 [docs/pcl-download-analysis.md](docs/pcl-download-analysis.md)。

## 技术栈

- C# + WPF，目标框架 .NET Framework 4.8（Windows 10/11 自带，无需安装运行时）
- 与 PCL 使用的技术栈一致，后续维护可以对照 PCL 的 C# 源码
- 产物为单个 exe（约 900 KB），依赖库已内嵌

## 从源码构建

```powershell
dotnet build src\PCLDownloadHelper\PCLDownloadHelper.csproj -c Release
# 或
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物在 `dist\PCL修复下载.exe`。

## 目录结构

```
src/PCLDownloadHelper/
  Core/
    PclInstall.cs     启动器定位、版本识别、Setup.ini 修复
    SetupIni.cs       Setup.ini 读写（保留键序与换行）
    Net.cs            下载客户端：镜像回退、重试、卡死回收、SHA-1 校验
    McMeta.cs         版本元数据：规则、依赖库、资源索引
    Installer.cs      安装流程与原版 / 加载器安装
    LoaderCatalog.cs  各加载器的版本列表与安装包地址
    JavaRuntime.cs    查找本机 Java，供 Forge / NeoForge 安装程序使用
    AppConfig.cs      本地配置
  UI/
    MainWindow.xaml   主窗口（版本列表、加载器选择、进度、设置）
    NoticeDialog.xaml 提示框
    Models.cs         列表项与图标模板选择器
  App.xaml           全局样式与配色
assets/              图标
docs/                截图与源码分析
```

## 已知限制

- OptiFine、LiteLoader 暂未支持：它们的安装程序只能用图形界面，需要额外做无界面注入，后续再补。
- 加载器安装依赖本机已安装的 Java（Forge/NeoForge）；Fabric、Quilt 只需要下载文件，不需要 Java。
- 只负责把文件放到位，账号登录、Java 版本选择、启动参数仍由启动器负责。

## 许可

MIT