<# 验证无真实私钥时的发布保护，不生成或使用临时签名身份。 #>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$state = Join-Path $repo ('artifacts/signing-policy-'+[guid]::NewGuid().ToString('N'))
$runner = Join-Path $state 'runner'
$outside = Join-Path $state 'outside'
New-Item -ItemType Directory -Path $runner,$outside -Force | Out-Null
$environmentFile = Join-Path $state 'github-env.txt'
[IO.File]::WriteAllText($environmentFile,'')
$names = @('GITHUB_ENV','RUNNER_TEMP','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','CLASSSHOUT_SIGNING_DIRECTORY','WINDOWS_SIGNING_PFX_BASE64','WINDOWS_SIGNING_PASSWORD','ANDROID_SIGNING_KEYSTORE_BASE64','ANDROID_SIGNING_KEY_ALIAS','ANDROID_SIGNING_STORE_PASSWORD','ANDROID_SIGNING_KEY_PASSWORD','WINDOWS_SIGNING_PFX_PATH','ANDROID_SIGNING_KEYSTORE_PATH','ANDROID_SIGNING_ALIAS','ANDROID_KEYSTORE_PASSWORD','ANDROID_KEY_PASSWORD')
$prior = @{}
$failures = 0
function Check([string]$title,[bool]$passed) {
    if(-not $passed){$script:failures++}
    Write-Output "[$(if($passed){'通过'}else{'失败'})] 签名策略：$title"
}
try {
    foreach($name in $names){$prior[$name]=[Environment]::GetEnvironmentVariable($name);[Environment]::SetEnvironmentVariable($name,$null)}
    $env:GITHUB_ENV=$environmentFile;$env:RUNNER_TEMP=$runner;$env:GITHUB_RUN_ID='123';$env:GITHUB_RUN_ATTEMPT='1'
    . (Join-Path $PSScriptRoot 'release-signing.ps1')
    $rejected=$false
    try {Initialize-ReleaseSigning -Required}catch{$rejected=$_.Exception.Message -like '正式签名配置不完整*'}
    Check '缺失正式材料时拒绝降级' $rejected
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-release-signing.ps1') 2>&1
    Check 'CI 默认拒绝没有材料的发布' ($LASTEXITCODE -ne 0)
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-release-signing.ps1') -AllowUnsignedDryRun 2>&1
    Check '显式 dry-run 只标记未签名验收' ($LASTEXITCODE -eq 0 -and (Get-Content $environmentFile -Raw).Contains('CLASSSHOUT_SIGNING_REQUIRED=false'))
    $env:WINDOWS_SIGNING_PASSWORD='SIGNING_SECRET_CANARY'
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-release-signing.ps1') -AllowUnsignedDryRun 2>&1
    Check '部分配置不能退回未签名包' ($LASTEXITCODE -ne 0)
    Check '异常不回显密码内容' (-not (($log -join "`n").Contains('SIGNING_SECRET_CANARY')))
    $env:WINDOWS_SIGNING_PASSWORD=$null
    $env:ANDROID_SIGNING_KEYSTORE_BASE64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('仅用于验证材料准备，不是真实私钥'))
    $env:ANDROID_SIGNING_KEY_ALIAS='fixture'
    $env:ANDROID_SIGNING_STORE_PASSWORD='FIXTURE_STORE'
    $env:ANDROID_SIGNING_KEY_PASSWORD='FIXTURE_KEY'
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-release-signing.ps1') 2>&1
    $owned=Join-Path $runner 'classshout-signing-123-1'
    Check 'Android 配置完整时无需 Windows 证书' ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $owned 'android.keystore')) -and -not (Test-Path -LiteralPath (Join-Path $owned 'windows.pfx')))
    $env:CLASSSHOUT_SIGNING_DIRECTORY=$owned
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'cleanup-release-signing.ps1') 2>&1
    if($LASTEXITCODE -ne 0){throw '材料准备测试的清理失败。'}
    $desktopAllowed=$true
    try {Initialize-ReleaseSigning -Required -SkipAndroid}catch{$desktopAllowed=$false}
    Check '仅桌面打包不强制要求 Windows 证书' $desktopAllowed
    $env:CLASSSHOUT_SIGNING_DIRECTORY=$outside
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'cleanup-release-signing.ps1') 2>&1
    Check '清理拒绝 Runner 目录以外的路径' ($LASTEXITCODE -ne 0 -and (Test-Path -LiteralPath $outside))
    $owned=Join-Path $runner 'classshout-signing-123-1';New-Item -ItemType Directory -Path $owned|Out-Null
    $env:CLASSSHOUT_SIGNING_DIRECTORY=$owned
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'cleanup-release-signing.ps1') 2>&1
    Check '仅清理本次运行的受限目录' ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath $owned) -and (Test-Path -LiteralPath $outside))
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$prior[$name])}
}
if($failures -gt 0){exit 1}
exit 0
