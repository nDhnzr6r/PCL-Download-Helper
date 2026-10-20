# PCL 下载助手

PCL 2.99 正式版移除了 Minecraft 下载入口（更新日志：「为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能」）。
这个工具用来补回这部分能力，分两种情况：

- **启动器是 2.14 或更早的版本**：一键关闭启动器的自动更新，避免它被升级到 2.99 之后下载入口消失。
- **启动器已经是 2.99，或想直接装游戏**：内置下载器，把原版、Fabric、Quilt 版本直接安装到 PCL 的游戏目录，装完重新打开 PCL 就能在版本列表里看到。

![界面](docs/screenshot.png)

## 功能

| 模块 | 说明 |
| --- | --- |
| 启动器检测 | 自动在本机浅层目录中查找 `Plain Craft Launcher 2.exe`，读取文件版本，也可以手动选择文件夹 |
| 一键修复 | 向 `PCL\Setup.ini` 写入 `SystemSystemUpdate:3`（不存在则新增），关闭更新提示，阻止自动升级 |
| 版本下载 | 正式版、快照、全部历史版本，支持搜索，实时显示进度、速度与剩余量 |
| 加载器 | 安装原版后可直接追加 Fabric 或 Quilt，自动解析并下载对应依赖 |
| 下载源 | 默认优先国内镜像（BMCLAPI），镜像节点异常时自动回退官方源 |
| 完整性 | 按 SHA-1 与文件大小校验，已存在的文件自动跳过，只补缺失的部分 |

## 使用

1. 双击 `PCL下载助手.exe`。
2. 程序会自动找到 PCL；没有找到时点「选择文件夹」，选中放有 `Plain Craft Launcher 2.exe` 的目录。
3. 左侧会显示启动器版本与状态：
   - 显示「可修复」时，点「一键修复」，然后重启 PCL（修复前请先退出 PCL，否则设置会被覆盖）。
   - 显示「已修复」说明更新提示已关闭，下载入口不会因为升级而消失。
   - 显示「下载已关闭」表示当前是 2.99 及之后的版本，直接在右侧下载游戏即可。
4. 右侧选择版本、选择加载器，点「下载」。下载完成后重新打开 PCL，新版本会出现在版本选择列表里。

> 首次启动会从 Mojang 或镜像获取版本清单，需要联网。

## 运行环境

- Windows 10 / 11，64 位
- 游戏启动仍然由 PCL 负责，Java 由 PCL 自行匹配

## 从源码运行

```powershell
pip install -r requirements.txt
python -m app
```

## 打包

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物在 `dist\PCL下载助手.exe`（单文件，约 37 MB）。

## 目录结构

```
app/
  core/
    pcl.py         启动器定位、版本读取、Setup.ini 修复
    setupini.py    Setup.ini 读写（保留原有键序与换行）
    net.py         下载客户端：镜像回退、重试、卡死连接回收、SHA-1 校验
    mcmeta.py      版本元数据解析：规则、依赖库、资源索引
    installer.py   安装流程与原版 / 加载器下载
    loaders.py     Fabric / Quilt 解析
    sources.py     镜像地址改写
    config.py      本地配置
  ui/
    window.py      主窗口
    version_list.py 自绘版本列表与滚动条
    theme.py       配色与字体
  main.py          入口
tools/             图标生成、命令行安装、完整性校验
docs/              源码对比分析、界面截图
```

## 原理

PCL 2.99 在 `ModSecret.vb` 中新增了 `RestrictMcDownload = True`，并在下载页、整合包安装、版本修改处加了拦截，所以这个版本没有可用的配置开关能绕开限制。

PCL 的更新策略保存在 `PCL\Setup.ini` 的 `SystemSystemUpdate` 里：默认值 0 表示「在有新版本时自动下载」，3 表示「关闭更新提示」。早期的 PCL 下载功能本身完好，只是会被自动更新成 2.99。把它设为 3，启动器就停留在当前版本，下载入口继续可用。

详细的源码对比见 [docs/pcl-download-analysis.md](docs/pcl-download-analysis.md)。

## 说明

- 只使用 Mojang 官方接口与 BMCLAPI 公开镜像，不接触任何第三方账号或启动器配置之外的目录。
- 下载器只做「把文件放到位」，账号、Java、启动参数、模组管理仍由 PCL 处理。
- 下载的内容版权归 Mojang 与对应加载器作者所有，请使用正版账号游玩。

## 许可

MIT
