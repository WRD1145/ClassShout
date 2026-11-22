<# 在 Windows 节点解开更新包，验证清单并启动真实 GUI，不能只验证发布命令退出码。 #>
[CmdletBinding()]
param([string]$Assets = 'dist/release', [string]$Version = $env:EXPECTED_VERSION, [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '本项验收必须在 Windows 节点运行。' }
$state = Join-Path ([IO.Path]::GetTempPath()) ('classshout-update-smoke-' + [guid]::NewGuid().ToString('N'))
$prior = $env:CLASSSHOUT_DATA_DIR
New-Item -ItemType Directory -Path $state | Out-Null
try {
    foreach ($role in @('Classroom', 'Teacher')) {
        $folder = Join-Path $state $role
        Expand-Archive -LiteralPath (Join-Path $Assets "ClassShout.$role-$Runtime-update.zip") -DestinationPath $folder
        $manifest = Get-Content (Join-Path $folder 'classshout-update.json') -Raw | ConvertFrom-Json
        if ($manifest.runtime -ne $Runtime -or $manifest.format -ne 1 -or $manifest.appId -ne $role.ToLowerInvariant() -or ($Version -and $manifest.version -ne $Version)) { throw '更新包平台或格式不正确。' }
        foreach ($file in $manifest.files) {
            $path = Join-Path $folder $file.path
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "更新文件校验失败：$($file.path)" }
        }
        $env:CLASSSHOUT_DATA_DIR = Join-Path $state "$role-data"
        $stderr = Join-Path $state "$role.stderr.log"
        $process = Start-Process -FilePath (Join-Path $folder $manifest.entryPoint) -WorkingDirectory $folder -PassThru `
            -RedirectStandardOutput (Join-Path $state "$role.stdout.log") -RedirectStandardError $stderr
        try {
            Start-Sleep -Seconds 8
            $process.Refresh()
            if ($process.HasExited) { Get-Content $stderr; throw "$role 更新包启动失败，退出码：$($process.ExitCode)" }
            Write-Host "[通过] $role 更新包校验与真实启动：运行 8 秒未退出。"
        }
        finally {
            $process.Refresh()
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
        }
    }
}
finally {
    $env:CLASSSHOUT_DATA_DIR = $prior
    Remove-Item -LiteralPath $state -Recurse -Force -ErrorAction SilentlyContinue
}
