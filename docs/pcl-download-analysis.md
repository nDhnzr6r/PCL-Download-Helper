# PCL 2.14 → 2.99 下载功能源码对比

对象：<https://github.com/Meloong-Git/PCL>，标签 `2.14.0.0` 与 `2.99.0.0`。

```
git clone --filter=blob:none https://github.com/Meloong-Git/PCL.git
git diff --stat 2.14.0.0 2.99.0.0
```

两个版本之间只有 10 个源文件变动，其中与下载相关的有 5 个。下面是逐处说明。

## 1. 总开关

`Plain Craft Launcher 2/Modules/ModSecret.vb`

```vb
Public Const RestrictMcDownload As Boolean = True
```

2.14 中没有这个常量，2.99 新增并写死为 `True`。它编译进程序集，没有对应的配置文件、环境变量或命令行参数，因此 **2.99 无法通过改设置恢复下载入口**。

## 2. 三处调用点

### 2.1 下载页

`Pages/PageDownload/PageDownloadInstall.xaml.vb`

```vb
Public Overridable Sub LoaderInit() Handles Me.Initialized
    ControlInit()
    If RestrictMcDownload Then
        PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, Nothing,
            New LoaderTask(Of String, DlClientListResult)("关闭下载",
                Sub() Throw New Exception("为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……")),
            AddressOf LoadMinecraft_OnFinish)
    Else
        PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, Nothing, DlClientListLoader, AddressOf LoadMinecraft_OnFinish)
    End If
End Sub
```

原来的 `DlClientListLoader`（拉取版本列表）被换成一个直接抛异常的加载任务，于是下载页只剩下这条红色提示。

### 2.2 整合包安装

`Modules/Minecraft/ModModpack.vb`

```vb
Public Sub ModpackInstall()
    If RestrictMcDownload Then
        Hint("为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……", HintType.Red)
        Return
    End If
    ...
```

安装流程内部（`StartInstall` 之前）也加了一次同样的判断，避免从其他入口绕过。

### 2.3 版本修改

`Pages/PageDownload/PageDownloadModify.vb`

```vb
CardMinecraft.Visibility = If(RestrictMcDownload, Visibility.Collapsed, Visibility.Visible)
```

「修改版本」页里用于增删改 Minecraft 版本本体的卡片被隐藏，只保留模组、资源包等组件管理。

另有 `Pages/Resource/PageDownloadCompDetail.xaml.vb`：

```vb
If Not RestrictMcDownload AndAlso TargetResourceType = ResourceTypes.ModPack Then
```

资源详情页的「安装整合包」按钮同样被收起。

## 3. 版本号变化

`Modules/Base/ModBase.vb`

| 常量 | 2.14.0.0 | 2.99.0.0 |
| --- | --- | --- |
| `VersionBaseName` | `2.14.0.0` | `2.99.0.0` |
| `VersionCode`（正式版） | `411` | `413` |
| `VersionCode`（快照） | `412` | `414` |

## 4. 2.14 里仍然完整的下载链路

主流程在 `Pages/PageDownload/ModDownloadLib.vb` 的 `McDownloadClientLoader`：

```vb
Private Function McDownloadClientLoader(Id As String, JsonUrl As String, VersionFolder As String) As List(Of LoaderBase)
    ...
    ' 1. 取版本 json 地址
    Dim JsonAddress As String = DlClientListGet(Id)
    ' 2. 下载 versions\<id>\<id>.json
    ' 3. 读取 json，构造 McInstance
    ' 4. 分析并下载依赖库：McLibNetFilesFromInstance(NewInstance)
    ' 5. 下载资源索引，再按索引补齐资源：McAssetsFixList(NewInstance, True, Task)
```

各步骤对应的实现：

| 步骤 | 位置 | 说明 |
| --- | --- | --- |
| 版本清单 | `ModDownload.vb` `DlClientListGet` | `https://launchermeta.mojang.com/mc/game/version_manifest.json`，镜像为 `https://bmclapi2.bangbang93.com/mc/game/version_manifest.json` |
| 客户端 jar | 版本 json 的 `downloads.client` | 写入 `versions\<id>\<id>.jar` |
| 依赖库 | `McLibNetFilesFromInstance` | 按 `rules` 过滤系统，处理 `downloads.artifact` 与 `natives` 分类器，落到 `libraries\<path>` |
| 资源索引 | 版本 json 的 `assetIndex` | 写入 `assets\indexes\<id>.json` |
| 资源文件 | `ModMinecraft.vb` `McAssetsListGet` / `McAssetsFixList` | 落到 `assets\objects\<hash 前两位>\<hash>` |
| 虚拟资源 | 同上 | 索引带 `virtual` 时映射到 `assets\virtual\legacy\<逻辑路径>`；带 `map_to_resources` 时映射到版本目录下的 `resources\<逻辑路径>` |

PCL 在启动时还会用同样的 `McAssetsFixList` 再查一次资源，缺什么补什么，所以资源目录被清掉也能自愈。

### 镜像改写

`ModDownload.vb` 中三组函数决定了每个文件的可选来源：

```vb
' 资源文件
https://resources.download.minecraft.net  → https://bmclapi2.bangbang93.com/assets
' 依赖库
https://libraries.minecraft.net           → https://bmclapi2.bangbang93.com/maven（或 /libraries）
' 版本 json、jar
https://piston-meta.mojang.com            → https://bmclapi2.bangbang93.com
https://piston-data.mojang.com            → https://bmclapi2.bangbang93.com
```

本程序的 `src/PCLDownloadHelper/Core/Net.cs` 复刻了这套规则，并按「镜像优先、失败回退官方」的顺序取流。

## 5. 更新设置与「阻挠更新」的关系

`Pages/PageSetup/Settings.vb`

```vb
New Setting("SystemSystemUpdate", 0)
```

`Pages/PageSetup/PageSetupSystem.xaml` 中该下拉框有四项：

```
在有新版本时自动下载            → 0（默认）
在有新版本时显示提示            → 1
仅在有重大漏洞更新时显示提示    → 2
关闭更新提示                    → 3
```

默认值 0 意味着旧版启动器只要联网就会把自身更新到 2.99，随后下载入口按上文逻辑被关闭。
把 `PCL\Setup.ini` 里的 `SystemSystemUpdate` 改成 `3` 后，启动器不再检查更新，停留在当前版本，下载功能保持不变。

这就是「阻挠更新」的完整链路：**不是给 2.99 打补丁，而是让旧版本不再升级到 2.99。**

## 6. 设置文件的写法

`Modules/Base/ModBase.vb`：

```vb
If Not FileName.Contains(":\") Then FileName = $"{Paths.Base}PCL\{FileName}.ini"
```

即设置文件固定在 `启动器目录\PCL\Setup.ini`，格式为 `键:值`，UTF-8 编码。程序按行读写，保留原有键顺序与换行风格，只改动目标键，不存在时追加一行。

## 7. 结论

1. 2.99 的下载入口由编译期常量关闭，配置层面没有开关。
2. 旧版本的下载实现完整，唯一的问题是默认会自动升级到 2.99。
3. `SystemSystemUpdate=3` 切断自动升级，是旧版本最直接的阻挠更新方式。
4. 对已经升到 2.99 的用户，只能把游戏文件直接装进 `.minecraft`，由启动器照常识别和启动——这正是本工具下载器做的事。
