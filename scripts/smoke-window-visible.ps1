# 「窗口式喊话要不要把窗口唤出来」的运行时验证。
#
#   pwsh -File scripts/smoke-window-visible.ps1
#   pwsh -File scripts/smoke-window-visible.ps1 -Exe dist\windows\classroom\ClassShout.Classroom.exe
#
# 思路（和 smoke-tray.ps1 同一套手法：只认真实进程的行为，不看代码）：
#   1. 起教室端，等主窗口出现；
#   2. 给主窗口发 WM_CLOSE —— 等价于点右上角的 ×，窗口缩进托盘、进程继续接收；
#   3. 从这脚本自己发一条**窗口式**喊话（display=window）过去；
#   4. 看主窗口句柄有没有重新出现。
#
# 为什么值得单独立一个冒烟：窗口模式下内容是画在主窗口大字区里的。窗口藏在托盘时，
# 喊话照样"显示成功"、老师那边收到的是成功回执，而教室里一片安静 —— 这比彻底失败更糟，
# 因为它看起来是好的。这个脚本就是钉住"别再变成那样"。
#
# 退出码 0 表示通过。
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$StartupTimeoutSeconds = 30,
    [int]$PopBackTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

if (-not $Exe) {
    $root = Split-Path -Parent $PSScriptRoot

    # 取**最新**的那个，而不是"dist 优先"。
    #
    # 踩过：dist\windows\classroom 里躺着一份几天前打的包，脚本优先用了它，
    # 于是刚改的功能在冒烟里"没生效" —— 测试没错，测的是旧二进制。
    # 这类假失败比真失败更费时间，所以这里按修改时间挑，并把挑中的路径打出来。
    $candidates = @(
        (Join-Path $root 'dist\windows\classroom\ClassShout.Classroom.exe'),
        (Join-Path $root 'src\ClassShout.Classroom\bin\Release\net10.0-windows\ClassShout.Classroom.exe')
    ) | Where-Object { Test-Path $_ } |
        ForEach-Object { Get-Item $_ } |
        Sort-Object LastWriteTime -Descending

    $Exe = $candidates | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Exe -or -not (Test-Path $Exe)) {
    throw "找不到教室端可执行文件。请先打包（pwsh -File scripts/pack.ps1）或构建（dotnet build ClassShout.DesktopOnly.slnf -c Release），或用 -Exe 指定路径。"
}

$Exe = (Resolve-Path $Exe).Path
Write-Host "被测程序：$Exe" -ForegroundColor Cyan

# 单实例互斥体是进程级的：上次没退干净的实例会让本次启动只弹一个"已在运行"的提示窗，
# 于是测出来的结论全是假的。每次都从干净状态出发。
Get-Process -Name 'ClassShout.Classroom' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

Add-Type -Namespace ClassShoutSmoke -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@

$WM_CLOSE = 0x0010

# ---------- 发一条窗口式喊话 ----------

function Write-ShoutFrame {
    param(
        [System.IO.Stream]$Stream,
        [byte]$Kind,
        [byte[]]$Payload
    )

    # 分帧：[4 字节大端长度][1 字节类型][负载]，长度含类型字节
    $length = $Payload.Length + 1

    # 先算到变量里：`@(($x -shr 24) -band 0xFF, ...)` 这种写法里逗号的优先级会把
    # 后面几项并进 -band 的右操作数，结果是 Object[] 上不存在的 op_BitwiseAnd。
    $b0 = ($length -shr 24) -band 0xFF
    $b1 = ($length -shr 16) -band 0xFF
    $b2 = ($length -shr 8) -band 0xFF
    $b3 = $length -band 0xFF
    $header = [byte[]]@($b0, $b1, $b2, $b3, $Kind)

    $Stream.Write($header, 0, $header.Length)
    $Stream.Write($Payload, 0, $Payload.Length)
    $Stream.Flush()
}

function Send-WindowShout {
    param([int]$Port = 45900, [string]$Text = '窗口唤出测试')

    $client = [System.Net.Sockets.TcpClient]::new()
    $client.Connect('127.0.0.1', $Port)
    $stream = $client.GetStream()

    try {
        $utf8 = [System.Text.UTF8Encoding]::new($false)

        # 先打招呼：教室端按 hello 建会话，之后才认喊话
        $hello = @{
            type            = 'hello'
            clientId        = 'smoke-window'
            clientName      = '窗口唤出冒烟'
            protocolVersion = 1
        } | ConvertTo-Json -Compress

        Write-ShoutFrame -Stream $stream -Kind 1 -Payload $utf8.GetBytes($hello)
        Start-Sleep -Milliseconds 300

        # display=window：走"占满大字区"那条路（也就是画在主窗口里的那条）
        $shout = @{
            type      = 'textShout'
            id        = (Get-Date -Format 'yyyyMMddHHmmssfff')
            text      = $Text
            rate      = 0
            volume    = 100
            interrupt = $true
            display   = 'window'
            holdMs    = 5000
            speak     = $false
        } | ConvertTo-Json -Compress

        Write-ShoutFrame -Stream $stream -Kind 1 -Payload $utf8.GetBytes($shout)
        Start-Sleep -Milliseconds 300
    }
    finally {
        $stream.Dispose()
        $client.Dispose()
    }
}

# ---------- 主流程 ----------

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

    Write-Host "主窗口已出现（PID $($proc.Id)），发送 WM_CLOSE 缩进托盘…"
    [ClassShoutSmoke.Win32]::PostMessage($handle, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 3

    $proc.Refresh()
    $hiddenAfterClose = $proc.MainWindowHandle -eq [IntPtr]::Zero

    if (-not $hiddenAfterClose) {
        Write-Host '前置条件不成立：窗口没有缩进托盘（可能托盘不可用），这个用例不适用。' -ForegroundColor Yellow
        exit 1
    }

    Write-Host '窗口已隐藏，发一条窗口式喊话…'
    Send-WindowShout

    $popped = $false
    $deadline = (Get-Date).AddSeconds($PopBackTimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()

        if ($proc.HasExited) { break }

        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            $popped = $true
            break
        }
    }
}
finally {
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        $proc.WaitForExit(5000) | Out-Null
    }
}

Write-Host ''
Write-Host "窗口唤回：$(if ($popped) { '是' } else { '否' })"
Write-Host ''

if ($popped) {
    Write-Host '结果：通过 —— 窗口关闭状态下收到窗口式喊话，窗口被弹出。' -ForegroundColor Green
    exit 0
}

Write-Host '结果：失败 —— 窗口关了之后，窗口式喊话没有把窗口唤回来。' -ForegroundColor Red
exit 1
