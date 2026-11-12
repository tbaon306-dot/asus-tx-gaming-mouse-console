# 停掉奥创中心（需要管理员：右键 → 以管理员身份运行）
# 目的：让「保存到板载」真正落盘 —— asus_framework 在跑时写板载必然"返回成功但无效"
#
# 说明：ArmouryCrateService 是 SYSTEM 服务，普通用户杀不掉它的子进程，
#       必须先停服务，再清理残留进程。

$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot 'stop-ac.log'
"=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') 停止奥创 ===" | Set-Content -LiteralPath $log -Encoding UTF8

foreach ($s in 'ArmouryCrateService', 'ArmouryCrateControlInterface') {
    try {
        Stop-Service $s -Force -ErrorAction Stop
        "已停止服务 $s" | Add-Content -LiteralPath $log -Encoding UTF8
    } catch {
        "停服务失败 $s : $($_.Exception.Message)" | Add-Content -LiteralPath $log -Encoding UTF8
    }
}

Start-Sleep -Seconds 2

$names = 'asus_framework', 'ArmouryCrate', 'ArmouryCrate.Service', 'ArmouryCrate.UserSessionHelper',
         'ArmouryCrateControlInterface', 'ArmouryHtmlDebugServer', 'ArmourySocketServer', 'ArmourySwAgent'
$killed = 0
foreach ($n in $names) {
    Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $_.Kill()
            "已结束 $n $($_.Id)" | Add-Content -LiteralPath $log -Encoding UTF8
            $killed++
        } catch {
            "结束失败 $n $($_.Id): $($_.Exception.Message)" | Add-Content -LiteralPath $log -Encoding UTF8
        }
    }
}

Start-Sleep -Seconds 1
$left = Get-Process | Where-Object { $_.Name -match 'Armoury|asus_framework' }
if ($left) {
    "仍残留：$($left.Name -join ', ')" | Add-Content -LiteralPath $log -Encoding UTF8
} else {
    "已全部停止（结束进程 $killed 个）" | Add-Content -LiteralPath $log -Encoding UTF8
}
"done" | Add-Content -LiteralPath $log -Encoding UTF8

Write-Host '完成，详情见 stop-ac.log'
Write-Host ''
Get-Content -LiteralPath $log -Encoding UTF8 | Select-Object -Skip 1