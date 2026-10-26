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

    # ==================== 私有运行时（runtime\） ====================
    #
    # 发布包的样子：
    #   <解压根目录>\  程序（exe + 我们的程序集 + json）
    #   runtime\       运行时（host\fxr\<ver> + shared\<框架>\<ver>）
    # 启动脚本设好 DOTNET_ROOT 再起 exe —— 目标机上仍然**不需要装 .NET**。
    #
    # 为什么不采用"把自包含发布物里的框架文件挪进 runtime\"这条更省事的路：
    # 自包含发布物的 deps.json 把框架程序集写成**纯文件名**（它们本来就和 exe
    # 挤在一个目录里），一旦挪走，宿主找不到 hostpolicy 就直接 0xC0000135 ——
    # 试过，起不来。而 runtime\shared\... 要被宿主认作"装好的 .NET"，
    # 必须带上框架自己的 deps.json / runtimeconfig.json / .version；
    # 这三样正好在 NuGet 的运行时包里（runtimes\<rid>\lib\<tfm>\），
    # 于是"用运行时包拼一份私有 .NET"既有正确元数据，又不用本机装同版本运行时。

    # NuGet 的包目录不一定是 %USERPROFILE%\.nuget\packages（CI 上常被指到别处），
    # 所以每次都问 dotnet 要一次 —— 猜路径的话，第一次在干净机器上打包就会找不到运行时包。
    function Get-NuGetPackagesRoot {
        $line = (& dotnet nuget locals global-packages --list 2>$null | Select-Object -First 1)
        if ($line -match 'global-packages:\s*(.+)$') {
            return $Matches[1].Trim()
        }

        return Join-Path $env:USERPROFILE '.nuget\packages'
    }

    function Find-RuntimePack {
        param([string]$FxName, [string]$Rid, [int]$Major, [int]$Minor)

        $base = Join-Path (Get-NuGetPackagesRoot) ($FxName.ToLowerInvariant() + ".runtime.$Rid")
        if (-not (Test-Path $base)) { return $null }

        Get-ChildItem $base -Directory |
            ForEach-Object { [pscustomobject]@{ Version = [version]$_.Name; Path = $_.FullName } } |
            Where-Object { $_.Version.Major -eq $Major -and $_.Version.Minor -eq $Minor } |
            Sort-Object Version |
            Select-Object -Last 1
    }

    function Install-PrivateRuntime {
        param([string]$AppDir, [string]$Rid, [string]$Project)

        $rcFile = Get-ChildItem $AppDir -Filter '*.runtimeconfig.json' -File | Select-Object -First 1
        if (-not $rcFile) { throw "找不到 runtimeconfig.json：$AppDir" }

        $rc = Get-Content $rcFile.FullName -Raw | ConvertFrom-Json
        $wanted = @()
        if ($rc.runtimeOptions.framework) { $wanted += $rc.runtimeOptions.framework }
        if ($rc.runtimeOptions.frameworks) { $wanted += $rc.runtimeOptions.frameworks }

        if ($wanted.Count -eq 0) {
            Write-Host '    这次发布没有声明框架（自包含？），跳过 runtime\ 组装' -ForegroundColor DarkGray
            return
        }

        # 运行时包只有**自包含**发布才会被下载；而这里发的是依赖框架的发布物，
        # 干净机器上那份包压根不在缓存里（CI 上就是这么挂的）。
        # 于是显式 restore 一次，把运行时包拉下来 —— 这一步只写缓存，不改发布物。
        if ($Project) {
            Write-Host '    正在取运行时包（restore）…'
            & dotnet restore $Project -r $Rid -p:SelfContained=true --nologo -v q
            if ($LASTEXITCODE -ne 0) {
                throw "取运行时包失败（restore 退出码 $LASTEXITCODE）：$Project / $Rid"
            }
        }

        $runtimeDir = Join-Path $AppDir 'runtime'
        $lastFxDir = $null

        foreach ($fx in $wanted) {
            $want = [version]$fx.version
            $pack = Find-RuntimePack -FxName $fx.name -Rid $Rid -Major $want.Major -Minor $want.Minor
            if (-not $pack) {
                throw "NuGet 缓存里找不到 $($fx.name) 的 $Rid 运行时包（$($want.Major).$($want.Minor).x）。先跑一次 dotnet restore / dotnet publish 让它下载下来，或者改用 -FrameworkDependent。"
            }

            $ver = $pack.Version.ToString()
            $packRoot = Join-Path $pack.Path "runtimes\$Rid"
            $libDir = Get-ChildItem (Join-Path $packRoot 'lib') -Directory -ErrorAction SilentlyContinue | Select-Object -Last 1
            if (-not $libDir) { throw "运行时包里没有 lib 目录：$($pack.Path)" }

            $fxDir = Join-Path $runtimeDir ("shared\{0}\{1}" -f $fx.name, $ver)
            New-Item -ItemType Directory -Force -Path $fxDir | Out-Null

            Copy-Item (Join-Path $libDir.FullName '*') $fxDir -Recurse -Force

            # 不是每个框架的运行时包都有 native\ 目录（AspNetCore 的 linux 包就没有），
            # 直接 Copy-Item 一个不存在的路径会整段打包失败。
            $nativeDir = Join-Path $packRoot 'native'
            if (Test-Path $nativeDir) {
                Copy-Item (Join-Path $nativeDir '*') $fxDir -Recurse -Force
            }

            # 宿主认"这是一份装好的框架"靠的就是这个文件
            [System.IO.File]::WriteAllText((Join-Path $fxDir '.version'), "$ver`n")

            # hostfxr 必须在 host\fxr\<ver>\ 下：apphost 先看自己旁边的目录，
            # 再按 DOTNET_ROOT 找，而它找的正是这个位置。
            #
            # Windows 上叫 hostfxr.dll，Linux 上叫 **libhostfxr.so** —— 只按 hostfxr.*
            # 匹配的话，Linux 包里根本不会有 host\fxr，解压出来就是
            # "You must install .NET to run this application"（这个坑踩过一次）。
            $hostFxr = Get-ChildItem $fxDir -File |
                Where-Object { $_.Name -like 'hostfxr.*' -or $_.Name -like 'libhostfxr*' } |
                Select-Object -First 1
            if ($hostFxr) {
                $hostDir = Join-Path $runtimeDir ("host\fxr\{0}" -f $ver)
                New-Item -ItemType Directory -Force -Path $hostDir | Out-Null
                Move-Item $hostFxr.FullName (Join-Path $hostDir $hostFxr.Name) -Force
            }

            $lastFxDir = $fxDir
            Write-Host ("    runtime/{0}/{1}  ({2} 个文件)" -f $fx.name, $ver, (Get-ChildItem $fxDir -Recurse -File).Count)
        }

        # hostpolicy 是从**最后一个**框架目录里加载的：多个框架（服务器带 ASP.NET Core）时，
        # 排在后面的那个目录里也得有它 —— 否则宿主报"hostpolicy.dll 找不到"。
        if ($wanted.Count -gt 1 -and $lastFxDir) {
            $hostPolicy = Get-ChildItem (Join-Path $runtimeDir 'shared') -Recurse -File |
                Where-Object { $_.Name -like 'hostpolicy.*' -or $_.Name -like 'libhostpolicy*' } |
                Select-Object -First 1

            if ($hostPolicy) {
                Copy-Item $hostPolicy.FullName (Join-Path $lastFxDir $hostPolicy.Name) -Force
            }
            else {
                throw "私有运行时里找不到 hostpolicy（$Rid）：多个框架时宿主只会在最后一个框架目录里找它。"
            }
        }

        # 打完自检：没有 host\fxr 的包在目标机上只会给一句"请安装 .NET"，
        # 那是最难查的一种错 —— 打包时就直接失败。
        if (-not (Test-Path (Join-Path $runtimeDir 'host\fxr'))) {
            throw "私有运行时没生成 host\fxr（$Rid）：启动时宿主会找不到 .NET。"
        }
    }
    # 打 .tar.gz 一律在进程里做，不调外部 tar。
    #
    # 起因：CI 上 `tar -czf` 把 106 MB 的服务器目录压成了几百 KB 的包，却仍然返回 0；
    # 改成 $env:SystemRoot\System32\tar.exe 之后结果一样。既然外部工具在 runner 上
    # 不可信，就用自己的实现：System.Formats.Tar 写 tar、GZipStream 压 gz，
    # 结果只取决于我们的代码与文件本身。
    function New-TarGz {
        param([string]$SourceDir, [string]$TargetPath)

        Remove-Item $TargetPath -Force -ErrorAction SilentlyContinue

        $tarTemp = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.tar')

        try {
            # includeBaseDirectory = true：包内顶层就是源目录名，解压出来是"一个文件夹"
            [System.Formats.Tar.TarFile]::CreateFromDirectory($SourceDir, $tarTemp, $true)

            $tarIn = [System.IO.File]::OpenRead($tarTemp)
            $tarOut = [System.IO.File]::Create($TargetPath)

            try {
                $gzipStream = [System.IO.Compression.GZipStream]::new(
                    $tarOut, [System.IO.Compression.CompressionLevel]::Optimal)

                try {
                    $tarIn.CopyTo($gzipStream)
                }
                finally {
                    $gzipStream.Dispose()
                }
            }
            finally {
                $tarOut.Dispose()
                $tarIn.Dispose()
            }
        }
        finally {
            Remove-Item $tarTemp -Force -ErrorAction SilentlyContinue
        }
    }
    # ---------- Windows ----------
    # 发布物本身一律是"依赖框架"的（程序文件因此能直接待在解压根目录里），
    # 运行时另用 Install-PrivateRuntime 摆进 runtime\：
    #   · 默认：解压即用，目标机不需要装 .NET（运行时就在 runtime\ 下）；
    #   · -FrameworkDependent：连 runtime\ 也不带，体积最小，但目标机必须装 .NET。
    $selfContained = 'false'
    $modeText = if ($FrameworkDependent) { '依赖框架（需装 .NET 10 运行时，不带 runtime\）' } else { '自带运行时（runtime\ 下，免安装）' }

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

        # 运行时单独摆进 runtime\（-FrameworkDependent 时不带，要求目标机装 .NET）
        if (-not $FrameworkDependent) {
            Install-PrivateRuntime -AppDir $out -Rid $Runtime -Project $item.Project
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
            '--self-contained=false'
            '-p:DebugType=none'
        )

        $serverLinuxOut = Join-Path $linuxOut 'server'
        New-Item -ItemType Directory -Force -Path $serverLinuxOut | Out-Null

        & dotnet publish 'src\ClassShout.RelayServer\ClassShout.RelayServer.csproj' @linuxArgs -o $serverLinuxOut --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "Linux 服务器发布失败（退出码 $LASTEXITCODE）"
        }

        if (-not $FrameworkDependent) {
            Install-PrivateRuntime -AppDir $serverLinuxOut -Rid 'linux-x64' -Project 'src\ClassShout.RelayServer\ClassShout.RelayServer.csproj'
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

        if (-not $FrameworkDependent) {
            Install-PrivateRuntime -AppDir $classroomLinuxOut -Rid 'linux-x64' -Project 'src\ClassShout.Classroom\ClassShout.Classroom.csproj'
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
        # 现在解压出来是这样的：
        #   根\           程序本体（exe + 程序集 + json）—— 主程序就在这一层
        #   根\runtime\   运行时（私有 .NET，见 Install-PrivateRuntime）
        #   根\logs\      日志
        #   根\启动脚本    设好 DOTNET_ROOT 再起 exe
        # 程序与运行时同层不同目录：主程序一眼能找到，运行时不用管。
        Copy-Item (Join-Path $SourceDir '*') $staging -Recurse -Force

        # 顶层 logs\：启动脚本用 CLASSSHOUT_LOG_DIR 指过来，
        # 于是"找日志"和"找软件"在同一层，不用钻进子目录里翻。
        New-Item -ItemType Directory -Force -Path (Join-Path $staging 'logs') | Out-Null

        if ($Gzip) {
            # Linux：一个可执行的 run.sh
            $script = @"
#!/bin/sh
# ClassShout $LaunchName
#
# 主程序就在这一层，运行时在同级的 runtime/ 下（私有 .NET，不需要另装）。
# DOTNET_ROOT 必须在启动前设好，否则宿主会去找系统里装的那份。
cd "`$(dirname "`$0")" || exit 1
export DOTNET_ROOT="`$(pwd)/runtime"
export CLASSSHOUT_LOG_DIR="`$(pwd)/logs"
exec "./$LaunchName" "`$@"
"@

            $launcher = Join-Path $staging 'run.sh'
        }
        else {
            # Windows：一个双击就行的 .cmd
            $script = @"
@echo off
rem ClassShout $LaunchName
rem
rem The program sits in this folder; the runtime is in runtime\ next to it (private .NET,
rem nothing has to be installed). DOTNET_ROOT must be set before the host starts,
rem otherwise it looks for a system-wide .NET.
rem
rem OS check: .NET no longer supports Windows 7 / 8.1. On those systems the host dies
rem with "The procedure entry point ... could not be located", which explains nothing.
rem So stop here and say so. ASCII only: cmd.exe parses this file in the ANSI code page,
rem so anything non-ASCII would turn into mojibake (or even break parsing).
for /f "tokens=4,5 delims=. " %%a in ('ver') do (
  set "WINMAJOR=%%a"
  set "WINMINOR=%%b"
)
if not defined WINMAJOR goto :run
if %WINMAJOR% LSS 10 goto :oldwindows

:run
cd /d "%~dp0"
set DOTNET_ROOT=%~dp0runtime
set CLASSSHOUT_LOG_DIR=%~dp0logs
start "" "%~dp0$LaunchName" %*
exit /b 0

:oldwindows
echo.
echo   ClassShout needs Windows 10 version 1607 or later.
echo.
echo   This machine reports Windows %WINMAJOR%.%WINMINOR%. .NET 10 cannot run on it,
echo   so the program cannot start here.
echo.
echo   Options:
echo     - put Windows 10/11 on this PC, or use a small x64 Linux box
echo     - the Linux classroom build needs no .NET either
echo.
echo   See the readme (.txt) in this folder for details.
pause
exit /b 1
"@

            $launcher = Join-Path $staging "$LaunchName 启动.cmd"
        }

        # 换行符要分开处理：
        #   · run.sh 用 LF（Linux 上 CRLF 会让 /bin/sh 报 "$'\r': command not found"）；
        #   · .cmd 必须用 CRLF —— cmd.exe 对 LF 换行的手册式宽容只覆盖简单语句，
        #     遇到标签、for、括号块就会错位（这个启动脚本里三样都有）。
        # 早期版本统一压成 LF，结果中文环境下双击 .cmd 直接报一堆
        # "'…' is not recognized"，程序根本没起来。
        $lineEnding = if ($Gzip) { "`n" } else { "`r`n" }
        $launcherText = $script -replace "`r`n", "`n"
        $launcherText = $launcherText -replace "`n", $lineEnding

        [System.IO.File]::WriteAllText($launcher, $launcherText, (New-Object System.Text.UTF8Encoding($false)))

        $readme = @"
ClassShout · $LaunchName
$('=' * 60)

启动：双击「$(Split-Path $launcher -Leaf)」
      （Windows 会弹一下黑窗口，那是启动脚本，一闪就没了）

目录说明：
  $LaunchName
          主程序本体（同层的其它 dll/json 都是它要用的，别单独搬走）
  runtime\
          运行时（私有 .NET）。**别删**，也别单独更新它 —— 升级时整个目录一起覆盖。
  logs\   运行日志，按天一个文件，自动只留最近 7 天

（不需要在系统里另装 .NET：运行时就在 runtime\ 下。）

升级：把新的压缩包解压覆盖本目录即可 —— 配置与日志都不在这里
      （Windows 存在 %LOCALAPPDATA%\ClassShout\），覆盖不会丢东西。

$LaunchHint
"@

        # 这份说明带 BOM：老系统的记事本不认"无 BOM 的 UTF-8"，打开是一片乱码 ——
        # 而它恰恰是给旧机器上的人看的那一份。
        [System.IO.File]::WriteAllText(
            (Join-Path $staging '使用说明.txt'),
            ($readme -replace "`r`n", "`r`n"),
            (New-Object System.Text.UTF8Encoding($true)))

        Remove-Item $TargetPath -Force -ErrorAction SilentlyContinue

        if ($Gzip) {
            # 可执行位：Windows 打的 tar 不保留 Unix 权限位，解压端要自己 chmod。
            # 这里至少让 run.sh 的意图写清楚（部署文档里有 chmod +x 那一步）。
            #
            # **显式用 System32 里的 tar**，不靠 PATH：runner 上排在前面的是 Git 附带的
            # GNU tar，它压出来只有几百 KB 的残包（却照样返回 0）。本机 PATH 里那份是
            # Windows 自带的 bsdtar，所以这个错本地永远复现不出来。
            # 打 tar.gz 在进程里做（见文件前面的 New-TarGz）：
            # CI 上 `tar -czf` 压出来的包只有几百 KB 却返回 0，换 System32 的 tar 也一样，
            # 所以不再依赖任何外部 tar。
            New-TarGz -SourceDir $staging -TargetPath $TargetPath
        }
        else {
            # Windows 的 zip 用 Compress-Archive（同样是"不依赖 PATH 里是哪一份工具"）。
            Remove-Item $TargetPath -Force -ErrorAction SilentlyContinue
            Compress-Archive -Path $staging -DestinationPath $TargetPath -CompressionLevel Optimal -Force
        }

        if (-not (Test-Path $TargetPath)) {
            throw "打包 $RootName 失败"
        }

        # 残包检查：体积不到源目录的 5% 一定是压错了（runner 上真的出现过一次，
        # 只压进去说明文件和 run.sh，却仍然是"成功"）。靠"文件存在"发现不了。
        $sourceSize = (Get-ChildItem $staging -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $archiveSize = (Get-Item $TargetPath).Length

        if ($sourceSize -gt 10MB -and $archiveSize -lt ($sourceSize * 0.05)) {
            throw ("打包 $RootName 失败：压缩包只有 {0:N2} MB，而源目录有 {1:N1} MB" -f ($archiveSize / 1MB), ($sourceSize / 1MB))
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
            -LaunchHint '中继服务器（Linux）：解压后 chmod +x ClassShout.RelayServer 与 run.sh；用 run.sh 启动（它会设好 DOTNET_ROOT）。' `
            -Gzip

        New-AppArchive -SourceDir (Join-Path $OutputRoot 'linux\classroom') -RootName 'ClassShout.Classroom' `
            -TargetPath (Join-Path $releaseOut 'ClassShout.Classroom-linux-x64.tar.gz') `
            -LaunchName 'ClassShout.Classroom' `
            -LaunchHint '教室端（Linux）：解压后 chmod +x ClassShout.Classroom 与 run.sh（用 run.sh 启动）；朗读需要有 spd-say 或 espeak-ng。' `
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
