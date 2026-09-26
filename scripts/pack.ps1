<#
.SYNOPSIS
    打包 ClassShout 的可分发产物：Windows 单文件 exe 与 Android APK。

.DESCRIPTION
    默认产出：
      dist\windows\classroom\ClassShout.Classroom.exe       教室端（自包含，目标机无需装 .NET）
      dist\windows\teacher\ClassShout.Teacher.Desktop.exe    教师端桌面头（同上）
      dist\windows\server\ClassShout.RelayServer.exe         中继服务器（Windows）
      dist\android\classshout-teacher-<版本>-universal.apk   含 arm64 + x64

    自 1.10.0 起**不再打成单文件**，而是"每个应用一个文件夹"；
    另外生成 dist\release\ —— 按「最终附件名」摆好的可发布资产目录（每个应用一个
    zip / tar.gz），内含 SHA256SUMS.txt。发布脚本（scripts\release.ps1）只需把这个
    目录里的东西全传上去、再逐个核对，不必再靠记忆去拼"哪个文件叫什么名字"。

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

    # 发布参数。
    #
    # 自 1.10.0 起**不再打成单文件**：单文件每次启动都要把 Skia 这类原生库解压到
    # 临时目录，首次启动明显变慢，体积还比"文件夹 + 压缩包"更大。
    # 对使用者来说并没有更难 —— 解压到哪里就在哪里双击运行。
    # 注意：-r 与 RID 必须作为两个独立参数传入，写成 -rwin-x64 会被 MSBuild 当成未知开关。
    $publishArgs = @(
        '-c', $Configuration
        '-r', $Runtime
        "--self-contained=$selfContained"
        '-p:DebugType=none'
    )

    # 三个应用各自一个子目录。它们引用同一批程序集（Core / Design），
    # 平铺在同一个目录里会互相覆盖 —— 单文件时代可以平铺，现在不行。
    $windowsApps = @(
        @{ Name = '教室端';       Project = 'src\ClassShout.Classroom\ClassShout.Classroom.csproj'; Framework = 'net10.0-windows'; Folder = 'classroom' }
        @{ Name = '教师端桌面头'; Project = 'src\ClassShout.Teacher.Desktop\ClassShout.Teacher.Desktop.csproj'; Framework = $null; Folder = 'teacher' }
        @{ Name = '中继服务器';   Project = 'src\ClassShout.RelayServer\ClassShout.RelayServer.csproj'; Framework = $null; Folder = 'server' }
    )

    # Framework 一列是必须的：教室端自 1.1.0 起是多目标（net10.0-windows;net10.0，
    # 后者给 Linux 用），不显式指定框架时 dotnet publish 会以 NETSDK1129 直接拒绝。
    foreach ($item in $windowsApps) {
        Write-Host "  正在发布 $($item.Name)…"

        $out = Join-Path $windowsOut $item.Folder
        New-Item -ItemType Directory -Force -Path $out | Out-Null

        $args = $publishArgs
        if ($item.Framework) {
            $args = @('-f', $item.Framework) + $publishArgs
        }

        & dotnet publish $item.Project @args -o $out --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "$($item.Name) 发布失败（退出码 $LASTEXITCODE）"
        }

        # Web SDK 会带出 web.config 这类只对 IIS 有意义的文件，独立运行时用不到
        Get-ChildItem $out -File |
            Where-Object { $_.Extension -in '.config', '.pdb' } |
            Remove-Item -Force -ErrorAction SilentlyContinue

        $size = (Get-ChildItem $out -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
        Write-Host ("    {0,-12} {1,8:N1} MB（{2} 个文件）" -f $item.Folder, $size, (Get-ChildItem $out -Recurse -File).Count)
    }

    # ---------- 中继服务器（Linux） ----------
    if ($IncludeLinuxServer) {
        Write-Host ''
        Write-Host '[Linux] 发布 linux-x64（服务器 + 教室端）' -ForegroundColor Yellow

        $linuxOut = Join-Path $OutputRoot 'linux'

        # 同样不再单文件；两个应用各一个子目录（理由同 Windows）
        $linuxArgs = @(
            '-c', $Configuration
            '-r', 'linux-x64'
            '--self-contained=true'
            '-p:DebugType=none'
        )

        $serverLinuxOut = Join-Path $linuxOut 'server'
        New-Item -ItemType Directory -Force -Path $serverLinuxOut | Out-Null

        & dotnet publish 'src\ClassShout.RelayServer\ClassShout.RelayServer.csproj' @linuxArgs -o $serverLinuxOut --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "Linux 服务器发布失败（退出码 $LASTEXITCODE）"
        }

        # Web SDK 会带出几个只对 IIS 有意义的文件，独立运行时用不到，清掉免得干扰部署
        Get-ChildItem $serverLinuxOut -File |
            Where-Object { $_.Extension -in '.config', '.pdb' } |
            Remove-Item -Force -ErrorAction SilentlyContinue

        # 教室端的 Linux 目标是 net10.0（不是 net10.0-windows）：
        # 播放走 aplay/paplay 管道，保底朗读走 spd-say/espeak，主力朗读是 Edge 在线语音。
        # 必须显式指定 -f，否则多目标项目会要求选一个框架而直接报错。
        $classroomLinuxOut = Join-Path $linuxOut 'classroom'
        New-Item -ItemType Directory -Force -Path $classroomLinuxOut | Out-Null

        $classroomLinuxArgs = @('-f', 'net10.0') + $linuxArgs

        & dotnet publish 'src\ClassShout.Classroom\ClassShout.Classroom.csproj' @classroomLinuxArgs -o $classroomLinuxOut --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "Linux 教室端发布失败（退出码 $LASTEXITCODE）"
        }

        Get-ChildItem $classroomLinuxOut -File |
            Where-Object { $_.Extension -in '.config', '.pdb' } |
            Remove-Item -Force -ErrorAction SilentlyContinue

        Write-Host ''
        foreach ($folder in 'server', 'classroom') {
            $path = Join-Path $linuxOut $folder
            $size = (Get-ChildItem $path -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
            Write-Host ("    {0,-12} {1,8:N1} MB（{2} 个文件）" -f $folder, $size, (Get-ChildItem $path -Recurse -File).Count)
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

    # ---------- 组装可分发的资产目录 ----------
    #
    # 这一步是为了让发布不再依赖记忆："哪个文件该以什么名字上传、放在哪一层目录"
    # 如果只存在于发布时敲的那条命令里，那它就不在任何地方 —— 换个人、
    # 或者同一个人隔一个月再做一次，都可能摆错。
    #
    # 固化成下面这张表：打包时就把资产按最终附件名摆进 dist\release\，
    # 发布脚本只需"把 dist\release 里的东西全传上去，再逐个核对"。
    $releaseOut = Join-Path $OutputRoot 'release'
    New-Item -ItemType Directory -Force -Path $releaseOut | Out-Null

    # 自 1.10.0 起每个应用打成一个压缩包（附件名见下）。
    #
    # 为什么压缩包里还套一层以应用命名的目录：解压出来是一整个目录，
    # 而不是十几个 dll 散落在"下载"文件夹里 —— 后者在教室里那台机器上
    # 基本等于"从此找不到它装在哪"。
    #
    # 用系统自带的 tar（Windows 10 起就有，bsdtar）而不是 Compress-Archive：
    # 后者对上百个文件慢得多，而且 -a 让它按扩展名自动选 zip 还是 gzip。
    function New-AppArchive {
        param(
            [string]$SourceDir,
            [string]$RootName,
            [string]$TargetPath,
            [string]$LaunchName,
            [string]$LaunchHint,
            [switch]$Gzip
        )

        if (-not (Test-Path $SourceDir)) {
            throw "缺少产物目录：$SourceDir —— 打包没有真正完成"
        }

        $staging = Join-Path $OutputRoot ('obj\stage\' + $RootName)
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path (Split-Path $staging -Parent) | Out-Null
        New-Item -ItemType Directory -Force -Path $staging | Out-Null

        # 程序本体放在 app\ 子目录里。
        #
        # 为什么不能把 dll 单独分出去：.NET 的运行时宿主文件（hostpolicy / hostfxr /
        # coreclr / System.Private.CoreLib …）必须与 exe 同级，而它们占了文件数的
        # 一大半 —— 试过把其余程序集挪进 lib\ 并改 deps.json，应用直接崩在
        # "hostpolicy.dll not found"。所以"一个 exe + 一个 dll 文件夹"在 .NET 上做不到。
        #
        # 能做的是把**界面**收拾干净：顶层只留一个启动脚本、一份说明和 logs\，
        # 257 个程序文件全部待在 app\ 里。
        $appDir = Join-Path $staging 'app'
        Copy-Item $SourceDir $appDir -Recurse -Force

        # 顶层 logs\：启动脚本用 CLASSSHOUT_LOG_DIR 指过来，
        # 于是"找日志"和"找软件"在同一层，不用钻进 app\ 里翻。
        New-Item -ItemType Directory -Force -Path (Join-Path $staging 'logs') | Out-Null

        if ($Gzip) {
            # Linux：一个可执行的 run.sh
            $script = @"
#!/bin/sh
# ClassShout $LaunchName
#
# 程序在 app/ 里，日志写进同级的 logs/。
cd "`$(dirname "`$0")" || exit 1
export CLASSSHOUT_LOG_DIR="`$(pwd)/logs"
exec ./app/$LaunchName "`$@"
"@

            $launcher = Join-Path $staging 'run.sh'
        }
        else {
            # Windows：一个双击就行的 .cmd
            $script = @"
@echo off
rem ClassShout $LaunchName
rem
rem 程序在 app\ 里，日志写进同级的 logs\。
cd /d "%~dp0"
set CLASSSHOUT_LOG_DIR=%~dp0logs
start "" "%~dp0app\$LaunchName" %*
"@

            $launcher = Join-Path $staging "$LaunchName 启动.cmd"
        }

        [System.IO.File]::WriteAllText($launcher, ($script -replace "`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))

        $readme = @"
ClassShout · $LaunchName
$('=' * 60)

启动：双击「$(Split-Path $launcher -Leaf)」
      （Windows 会弹一下黑窗口，那是启动脚本，一闪就没了）

目录说明：
  app\    程序本体（exe 与它需要的全部文件，别单独搬走其中的文件）
  logs\   运行日志，按天一个文件，自动只留最近 7 天

升级：把新的压缩包解压覆盖本目录即可 —— 配置与日志都不在这里
      （Windows 存在 %LOCALAPPDATA%\ClassShout\），覆盖不会丢东西。

$LaunchHint
"@

        [System.IO.File]::WriteAllText(
            (Join-Path $staging '使用说明.txt'),
            ($readme -replace "`r`n", "`r`n"),
            (New-Object System.Text.UTF8Encoding($false)))

        Remove-Item $TargetPath -Force -ErrorAction SilentlyContinue

        if ($Gzip) {
            # 可执行位：Windows 打的 tar 不保留 Unix 权限位，解压端要自己 chmod。
            # 这里至少让 run.sh 的意图写清楚（部署文档里有 chmod +x 那一步）。
            & tar -czf $TargetPath -C (Split-Path $staging -Parent) $RootName
        }
        else {
            & tar -a -c -f $TargetPath -C (Split-Path $staging -Parent) $RootName
        }

        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $TargetPath)) {
            throw "打包 $RootName 失败"
        }

        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    # 附件名一经发布就不要再改：README、历史发行版、别人的脚本都按它引用。
    New-AppArchive -SourceDir (Join-Path $windowsOut 'classroom') -RootName 'ClassShout.Classroom' `
        -TargetPath (Join-Path $releaseOut 'ClassShout.Classroom-win-x64.zip') `
        -LaunchName 'ClassShout.Classroom.exe' `
        -LaunchHint '教室端：首次启动若弹防火墙提示，勾选「专用网络」并允许。'

    New-AppArchive -SourceDir (Join-Path $windowsOut 'teacher') -RootName 'ClassShout.Teacher' `
        -TargetPath (Join-Path $releaseOut 'ClassShout.Teacher-win-x64.zip') `
        -LaunchName 'ClassShout.Teacher.Desktop.exe' `
        -LaunchHint '教师端（桌面）：手机上请装同一次发布里的 APK。'

    New-AppArchive -SourceDir (Join-Path $windowsOut 'server') -RootName 'ClassShout.RelayServer' `
        -TargetPath (Join-Path $releaseOut 'ClassShout.RelayServer-win-x64.zip') `
        -LaunchName 'ClassShout.RelayServer.exe' `
        -LaunchHint '中继服务器：常驻部署见文档「中继服务器」一节。'

    if ($IncludeLinuxServer) {
        New-AppArchive -SourceDir (Join-Path $OutputRoot 'linux\server') -RootName 'ClassShout.RelayServer' `
            -TargetPath (Join-Path $releaseOut 'ClassShout.RelayServer-linux-x64.tar.gz') `
            -LaunchName 'ClassShout.RelayServer' `
            -LaunchHint '中继服务器（Linux）：解压后 chmod +x app/ClassShout.RelayServer 与 run.sh。' `
            -Gzip

        New-AppArchive -SourceDir (Join-Path $OutputRoot 'linux\classroom') -RootName 'ClassShout.Classroom' `
            -TargetPath (Join-Path $releaseOut 'ClassShout.Classroom-linux-x64.tar.gz') `
            -LaunchName 'ClassShout.Classroom' `
            -LaunchHint '教室端（Linux）：解压后 chmod +x app/ClassShout.Classroom 与 run.sh；朗读需要有 spd-say 或 espeak-ng。' `
            -Gzip
    }

    # APK 的文件名本来就是最终附件名（含版本号），直接沿用
    Get-ChildItem $androidOut -Filter '*.apk' -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $releaseOut $_.Name) -Force
    }

    # ---------- 校验清单 ----------
    #
    # 三个细节都是踩过的坑，缺一个这份清单就等于没有：
    #   1. 名字用附件名 —— 早先写的是构建目录相对路径，跟下载到的文件名对不上号；
    #   2. 行格式用 sha256sum 能直接吃的「哈希 + 两空格 + 文件名」；
    #   3. 换行必须是 LF。清单在 Windows 上生成，默认会写成 CRLF，而 sha256sum
    #      会把行尾的 \r 当成文件名的一部分，于是每个文件都"不存在"。配上
    #      --ignore-missing 还会被静默跳过，最后只报一句「no file was verified」，
    #      让人以为是下载坏了。服务端本来就部署在 Linux 上，这份清单必须在那儿能用。
    $manifestPath = Join-Path $releaseOut 'SHA256SUMS.txt'
    $manifestLines = Get-ChildItem $releaseOut -File |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object Name |
        ForEach-Object {
            "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)  $($_.Name)"
        }

    # 不带 BOM 的 UTF-8，行尾 LF（-NoNewline + 手动 join 才能保证不掺进 CR）
    [System.IO.File]::WriteAllText(
        $manifestPath,
        (($manifestLines -join "`n") + "`n"),
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Host ''
    Write-Host '可发布资产（dist\release，附件名即文件名）：' -ForegroundColor Green
    Get-ChildItem $releaseOut -File | Sort-Object Name | ForEach-Object {
        Write-Host ("    {0,-46} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
    }
    Write-Host "校验清单：$manifestPath" -ForegroundColor Green
    Write-Host ''
    Write-Host ('=' * 66)
    Write-Host '打包完成。' -ForegroundColor Green
    Write-Host ''
    Write-Host '  教师端（手机）：adb install -r <APK>；首次启动会申请麦克风权限。'
    Write-Host '  桌面端：把对应的压缩包解压到一个固定目录，双击里面的 exe 即可。'

    if ($FrameworkDependent) {
        Write-Host '          该机器必须先安装 .NET 10 运行时' -ForegroundColor Yellow
        Write-Host '          官方下载：https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor DarkGray
    }
    else {
        Write-Host '          免运行时（自包含）；升级就是解压覆盖同一个目录。'
    }
}
finally {
    Pop-Location
}
