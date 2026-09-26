# 托盘驻留的运行时验证。
#
#   pwsh -File scripts/smoke-tray.ps1
#   pwsh -File scripts/smoke-tray.ps1 -Exe dist\windows\classroom\ClassShout.Classroom.exe
#
# 思路：给主窗口发 WM_CLOSE（等价于用户点右上角的 ×），然后看进程是否还活着。
#   · 进程存活 + 主窗口句柄消失  ->  关闭被拦下、窗口被藏进托盘，功能成立
#   · 进程退出                   ->  拦截没生效
#
# 退出码 0 表示通过。
#
# 这个断言同时覆盖了托盘图标资源路径是否正确：图标是作为 Avalonia 资源嵌入的，
# 路径写错会让 TrayPresence.TryInstall 抛异常并返回 null，程序退回"关闭即退出"，
# 于是 WM_CLOSE 之后进程就没了 —— 断言随之失败。
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$StartupTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

if (-not $Exe) {
    $root = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $root 'dist\windows\classroom\ClassShout.Classroom.exe'),
        (Join-Path $root 'src\ClassShout.Classroom\bin\Release\net10.0-windows\ClassShout.Classroom.exe')
    )
    $Exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Exe -or -not (Test-Path $Exe)) {
    throw "找不到教室端可执行文件。请先打包（pwsh -File scripts/pack.ps1）或构建（dotnet build ClassShout.DesktopOnly.slnf -c Release），或用 -Exe 指定路径。"
}

$Exe = (Resolve-Path $Exe).Path
Write-Host "被测程序：$Exe" -ForegroundColor Cyan

# 开跑前先清掉可能残留的教室端进程。
#
# 这不是洁癖：单实例互斥体是**进程级**的，一个上次没退干净的实例会让本次启动
# 直接变成"第二个实例"——只弹一个"已在运行"的提示窗。于是这个测试看到的会是
# "关了窗口进程就退了"（因为它关掉的是那个提示窗，关掉即退出），
# 报成"没有缩到托盘"，而其实什么都没坏。
# 每个阶段都从干净状态出发，才不会把上一阶段的残留当成这一阶段的结论。
Get-Process -Name 'ClassShout.Classroom' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

Add-Type -Namespace ClassShout -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@

$WM_CLOSE = 0x0010

$proc = Start-Process -FilePath $Exe -PassThru
$handle = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)

try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()
        if ($proc.HasExited) { break }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            $handle = $proc.MainWindowHandle
            break
        }
    }

    if ($proc.HasExited) {
        Write-Host "启动失败：进程在窗口出现前就退出了（退出码 $($proc.ExitCode)）" -ForegroundColor Red
        exit 1
    }

    if ($handle -eq [IntPtr]::Zero) {
        Write-Host "启动失败：$StartupTimeoutSeconds 秒内没有出现主窗口。" -ForegroundColor Red
        Write-Host '（若是无人登录的会话或纯服务器环境，这个测试不适用。）'
        exit 1
    }

    Write-Host "主窗口已出现（PID $($proc.Id)），发送 WM_CLOSE —— 等价于点击关闭按钮…"
    [ClassShout.Win32]::PostMessage($handle, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null

    Start-Sleep -Seconds 3
    $proc.Refresh()

    $alive = -not $proc.HasExited
    $windowHidden = $proc.MainWindowHandle -eq [IntPtr]::Zero
}
finally {
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        $proc.WaitForExit(5000) | Out-Null
    }
}

Write-Host ''
Write-Host "进程仍存活  ：$(if ($alive) { '是' } else { "否（退出码 $($proc.ExitCode)）" })"
Write-Host "主窗口已隐藏：$(if ($windowHidden) { '是' } else { '否' })"
Write-Host ''

if ($alive -and $windowHidden) {
    Write-Host '结果：通过 —— 关闭窗口已最小化到托盘，进程继续接收喊话。' -ForegroundColor Green
    exit 0
}

Write-Host '结果：失败 —— 关闭窗口没有缩到托盘。' -ForegroundColor Red
exit 1
