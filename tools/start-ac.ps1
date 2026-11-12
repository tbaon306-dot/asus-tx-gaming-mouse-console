# 恢复奥创中心（需要管理员）
# 只是把服务启动回来；奥创界面需要你自己双击打开

$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot 'stop-ac.log'
"=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') 启动奥创 ===" | Add-Content -LiteralPath $log -Encoding UTF8

foreach ($s in 'ArmouryCrateService', 'ArmouryCrateControlInterface') {
    try {
        Start-Service $s -ErrorAction Stop
        "已启动 $s" | Add-Content -LiteralPath $log -Encoding UTF8
    } catch {
        "启动失败 $s : $($_.Exception.Message)" | Add-Content -LiteralPath $log -Encoding UTF8
    }
}

Start-Sleep -Seconds 1
Get-Service ArmouryCrateService, ArmouryCrateControlInterface |
    Select-Object Name, Status | Format-Table -AutoSize

Write-Host '服务已启动（奥创界面需自行双击打开）'