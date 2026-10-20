# 打包脚本：生成单文件可执行程序
param(
    [string]$Name = "PCL下载助手"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path "assets\icon.ico")) {
    python tools\make_icon.py
}

python -m PyInstaller `
    --noconfirm `
    --clean `
    --windowed `
    --onefile `
    --name $Name `
    --icon "assets\icon.ico" `
    --add-data "assets;assets" `
    --collect-data customtkinter `
    --exclude-module tkinter.test `
    run.py

if ($LASTEXITCODE -ne 0) { throw "打包失败" }

$target = Join-Path $PSScriptRoot "dist\$Name.exe"
Write-Host "已生成：$target"