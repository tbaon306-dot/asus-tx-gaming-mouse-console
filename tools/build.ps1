# 编译主程序
#   用法: ./tools/build.ps1 [-Output build/ASUSMouseConsole.exe]
param(
    [string]$Output = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'src\MouseCenter.cs'
$icon = Join-Path $root 'assets\app.ico'

if (-not $Output) { $Output = Join-Path $root 'build\ASUSMouseConsole.exe' }

# --- 找 csc.exe（.NET Framework 自带，无需 VS） ---
$cscCandidates = @()
$cscCandidates += Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$cscCandidates += Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\Framework') -Filter 'v4.*' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'csc.exe' }

$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw '找不到 csc.exe，需要 .NET Framework 4.x' }

New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null

$cscArgs = @(
    '/nologo', '/platform:x86', '/optimize+', '/target:winexe', '/codepage:65001',
    '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Core.dll',
    "/out:$Output"
)
if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }
$cscArgs += $src

Write-Host "csc:  $csc"
Write-Host "输出: $Output"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$size = [math]::Round((Get-Item $Output).Length / 1KB, 1)
Write-Host "完成: $size KB" -ForegroundColor Green