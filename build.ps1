# 打包脚本：编译并输出单文件 exe
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dotnet = "C:\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

& $dotnet build "src\PCLDownloadHelper\PCLDownloadHelper.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

New-Item -ItemType Directory -Force dist | Out-Null
$source = "src\PCLDownloadHelper\bin\Release\net48\PCLDownloadHelper.exe"
Copy-Item $source "dist\PCL游戏下载助手.exe" -Force

$size = [math]::Round((Get-Item "dist\PCL游戏下载助手.exe").Length / 1KB)
Write-Host "已生成：dist\PCL游戏下载助手.exe（$size KB）"