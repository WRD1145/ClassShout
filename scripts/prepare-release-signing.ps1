<# 仅在 CI 临时目录解码签名材料；不将密码写入文件或 GITHUB_ENV。 #>
[CmdletBinding()]
param([switch]$AllowUnsignedDryRun)
$ErrorActionPreference = 'Stop'
if (-not $env:GITHUB_ENV -or -not $env:RUNNER_TEMP) { throw '签名材料准备仅用于 GitHub Actions。' }
$names = @('WINDOWS_SIGNING_PFX_BASE64','WINDOWS_SIGNING_PASSWORD','ANDROID_SIGNING_KEYSTORE_BASE64','ANDROID_SIGNING_KEY_ALIAS','ANDROID_SIGNING_STORE_PASSWORD','ANDROID_SIGNING_KEY_PASSWORD')
$present = @($names | Where-Object { -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
if ($AllowUnsignedDryRun -and $present.Count -eq 0) {
    'CLASSSHOUT_SIGNING_REQUIRED=false' | Out-File -LiteralPath $env:GITHUB_ENV -Encoding utf8 -Append
    Write-Warning '本次明确选择未签名 dry-run，只生成验收附件，不创建发行版。'
    exit 0
}
$missing = @($names | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
if ($missing.Count -gt 0) { throw ('缺少正式发布签名 Secrets：'+($missing -join '、')+'。请配置后重试，禁止自动生成临时签名。') }
$temporaryRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\','/')
$folder = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('classshout-signing-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)))
if (-not $folder.StartsWith($temporaryRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw '签名目录越界。' }
if (Test-Path -LiteralPath $folder) { throw '签名临时目录已存在，拒绝覆盖。' }
try {
    New-Item -ItemType Directory -Path $folder | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $folder /inheritance:r /grant:r "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '无法限制签名目录访问权限。' }
    $pfx = Join-Path $folder 'windows.pfx'
    $keystore = Join-Path $folder 'android.keystore'
    try {
        [IO.File]::WriteAllBytes($pfx,[Convert]::FromBase64String($env:WINDOWS_SIGNING_PFX_BASE64))
        [IO.File]::WriteAllBytes($keystore,[Convert]::FromBase64String($env:ANDROID_SIGNING_KEYSTORE_BASE64))
    } catch { throw '签名材料无法解码为文件，请检查 Secrets 的 Base64 内容。' }
    @('CLASSSHOUT_SIGNING_REQUIRED=true',"CLASSSHOUT_SIGNING_DIRECTORY=$folder","WINDOWS_SIGNING_PFX_PATH=$pfx","ANDROID_SIGNING_KEYSTORE_PATH=$keystore") |
        Out-File -LiteralPath $env:GITHUB_ENV -Encoding utf8 -Append
    Write-Output '统一签名材料已准备，私钥仅位于受限临时目录。'
} catch {
    # 已核对绝对路径位于本次 Runner 临时目录，且名称由当前运行编号生成。
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    throw
}
