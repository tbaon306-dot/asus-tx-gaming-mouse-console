# 从本机已安装的奥创中心里取出 MouseSDK
#   用法: ./tools/fetch-sdk.ps1 [-Destination .\MouseSDK] [-Source <路径>]
param(
    [string]$Destination = "",
    [string]$Source = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $root 'MouseSDK' }

function Test-SdkDir([string]$p) {
    if (-not $p) { return $false }
    return (Test-Path (Join-Path $p 'ArmouryMsSDK.dll')) -and (Test-Path (Join-Path $p 'config.ini'))
}

if ($Source) {
    if (-not (Test-SdkDir $Source)) { throw "指定的目录不是有效的 SDK 目录：$Source" }
    $found = $Source
}
else {
    $candidates = @(
        'C:\Program Files (x86)\ASUS\ArmouryDevice\dll\MouseSDK',
        'C:\Program Files\ASUS\ArmouryDevice\dll\MouseSDK',
        (Join-Path $env:LOCALAPPDATA 'ASUS\ArmouryDevice\dll\MouseSDK')
    )

    $found = $candidates | Where-Object { Test-SdkDir $_ } | Select-Object -First 1

    if (-not $found) {
        # 兜底：在 ASUS 目录里搜
        foreach ($base in @('C:\Program Files (x86)\ASUS', 'C:\Program Files\ASUS')) {
            if (-not (Test-Path $base)) { continue }
            $dll = Get-ChildItem $base -Recurse -Filter 'ArmouryMsSDK.dll' -ErrorAction SilentlyContinue -Force |
                   Select-Object -First 1
            if ($dll) { $found = $dll.DirectoryName; break }
        }
    }
}

if (-not $found) {
    Write-Host '没找到 MouseSDK。' -ForegroundColor Yellow
    Write-Host '这个目录来自奥创中心的 ArmouryDevice 组件，通常在：'
    Write-Host '  C:\Program Files (x86)\ASUS\ArmouryDevice\dll\MouseSDK'
    Write-Host '如果这台机器没装过奥创，请先安装一次（装完可以把服务停掉）。'
    exit 1
}

Write-Host "来源: $found"

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item (Join-Path $found '*') $Destination -Recurse -Force

$count = (Get-ChildItem $Destination -Recurse -File).Count
$mb = [math]::Round(((Get-ChildItem $Destination -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "完成: $Destination  ($count 个文件, $mb MB)" -ForegroundColor Green
Write-Host ''
Write-Host '提醒：这些是华硕的专有文件，仅供本机运行使用，不要提交到仓库或公开发布。' -ForegroundColor Yellow