<# 将含 manifest.json 的目录打包为 .cspx；安装仍会执行宿主安全预检。 #>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '../artifacts/plugin-example'),
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/classshout.hello.cspx')
)
$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($Source)
$outputPath = [IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'manifest.json') -PathType Leaf)) { throw '源目录缺少 manifest.json。' }
if ($outputPath.StartsWith($sourcePath.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw '输出文件不能位于插件源目录内。' }
if (Test-Path -LiteralPath $outputPath) { throw '输出文件已存在，请换一个名称，避免覆盖已有插件包。' }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($outputPath)) -Force | Out-Null
[IO.Compression.ZipFile]::CreateFromDirectory($sourcePath,$outputPath,[IO.Compression.CompressionLevel]::Optimal,$false)
Write-Output "插件包已生成：$outputPath；安装后默认禁用。"
