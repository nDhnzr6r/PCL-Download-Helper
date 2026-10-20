# 打包脚本：编译并输出单文件 exe
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 找一个带 SDK 的 dotnet：PATH 里的、常见的本地安装位置
$candidates = @()
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
if ($onPath) { $candidates += $onPath.Source }
$candidates += "C:\dotnet\dotnet.exe"

$dotnet = $null
foreach ($candidate in $candidates) {
    if (-not (Test-Path $candidate)) { continue }
    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    throw "没有找到带 SDK 的 .NET，请先安装：https://dotnet.microsoft.com/download"
}

& $dotnet build "src\PCLDownloadHelper\PCLDownloadHelper.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

New-Item -ItemType Directory -Force dist | Out-Null
$source = "src\PCLDownloadHelper\bin\Release\net48\PCLDownloadHelper.exe"
Copy-Item $source "dist\PCL游戏下载助手.exe" -Force

$file = Get-Item "dist\PCL游戏下载助手.exe"
Write-Host ("已生成：dist\PCL游戏下载助手.exe（{0:N0} 字节）" -f $file.Length)