# 单实例约束的运行时验证。
#
#   pwsh -File scripts/smoke-single-instance.ps1
#   pwsh -File scripts/smoke-single-instance.ps1 -Exe dist\windows\ClassShout.Classroom.exe
#
# 靠窗口标题判定，而不是靠"第二个进程有没有退出" ——
# 第二个实例是弹一个提示窗、等用户关掉再退出，所以它**应该**是活着的。
# 要验证的是两件事：
#   · 第一个实例的主窗口还在，并且只有一个进程持有它；
#   · 第二个进程的窗口是"已在运行"那个提示，而不是又一个主界面。
#
# 退出码 0 表示通过。
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$StartupTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

if (-not $Exe) {
    $root = Split-Path -Parent $PSScriptRoot
    $Exe = @(
        (Join-Path $root 'dist\windows\ClassShout.Classroom.exe'),
        (Join-Path $root 'src\ClassShout.Classroom\bin\Release\net10.0-windows\ClassShout.Classroom.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Exe -or -not (Test-Path $Exe)) {
    throw "找不到教室端可执行文件。请先打包或构建，或用 -Exe 指定路径。"
}

$Exe = (Resolve-Path $Exe).Path
$mainTitle = 'ClassShout 教室端'
Write-Host "被测程序：$Exe" -ForegroundColor Cyan

function Wait-ForTitle([System.Diagnostics.Process]$proc, [string]$title, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()
        if ($proc.HasExited) { return $false }
        if ($proc.MainWindowTitle -eq $title) { return $true }
    }
    return $false
}

$first = $null
$second = $null

try {
    $first = Start-Process -FilePath $Exe -PassThru
    if (-not (Wait-ForTitle $first $mainTitle $StartupTimeoutSeconds)) {
        Write-Host "第一个实例没有出现主窗口（标题应为「$mainTitle」）" -ForegroundColor Red
        exit 1
    }

    Write-Host "第一个实例已就绪（PID $($first.Id)）"

    $second = Start-Process -FilePath $Exe -PassThru
    if (-not (Wait-ForTitle $second "$mainTitle 已在运行" $StartupTimeoutSeconds)) {
        $second.Refresh()
        Write-Host "第二个实例没有弹出「已在运行」提示（实际标题：$($second.MainWindowTitle)）" -ForegroundColor Red
        exit 1
    }

    Write-Host "第二个实例弹出了提示窗（PID $($second.Id)），标题：$($second.MainWindowTitle)"

    Start-Sleep -Seconds 1

    # 主窗口只能有一个持有者
    $holders = Get-Process -Name 'ClassShout.Classroom' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -eq $mainTitle }

    Write-Host ''
    Write-Host "持有主界面「$mainTitle」的进程数：$($holders.Count)"
    Write-Host "第二个进程是否仍活着（等用户关提示）：$(-not $second.HasExited)"

    $ok = ($holders.Count -eq 1) -and ($holders[0].Id -eq $first.Id) -and (-not $second.HasExited)
}
finally {
    foreach ($proc in @($second, $first)) {
        if ($null -ne $proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force
            $proc.WaitForExit(5000) | Out-Null
        }
    }
}

Write-Host ''
if ($ok) {
    Write-Host '结果：通过 —— 第二个实例只提示、没有抢占端口或再开一个主界面。' -ForegroundColor Green
    exit 0
}

Write-Host '结果：失败。' -ForegroundColor Red
exit 1