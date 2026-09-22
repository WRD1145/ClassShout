# 一键跑完整回归：局域网链路 + 中继链路。
#
# 中继服务器用一个全新的临时状态目录和环境变量启动，因此不会碰到
# 上一次测试留在程序目录里的 relay-*.json，也不会污染真实部署。
#
#   pwsh -File scripts/regress.ps1
#   pwsh -File scripts/regress.ps1 -Port 8098
#
# 退出码 0 表示两个链路全部通过；非 0 表示有失败项（详情看输出）。
[CmdletBinding()]
param(
    [int]$Port = 8097,
    [string]$Configuration = 'Release',
    [switch]$Tts
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$e2e = Join-Path $root "tools\ClassShout.EndToEnd\bin\$Configuration\net10.0-windows\ClassShout.EndToEnd.exe"
$srv = Join-Path $root "src\ClassShout.RelayServer\bin\$Configuration\net10.0\ClassShout.RelayServer.exe"

foreach ($path in @($e2e, $srv)) {
    if (-not (Test-Path $path)) {
        throw "找不到 $path，请先执行：dotnet build ClassShout.slnx -c $Configuration"
    }
}

# 先构建再测。
#
# 这一步不是可有可无的礼节：中继服务器是独立进程，如果忘了重建，上面那两个
# 可执行文件还是上一次的，于是"全部通过"测的是旧代码 —— 而你会以为新改动没问题。
# 这个坑真的踩过一次，所以让脚本自己保证被测二进制是最新的。
Write-Host "构建中（$Configuration）…" -ForegroundColor Yellow
& dotnet build (Join-Path $root 'ClassShout.DesktopOnly.slnf') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) {
    throw "构建失败（退出码 $LASTEXITCODE），先修好再跑回归。"
}

$state = Join-Path ([System.IO.Path]::GetTempPath()) ('cs-regress-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $state -Force | Out-Null
Write-Host "状态目录：$state" -ForegroundColor Cyan

$env:CLASSSHOUT_CONFIG = Join-Path $state 'relay-config.json'
$env:CLASSSHOUT_USER_STATE = Join-Path $state 'relay-users.json'
$env:CLASSSHOUT_RELAY_STATE = Join-Path $state 'relay-state.json'
$env:CLASSSHOUT_BINDING_STATE = Join-Path $state 'relay-bindings.json'

$log = Join-Path $state 'server.log'
$proc = Start-Process -FilePath $srv -ArgumentList '--urls', "http://127.0.0.1:$Port" `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 400
        if ($proc.HasExited) { break }
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 2
            if ($health.ok) { $ready = $true; break }
        }
        catch { }
    }

    if (-not $ready) {
        Write-Host '中继服务器未能就绪，日志末尾：' -ForegroundColor Red
        Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 40
        exit 1
    }

    # 口令必须从状态目录读，而不是从某个固定的本地路径 ——
    # 服务器的工作目录由上面的环境变量决定，真实部署里它可能在完全不同的机器上。
    $adminPassword = (Get-Content $env:CLASSSHOUT_CONFIG -Raw | ConvertFrom-Json).AdminPassword
    Write-Host "中继服务器就绪（管理员口令已从配置文件读出，$($adminPassword.Length) 位）" -ForegroundColor Cyan

    Write-Host ''
    Write-Host '================ 局域网链路 ================' -ForegroundColor Yellow
    $lanArgs = @()
    if ($Tts) { $lanArgs += '--tts' }
    & $e2e @lanArgs
    $lanExit = $LASTEXITCODE

    Write-Host ''
    Write-Host '================ 中继链路 ================' -ForegroundColor Yellow
    & $e2e --relay "http://127.0.0.1:$Port" --admin-password $adminPassword
    $relayExit = $LASTEXITCODE
}
finally {
    if (-not $proc.HasExited) {
        $proc.Kill()
        $proc.WaitForExit(5000) | Out-Null
    }

    Remove-Item -Recurse -Force $state -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '================ 教室端托盘驻留 ================' -ForegroundColor Yellow
& (Join-Path $PSScriptRoot 'smoke-tray.ps1') -ErrorAction Continue
$trayExit = $LASTEXITCODE

Write-Host ''
Write-Host '================ 控制台前端转义 ================' -ForegroundColor Yellow

# 用 Node 把真正的 app.js 跑起来断言转义行为，所以它守的是浏览器会执行的那份代码，
# 而不是一份照抄过来的副本。没有 Node 就跳过 —— 这是唯一一个非 .NET 的测试。
$webuiExit = 0
if (Get-Command node -ErrorAction SilentlyContinue) {
    & node (Join-Path $PSScriptRoot 'smoke-webui.mjs')
    $webuiExit = $LASTEXITCODE
}
else {
    Write-Host '  跳过 —— 未找到 node' -ForegroundColor Yellow
}

Write-Host ''
Write-Host "局域网退出码：$lanExit    中继退出码：$relayExit    托盘退出码：$trayExit    前端退出码：$webuiExit" -ForegroundColor Cyan

if ($lanExit -ne 0 -or $relayExit -ne 0 -or $trayExit -ne 0 -or $webuiExit -ne 0) {
    Write-Host '存在失败项。' -ForegroundColor Red
    exit 1
}

Write-Host '全部通过。' -ForegroundColor Green
