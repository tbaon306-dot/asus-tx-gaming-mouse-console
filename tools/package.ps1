# 打包成单文件安装程序
#   用法: ./tools/package.ps1 [-SdkDir .\MouseSDK] [-Output dist\ASUSMouseConsole-Setup.exe]
#
# 注意：打出来的安装包会把 MouseSDK 整个嵌进去。
#       那是华硕的专有文件，只适合自己用或发给朋友，不要公开发布。
param(
    [string]$SdkDir = "",
    [string]$Output = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $SdkDir) { $SdkDir = Join-Path $root 'MouseSDK' }
if (-not $Output) { $Output = Join-Path $root 'dist\ASUSMouseConsole-Setup.exe' }

if (-not (Test-Path (Join-Path $SdkDir 'ArmouryMsSDK.dll'))) {
    throw "找不到 SDK：$SdkDir。先运行 ./tools/fetch-sdk.ps1"
}

# ---------- 1. 编译主程序 ----------
$appExe = Join-Path $root 'build\ASUSMouseConsole.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -Output $appExe

# ---------- 2. 准备载荷 ----------
$stage = Join-Path $root 'build\stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item $appExe (Join-Path $stage 'ASUSMouseControl.exe') -Force
Copy-Item $SdkDir (Join-Path $stage 'MouseSDK') -Recurse -Force
foreach ($extra in @('README.md', 'docs\使用说明.txt')) {
    $p = Join-Path $root $extra
    if (Test-Path $p) { Copy-Item $p (Join-Path $stage (Split-Path -Leaf $extra)) -Force }
}
foreach ($extra in @('stop-ac.ps1', 'start-ac.ps1', 'stop-ac.cmd', 'start-ac.cmd')) {
    $p = Join-Path $root "tools\$extra"
    if (Test-Path $p) { Copy-Item $p $stage -Force }
}

# ---------- 3. 打包载荷（自定义容器: "MCX1" + Deflate，与 src/Setup.cs 对应） ----------
Add-Type -AssemblyName System.IO.Compression

$payload = Join-Path $root 'build\payload.bin'
$files = Get-ChildItem $stage -Recurse -File | Sort-Object FullName
$fs = [IO.File]::Create($payload)
$w = New-Object IO.BinaryWriter($fs)
try {
    $w.Write([Text.Encoding]::ASCII.GetBytes('MCX1'))
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $nameBytes = [Text.Encoding]::UTF8.GetBytes($rel)
        $raw = [IO.File]::ReadAllBytes($f.FullName)

        $ms = New-Object IO.MemoryStream
        $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Compress)
        $ds.Write($raw, 0, $raw.Length)
        $ds.Close()
        $comp = $ms.ToArray()
        $ms.Dispose()

        $w.Write([int]$nameBytes.Length); $w.Write($nameBytes)
        $w.Write([int]$raw.Length); $w.Write([int]$comp.Length); $w.Write($comp)
    }
    $w.Write([int]0)
}
finally { $w.Close(); $fs.Dispose() }

$rawMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 2)
$payMb = [math]::Round(((Get-Item $payload).Length / 1MB), 2)
Write-Host "载荷: $($files.Count) 个文件, $rawMb MB -> $payMb MB"

# ---------- 4. 编译安装程序 ----------
$cscCandidates = @()
$cscCandidates += Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$cscCandidates += Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\Framework') -Filter 'v4.*' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'csc.exe' }
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw '找不到 csc.exe' }

New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null

$icon = Join-Path $root 'assets\app.ico'
$cscArgs = @(
    '/nologo', '/platform:anycpu', '/optimize+', '/target:winexe', '/codepage:65001',
    '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Core.dll',
    "/resource:$payload,payload.bin",
    "/out:$Output",
    (Join-Path $root 'src\Setup.cs'),
    (Join-Path $root 'src\SetupInfo.cs')
)
if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败（exit $LASTEXITCODE）" }

$sizeMb = [math]::Round(((Get-Item $Output).Length / 1MB), 2)
Write-Host "完成: $Output  ($sizeMb MB)" -ForegroundColor Green
Write-Host ''
Write-Host '未签名的 exe 在其他机器上会触发 SmartScreen 提示，属于正常现象。' -ForegroundColor Yellow