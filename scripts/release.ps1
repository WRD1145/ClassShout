<#
.SYNOPSIS
    发布一个版本到 GitHub Releases：打包 → 打标签 → 建发行版 → 上传附件 → 逐个核对。

.DESCRIPTION
    本脚本存在的理由是把"发布"从一串手工命令变成可复核的步骤。
    手工发布时的真实风险不是"传错文件"，而是**传没传完没人知道**：
    Releases 的附件列表是唯一的交付凭据，而命令行上传既不会在失败时中止整个流程，
    也没有任何一步会回头核对结果 —— 少一个附件、或者某个附件只传了一半，
    发布者看到的仍然是"命令都执行完了"。

    所以在这里，上传不是一个动作，而是一组必须通过的断言：
    每个附件传完回查大小，最后回查发行版的附件数量与名字，任何一项对不上就以非零码退出。

    附件取自 dist\release\（由 pack.ps1 按最终附件名摆好），
    因此不需要在发布时再手敲一遍"哪个文件叫什么名字"。

.PARAMETER Version
    版本标签，形如 v1.1.1。必须与 Directory.Build.props 里的 <Version> 一致 ——
    否则发出去的产物内部写的是另一个版本号，控制台、程序集元数据都会对不上。

.PARAMETER NotesFile
    发行说明的 Markdown 文件。体例：Bug 修复 / 新功能 / 回退 三段，
    每条形如「说明 · 提交 · 相关提议」。

.PARAMETER Token
    GitHub 令牌。默认依次读环境变量 GITHUB_TOKEN、GH_TOKEN。不会打印到输出里。

.PARAMETER Proxy
    访问 GitHub 用的代理，例如 http://127.0.0.1:7890。不填则直连。

.PARAMETER SkipPack
    跳过打包，直接用现有的 dist\release\（调试脚本本身时用）。

.PARAMETER KeepTag
    发布失败时保留已经推上去的标签。默认失败即删掉本地与远端标签，
    免得留下一个指向半成品的标签，把后来的人引到错误的地方。

.EXAMPLE
    .\scripts\release.ps1 -Version v1.1.1 -NotesFile notes.md -Proxy http://127.0.0.1:7890
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NotesFile,

    [string]$Token,
    [string]$Proxy,

    [switch]$SkipPack,
    [switch]$KeepTag
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$tagPushed = $false

try {
    # ---------- 前置检查：先把"还没开始就已经错了"的事挡掉 ----------

    if ($Version -notmatch '^v\d+\.\d+\.\d+$') {
        throw "版本标签格式不对：$Version（应形如 v1.1.1）"
    }

    $propsContent = Get-Content 'Directory.Build.props' -Raw
    $propsVersion = if ($propsContent -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '' }

    if ($Version -ne "v$propsVersion") {
        throw "标签 $Version 与 Directory.Build.props 里的 <Version>$propsVersion</Version> 不一致。" +
              '先统一版本号再发布 —— 否则产物内部写的是另一个版本，事后根本分不清谁是谁。'
    }

    if (-not (Test-Path $NotesFile)) {
        throw "找不到发行说明文件：$NotesFile"
    }
    $notes = Get-Content $NotesFile -Raw

    if (-not $Token) { $Token = $env:GITHUB_TOKEN }
    if (-not $Token) { $Token = $env:GH_TOKEN }
    if (-not $Token) { throw '缺少 GitHub 令牌：用 -Token 传入，或设置环境变量 GITHUB_TOKEN' }

    $headers = @{
        Authorization = "Bearer $Token"
        Accept        = 'application/vnd.github+json'
        'User-Agent'  = 'ClassShout-Release'
    }

    # 只在实际给了代理时才带上，否则直连（本地开了代理软件的人未必希望走它）
    $http = @{}
    if ($Proxy) { $http['Proxy'] = $Proxy }

    $remote = (git remote get-url origin).Trim()
    if ($remote -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') {
        throw "无法从 origin 解析出仓库：$remote"
    }
    $slug = "$($Matches[1])/$($Matches[2])"

    # 发布必须来自已提交的状态：否则附件与标签指向的代码不是同一份
    $dirty = git status --porcelain
    if ($dirty) {
        throw "工作区不干净，拒绝发布。未提交的改动：`n$dirty"
    }

    $existing = git tag --list $Version
    if ($existing) { throw "标签 $Version 已存在" }

    Write-Host ''
    Write-Host "发布 $Version  →  $slug" -ForegroundColor Cyan

    # ---------- 打包 ----------

    if ($SkipPack) {
        Write-Host '[打包] 已跳过（-SkipPack）' -ForegroundColor DarkGray
    }
    else {
        Write-Host '[打包] 开始…' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'pack.ps1') -IncludeLinuxServer
        if ($LASTEXITCODE -ne 0) { throw "打包失败（退出码 $LASTEXITCODE）" }
    }

    $releaseDir = Join-Path $repoRoot 'dist\release'
    if (-not (Test-Path $releaseDir)) {
        throw "找不到 $releaseDir —— 请先打包（不要用 -SkipPack）"
    }

    $files = Get-ChildItem $releaseDir -File | Sort-Object Name
    if ($files.Count -eq 0) { throw "$releaseDir 里没有任何文件" }

    Write-Host ''
    Write-Host "待上传 $($files.Count) 个附件：" -ForegroundColor Yellow
    $files | ForEach-Object { Write-Host ("    {0,-46} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

    # ---------- 打标签 ----------

    # 先把分支推上去，再打标签。
    #
    # 顺序不能反：标签一旦推上去，发行版的附件就已经对外可见了，
    # 而这时若分支还停在本地，别人从发行版下载到的代码在仓库里根本找不到 ——
    # 上一个版本就是这么漏掉的（标签推了、main 没推，事后才发现远端落后一个提交）。
    Write-Host ''
    Write-Host '[推送] 分支…' -ForegroundColor Yellow

    git push origin HEAD
    if ($LASTEXITCODE -ne 0) { throw "推送分支失败 —— 标签尚未创建，可以先修好网络再重跑。" }

    # 标签可能已经存在：上一次跑到一半被中断（大附件上传很慢，超时是常事），
    # 重跑时不该因为"标签已存在"就停在这里 —— 那会逼人先手工删标签再重来一遍。
    git rev-parse $Version 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[标签] $Version 已存在，复用它" -ForegroundColor Yellow
    }
    else {
        git tag -a $Version -m $Version
        if ($LASTEXITCODE -ne 0) { throw "创建标签失败" }
    }

    git push origin $Version
    if ($LASTEXITCODE -ne 0) { throw "推送标签失败" }
    $tagPushed = $true
    Write-Host ''
    Write-Host "[标签] $Version 已推送" -ForegroundColor Green

    # ---------- 建发行版 ----------

    $payload = @{
        tag_name   = $Version
        name       = $Version
        body       = $notes
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json -Depth 4

    # 发行版同理：已存在就复用它、往上面补附件。
    # 这一段与上面"标签已存在就复用"是同一个理由 —— 上传几百兆被中断之后，
    # 重跑应该只补缺的那几个，而不是从头再来。
    $release = $null
    try {
        $release = Invoke-RestMethod -Method Get `
            -Uri "https://api.github.com/repos/$slug/releases/tags/$Version" -Headers $headers @http

        Write-Host "[发行版] 已存在 id=$($release.id)（现有 $($release.assets.Count) 个附件），补传缺的" -ForegroundColor Yellow
    }
    catch {
        $release = $null
    }

    if (-not $release) {
        $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$slug/releases" `
            -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($payload)) `
            -ContentType 'application/json; charset=utf-8' @http

        Write-Host "[发行版] 已创建 id=$($release.id)" -ForegroundColor Green
    }

    # ---------- 上传附件，并逐个核对 ----------

    Write-Host ''
    Write-Host '[上传] 开始…' -ForegroundColor Yellow

    foreach ($file in $files) {
        # 已经传好且大小一致的跳过，省得把几百兆重来一遍。
        # Releases 允许同名附件并存，所以"传了一半"的那种必须先删掉：
        # 留着它下载的人会不知道该选哪个。
        $existing = $release.assets | Where-Object { $_.name -eq $file.Name }

        if ($existing -and $existing.size -eq $file.Length) {
            Write-Host ("    [已存在] {0,-46} {1,8:N1} MB" -f $existing.name, ($existing.size / 1MB))
            continue
        }

        if ($existing) {
            Invoke-RestMethod -Method Delete `
                -Uri "https://api.github.com/repos/$slug/releases/assets/$($existing.id)" `
                -Headers $headers @http | Out-Null

            Write-Host "    [替换] $($file.Name)（原有附件大小不符）" -ForegroundColor Yellow
        }

        $uri = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$($file.Name)"
        $asset = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers `
            -InFile $file.FullName -ContentType 'application/octet-stream' @http

        if ($asset.size -ne $file.Length) {
            throw "附件 $($file.Name) 大小不符：本地 $($file.Length)，远端 $($asset.size)"
        }

        Write-Host ("    [已上传] {0,-46} {1,8:N1} MB" -f $asset.name, ($asset.size / 1MB))
    }

    # ---------- 终局核对：不看"我传了"，只看"发行版上有什么" ----------

    $final = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases/$($release.id)" `
        -Headers $headers @http

    if ($final.assets.Count -ne $files.Count) {
        throw "发行版上的附件数量是 $($final.assets.Count)，本地是 $($files.Count) —— 上传不完整"
    }

    $missing = @()
    foreach ($file in $files) {
        $match = $final.assets | Where-Object { $_.name -eq $file.Name -and $_.size -eq $file.Length }
        if (-not $match) { $missing += $file.Name }
    }
    if ($missing) {
        throw "以下附件没有正确落到发行版上：$($missing -join '、')"
    }

    Write-Host ''
    Write-Host ('=' * 66)
    Write-Host "发布完成：$Version（$($final.assets.Count) 个附件全部核对通过）" -ForegroundColor Green
    Write-Host $final.html_url
    Write-Host ''
}
catch {
    Write-Host ''
    Write-Host "发布失败：$($_.Exception.Message)" -ForegroundColor Red

    if ($tagPushed -and -not $KeepTag) {
        Write-Host "[清理] 回滚标签 $Version（要保留请加 -KeepTag）" -ForegroundColor DarkGray
        git tag -d $Version 2>&1 | Out-Null
        git push origin ":refs/tags/$Version" 2>&1 | Out-Null
    }

    exit 1
}
finally {
    Pop-Location
}
