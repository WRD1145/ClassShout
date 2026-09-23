<#
.SYNOPSIS
    打包 ClassShout 的可分发产物：Windows 单文件 exe 与 Android APK。

.DESCRIPTION
    默认产出：
      dist\windows\ClassShout.Classroom.exe      教室端（自包含，目标机无需装 .NET）
      dist\windows\ClassShout.Teacher.Desktop.exe 教师端桌面头（同上）
      dist\android\classshout-teacher-<版本>-universal.apk   含 arm64 + x64

    另外生成 dist\SHA256SUMS.txt 便于校验分发完整性。

.PARAMETER FrameworkDependent
    改为依赖框架发布，体积从约 60 MB 降到约 15 MB，但目标机必须已安装 .NET 10 运行时。
    教室电脑往往不具备这个条件，所以默认是自包含。

.PARAMETER SplitApk
    额外为 arm64 和 x64 各出一个 APK。单 ABI 的包约 32 MB（通用包约 63 MB），
    适合只面向真机或只在模拟器上跑的场合。

.PARAMETER SkipAndroid
    跳过 Android 打包（本机未配置 Android SDK 时用）。

.PARAMETER IncludeLinuxServer
    额外为 linux-x64 发布一份中继服务器。服务器常部署在 Linux 上，
    而开发机通常是 Windows，单独出一个 Linux 包比事后手动发布省事。

.EXAMPLE
    .\scripts\pack.ps1
    .\scripts\pack.ps1 -FrameworkDependent -SkipAndroid
    .\scripts\pack.ps1 -SplitApk
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [switch]$SplitApk,

    [switch]$SkipAndroid,

    [switch]$IncludeLinuxServer,

    [string]$OutputRoot = 'dist'
)

$ErrorActionPreference = 'Stop'

# 以脚本自身位置定位仓库根目录，保证从任何工作目录调用都正确
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    # ---------- 读取版本号 ----------
    $propsContent = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $version = if ($propsContent -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '0.0.0' }

    $windowsOut = Join-Path $OutputRoot 'windows'
    $androidOut = Join-Path $OutputRoot 'android'

    if (Test-Path $OutputRoot) {
        Remove-Item $OutputRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $windowsOut | Out-Null

    Write-Host ''
    Write-Host "ClassShout 打包  v$version  $Configuration / $Runtime" -ForegroundColor Cyan
    Write-Host ('=' * 66)

    # ---------- Windows ----------
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $modeText = if ($FrameworkDependent) { '依赖框架（需装 .NET 10 运行时）' } else { '自包含（免运行时）' }

    Write-Host ''
    Write-Host "[Windows] 发布模式：$modeText" -ForegroundColor Yellow

    $windowsProjects = @(
        @{ Name = '教室端';        Project = 'src\ClassShout.Classroom\ClassShout.Classroom.csproj' }
        @{ Name = '教师端桌面头';  Project = 'src\ClassShout.Teacher.Desktop\ClassShout.Teacher.Desktop.csproj' }
    )

    # 单文件发布参数：
    #   IncludeNativeLibrariesForSelfExtract —— Skia 等原生库也要进单文件
    #   EnableCompressionInSingleFile        —— 体积约减半，代价是首次启动稍慢
    # 注意：-r 与 RID 必须作为两个独立参数传入，写成 -rwin-x64 会被 MSBuild 当成未知开关。
    $publishArgs = @(
        '-c', $Configuration
        '-r', $Runtime
        "--self-contained=$selfContained"
        '-p:PublishSingleFile=true'
        '-p:IncludeNativeLibrariesForSelfExtract=true'
        '-p:DebugType=none'
    )

    if (-not $FrameworkDependent) {
        # 压缩只在自包含模式下受支持，依赖框架时加上会报 NETSDK1176
        $publishArgs += '-p:EnableCompressionInSingleFile=true'
    }

    foreach ($item in $windowsProjects) {
        Write-Host "  正在发布 $($item.Name)…"
        & dotnet publish $item.Project @publishArgs -o $windowsOut --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "$($item.Name) 发布失败（退出码 $LASTEXITCODE）"
        }
    }

    # 清掉发布目录里不需要分发的文件
    Get-ChildItem $windowsOut -File | Where-Object { $_.Extension -notin '.exe', '.dll', '.json' } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # 中继服务器：跨局域网部署要用，一起打出来省得部署时再翻命令。
    # 单独放子目录，避免和桌面应用的依赖文件混在一起。
    Write-Host '  正在发布中继服务器…'
    $serverOut = Join-Path $windowsOut 'server'
    New-Item -ItemType Directory -Force -Path $serverOut | Out-Null

    & dotnet publish 'src\ClassShout.RelayServer\ClassShout.RelayServer.csproj' @publishArgs -o $serverOut --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "中继服务器发布失败（退出码 $LASTEXITCODE）"
    }

    # 同上：把 web.config 之类的 IIS 专用文件清掉
    Get-ChildItem $serverOut -File |
        Where-Object { $_.Name -ne 'ClassShout.RelayServer.exe' } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Host ("    server\{0,-32} {1,8:N1} MB" -f 'ClassShout.RelayServer.exe', ((Get-Item (Join-Path $serverOut 'ClassShout.RelayServer.exe')).Length / 1MB))

    Write-Host ''
    Get-ChildItem $windowsOut -File | Sort-Object Name | ForEach-Object {
        Write-Host ("    {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
    }

    # ---------- 中继服务器（Linux） ----------
    if ($IncludeLinuxServer) {
        Write-Host ''
        Write-Host '[Linux] 发布中继服务器（linux-x64）' -ForegroundColor Yellow

        $linuxOut = Join-Path $OutputRoot 'linux'
        New-Item -ItemType Directory -Force -Path $linuxOut | Out-Null

        $linuxArgs = @(
            '-c', $Configuration
            '-r', 'linux-x64'
            '--self-contained=true'
            '-p:PublishSingleFile=true'
            '-p:IncludeNativeLibrariesForSelfExtract=true'
            '-p:EnableCompressionInSingleFile=true'
            '-p:DebugType=none'
        )

        & dotnet publish 'src\ClassShout.RelayServer\ClassShout.RelayServer.csproj' @linuxArgs -o $linuxOut --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "Linux 服务器发布失败（退出码 $LASTEXITCODE）"
        }

        # Web SDK 会带出几个只对 IIS 有意义的文件，独立运行时用不到，清掉免得干扰部署
        Get-ChildItem $linuxOut -File |
            Where-Object { $_.Name -notin 'ClassShout.RelayServer', 'ClassShout.Classroom' } |
            Remove-Item -Force -ErrorAction SilentlyContinue

        Write-Host ''
        Write-Host '[Linux] 发布教室端（linux-x64）' -ForegroundColor Yellow

        # 教室端的 Linux 目标是 net10.0（不是 net10.0-windows）：
        # 播放走 aplay/paplay 管道，保底朗读走 spd-say/espeak，主力朗读是 Edge 在线语音。
        # 必须显式指定 -f，否则多目标项目会要求选一个框架而直接报错。
        $classroomLinuxArgs = @('-f', 'net10.0') + $linuxArgs

        & dotnet publish 'src\ClassShout.Classroom\ClassShout.Classroom.csproj' @classroomLinuxArgs -o $linuxOut --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "Linux 教室端发布失败（退出码 $LASTEXITCODE）"
        }

        Write-Host ''
        Get-ChildItem $linuxOut -File | Sort-Object Name | ForEach-Object {
            Write-Host ("    {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
        }
    }

    # ---------- Android ----------
    if (-not $SkipAndroid) {
        Write-Host ''
        Write-Host '[Android] 打包 APK' -ForegroundColor Yellow

        foreach ($variable in 'ANDROID_HOME', 'JAVA_HOME') {
            if (-not (Get-Item "env:$variable" -ErrorAction SilentlyContinue)) {
                Write-Warning "环境变量 $variable 未设置，Android 打包可能失败。参见 docs\开发环境配置.md。"
            }
        }

        New-Item -ItemType Directory -Force -Path $androidOut | Out-Null
        $androidProject = 'src\ClassShout.Teacher.Android\ClassShout.Teacher.Android.csproj'

        # 通用包：包含 csproj 里声明的全部 ABI
        Write-Host '  正在打包通用 APK（arm64 + x64）…'
        $universalDir = Join-Path $OutputRoot 'obj\universal'
        & dotnet publish $androidProject -c $Configuration -o $universalDir --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "Android 通用包发布失败（退出码 $LASTEXITCODE）"
        }

        $universalApk = Get-ChildItem $universalDir -Filter '*-Signed.apk' | Select-Object -First 1
        if (-not $universalApk) {
            throw '未找到已签名的 APK，请确认 Android SDK 配置正确。'
        }

        $universalTarget = Join-Path $androidOut "classshout-teacher-$version-universal.apk"
        Copy-Item $universalApk.FullName $universalTarget
        Remove-Item $universalDir -Recurse -Force -ErrorAction SilentlyContinue

        # 单 ABI 包：只面向真机或只面向模拟器时体积更小
        # 用项目自定义的 AndroidAbi 开关，而不是传 RuntimeIdentifiers ——
        # 后者是全局属性，会传播到被引用的 Core / Design / Teacher 导致它们编译失败。
        if ($SplitApk) {
            foreach ($abi in 'android-arm64', 'android-x64', 'android-arm') {
                Write-Host "  正在打包 $abi…"
                $abiDir = Join-Path $OutputRoot "obj\$abi"
                & dotnet publish $androidProject -c $Configuration -o $abiDir `
                    "-p:AndroidAbi=$abi" --nologo -v q

                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "$abi 打包失败，已跳过。"
                    continue
                }

                $apk = Get-ChildItem $abiDir -Filter '*-Signed.apk' | Select-Object -First 1
                if ($apk) {
                    $short = $abi -replace 'android-', ''
                    Copy-Item $apk.FullName (Join-Path $androidOut "classshout-teacher-$version-$short.apk")
                }

                Remove-Item $abiDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        Write-Host ''
        Get-ChildItem $androidOut -File | Sort-Object Name | ForEach-Object {
            Write-Host ("    {0,-46} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
        }
    }
    else {
        Write-Host ''
        Write-Host '[Android] 已跳过' -ForegroundColor DarkGray
    }

    # ---------- 校验清单 ----------
    Write-Host ''
    $manifestPath = Join-Path $OutputRoot 'SHA256SUMS.txt'
    Get-ChildItem $OutputRoot -Recurse -File |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        ForEach-Object {
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            $relative = $_.FullName.Substring((Resolve-Path $OutputRoot).Path.Length + 1)
            "$hash  $relative"
        } | Set-Content $manifestPath -Encoding UTF8

    Write-Host "校验清单：$manifestPath" -ForegroundColor Green
    Write-Host ''
    Write-Host ('=' * 66)
    Write-Host '打包完成。' -ForegroundColor Green
    Write-Host ''
    Write-Host '  教师端：adb install -r <APK>；首次启动会申请麦克风权限。'

    if ($FrameworkDependent) {
        Write-Host '  教室端：把 exe 拷到教室电脑，' -NoNewline
        Write-Host '该机器必须先安装 .NET 10 运行时' -ForegroundColor Yellow -NoNewline
        Write-Host '。'
        Write-Host '          官方下载：https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor DarkGray
        Write-Host '          （也可用 dotnet publish 时改回默认的自包含模式，免去这一步）' -ForegroundColor DarkGray
    }
    else {
        Write-Host '  教室端：把 ClassShout.Classroom.exe 拷到教室电脑双击即可，无需安装任何运行时。'
    }
}
finally {
    Pop-Location
}
