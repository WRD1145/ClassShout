<# 统一正式签名入口；密码仅通过环境变量传递，不进入命令行或打包日志。 #>
function Initialize-ReleaseSigning {
    param([switch]$Required, [switch]$SkipAndroid)
    $script:ReleaseCertificate = $null
    $script:RemoveImportedCertificate = $false
    $script:AndroidSigningArguments = @()
    $script:AndroidCertificateDigest = $null
    $script:ReleaseSignTool = $null
    $script:ReleaseApkSigner = $null
    $windowsNames = @('WINDOWS_SIGNING_PFX_PATH','WINDOWS_SIGNING_PASSWORD')
    $androidNames = @('ANDROID_SIGNING_KEYSTORE_PATH','ANDROID_SIGNING_KEY_ALIAS','ANDROID_SIGNING_STORE_PASSWORD','ANDROID_SIGNING_KEY_PASSWORD')
    $windowsPresent = @($windowsNames | Where-Object { -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
    if ($windowsPresent.Count -eq 1) { throw '可选 Windows 签名配置不完整，请同时配置 PFX 和密码，或同时留空。' }
    $androidPresent = @($androidNames | Where-Object { -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
    if (-not $SkipAndroid -and ($Required -or $androidPresent.Count -gt 0)) {
        $missing = @($androidNames | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
        if ($missing.Count -gt 0) { throw ('正式签名配置不完整，缺少：'+($missing -join '、')+'。未退回临时或调试签名。') }
    }
    if ($windowsPresent.Count -eq 0) { Write-Warning '未配置可选 Windows 证书，桌面文件不做 Authenticode 签名。' }
    if ($windowsPresent.Count -eq 2) {
        if (-not $IsWindows) { throw 'Windows Authenticode 签名需要 Windows 打包节点。' }
        $script:ReleaseSignTool = Get-ChildItem -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        if (-not $script:ReleaseSignTool) { throw '未找到 Windows SDK signtool.exe，不能执行正式签名。' }
        $before = @(Get-ChildItem Cert:\CurrentUser\My | Select-Object -ExpandProperty Thumbprint)
        $secure = ConvertTo-SecureString $env:WINDOWS_SIGNING_PASSWORD -AsPlainText -Force
        $certificate = Import-PfxCertificate -FilePath $env:WINDOWS_SIGNING_PFX_PATH -CertStoreLocation Cert:\CurrentUser\My -Password $secure -ErrorAction Stop |
            Where-Object HasPrivateKey | Select-Object -First 1
        if (-not $certificate) { throw '签名 PFX 未包含可用私钥。' }
        $script:ReleaseCertificate = $certificate
        $script:RemoveImportedCertificate = $certificate.Thumbprint -notin $before
        if ($certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'Windows 签名证书不在有效期内。' }
        if ('1.3.6.1.5.5.7.3.3' -notin @($certificate.EnhancedKeyUsageList | ForEach-Object ObjectId)) { throw 'Windows 证书没有代码签名用途。' }
    }
    if ($SkipAndroid) { return }
    if ($androidPresent.Count -eq 0) {
        Write-Warning '未配置固定 Android 签名，仅用于本地开发验收；不能保证跨机器或跨构建覆盖升级。'
        return
    }
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
    if (-not $sdk) { throw '未配置 Android SDK，无法验证 APK 签名。' }
    $script:ReleaseApkSigner = Get-ChildItem -LiteralPath (Join-Path $sdk 'build-tools') -Filter apksigner.bat -Recurse |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $script:ReleaseApkSigner) { throw '未找到 apksigner，不能校验正式 APK。' }
    $keytool = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/keytool.exe' } else { (Get-Command keytool -ErrorAction Stop).Source }
    $details = & $keytool '-J-Duser.language=en' -list -v -keystore $env:ANDROID_SIGNING_KEYSTORE_PATH -alias $env:ANDROID_SIGNING_KEY_ALIAS '-storepass:env' ANDROID_SIGNING_STORE_PASSWORD 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Android keystore 或 alias 验证失败，请检查签名配置。' }
    $text = $details -join "`n"
    if ($text -match '(?i)CN=Android Debug') { throw '正式发布禁止使用 Android 调试证书。' }
    if ($text -notmatch '(?im)^\s*SHA256:\s*([0-9A-F:]+)') { throw '无法读取 Android 签名证书指纹。' }
    $script:AndroidCertificateDigest = $Matches[1].Replace(':','').ToLowerInvariant()
    $script:AndroidSigningArguments = @('-p:AndroidKeyStore=true',('-p:AndroidSigningKeyStore='+$env:ANDROID_SIGNING_KEYSTORE_PATH),
        ('-p:AndroidSigningKeyAlias='+$env:ANDROID_SIGNING_KEY_ALIAS),'-p:AndroidSigningStorePass=env:ANDROID_SIGNING_STORE_PASSWORD','-p:AndroidSigningKeyPass=env:ANDROID_SIGNING_KEY_PASSWORD')
}

function Sign-ReleaseWindowsFiles {
    param([Parameter(Mandatory)][string]$Directory)
    if (-not $script:ReleaseCertificate) { return }
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object { $_.Name -like 'ClassShout*' -and $_.Extension -in '.exe','.dll' })
    if ($files.Count -eq 0) { throw '签名目标目录中没有 ClassShout 可执行文件或程序集。' }
    foreach ($file in $files) {
        $result = & $script:ReleaseSignTool sign /s My /sha1 $script:ReleaseCertificate.Thumbprint /fd SHA256 /tr https://timestamp.digicert.com /td SHA256 $file.FullName 2>&1
        if ($LASTEXITCODE -ne 0) { throw ('Windows 签名或时间戳失败：'+$file.Name) }
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $script:ReleaseCertificate.Thumbprint -or -not $signature.TimeStamperCertificate) {
            throw ('Windows 签名身份、信任或时间戳校验失败：'+$file.Name)
        }
    }
    Write-Host "已用统一 Windows 证书签名并验证 $($files.Count) 个自有文件。"
}

function Assert-ReleaseApkSignature {
    param([Parameter(Mandatory)][string]$Path)
    if (-not $script:AndroidCertificateDigest) { return }
    $details = & $script:ReleaseApkSigner verify --verbose --print-certs $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'APK 签名校验失败。' }
    $text = $details -join "`n"
    # 新工具在 v3.1 签名中按 Android SDK 范围输出证书，不一定使用 Signer #1。
    $signers = [regex]::Matches($text,'(?im)^Signer (?:#(?<index>\d+)|\(minSdkVersion=\d+(?: \(dev release=true\))?, maxSdkVersion=\d+\)) certificate SHA-256 digest:[ \t]*(?<digest>[0-9a-f]{64})[ \t]*\r?$')
    $count = [regex]::Matches($text,'(?im)^Number of signers:[ \t]*(?<count>\d+)[ \t]*\r?$')
    $certificateLines = [regex]::Matches($text,'(?im)^Signer [^\r\n]+ certificate SHA-256 digest:')
    $actual = @($signers | ForEach-Object { $_.Groups['digest'].Value.ToLowerInvariant() })
    $reportedCounts = @($count | ForEach-Object { $_.Groups['count'].Value })
    $wrongIdentity = @($signers | Where-Object { ($_.Groups['index'].Success -and $_.Groups['index'].Value -ne '1') -or $_.Groups['digest'].Value.ToLowerInvariant() -ne $script:AndroidCertificateDigest }).Count -gt 0
    if ($count.Count -ne 1 -or $count[0].Groups['count'].Value -ne '1' -or $signers.Count -eq 0 -or $signers.Count -ne $certificateLines.Count -or $wrongIdentity) {
        # 仅输出公钥证书指纹，不回显密钥、密码或原始工具输出。
        Write-Host "固定 keystore 证书 SHA-256：$script:AndroidCertificateDigest"
        Write-Host "APK 证书 SHA-256：$($actual -join '、')；识别证书条目：$($signers.Count)；工具报告签名者数量：$($reportedCounts -join '、')"
        throw 'APK 签名身份与固定 keystore 不一致或签名者信息无法确认，禁止发布。'
    }
    Write-Host "APK 签名与固定 keystore 一致，验证通过（证书条目 $($signers.Count)，签名者 1，SHA-256：$script:AndroidCertificateDigest）。"
}

function Complete-ReleaseSigning {
    if ($script:ReleaseCertificate -and $script:RemoveImportedCertificate) {
        Remove-Item -LiteralPath ('Cert:\CurrentUser\My\'+$script:ReleaseCertificate.Thumbprint) -DeleteKey -Force -ErrorAction SilentlyContinue
    }
    if ($script:ReleaseCertificate) { $script:ReleaseCertificate.Dispose() }
    $script:ReleaseCertificate = $null
}
