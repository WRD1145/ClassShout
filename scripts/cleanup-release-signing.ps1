<# 签名后清理本次流水线私钥文件，不触及其他目录。 #>
$ErrorActionPreference = 'Stop'
if (-not $env:CLASSSHOUT_SIGNING_DIRECTORY) { exit 0 }
if (-not $env:RUNNER_TEMP) { throw '缺少 Runner 临时目录，拒绝清理。' }
$temporaryRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\','/')
$folder = [IO.Path]::GetFullPath($env:CLASSSHOUT_SIGNING_DIRECTORY)
$expected = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('classshout-signing-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)))
if (-not $folder.StartsWith($temporaryRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $folder -ne $expected) { throw '签名目录与本次运行不匹配，拒绝递归删除。' }
if (Test-Path -LiteralPath $folder) {
    if ((Get-Item -LiteralPath $folder).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '签名临时目录被替换为链接，拒绝清理。' }
    Remove-Item -LiteralPath $folder -Recurse -Force
}
Write-Output '本次签名私钥临时文件已清理。'
