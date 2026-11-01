# 启动隔离教室，验证教师端 IPC 和插件确实经过网络收到上屏回执。
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$state=[IO.Path]::GetFullPath((Join-Path $temporaryRoot ('cs-teacher-extensions-'+[guid]::NewGuid().ToString('N'))))
if(-not $state.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase)){throw '验收目录超出临时根目录。'}
New-Item -ItemType Directory -Path $state|Out-Null
$previousData=$env:CLASSSHOUT_DATA_DIR
$previousLog=$env:CLASSSHOUT_LOG_DIR
$app=$null
$result=1
try{
    $env:CLASSSHOUT_DATA_DIR=$state
    $env:CLASSSHOUT_LOG_DIR=Join-Path $state 'logs'
    @{AutoConnect=$false;ClassroomName='教师扩展回归教室'}|ConvertTo-Json|Set-Content (Join-Path $state 'classroom.json')
    @{Enabled=$true;DefaultDisplay='window';DefaultHoldMs=20000}|ConvertTo-Json|Set-Content (Join-Path $state 'classroom-notification.json')
    $exe=Join-Path $repoRoot 'src/ClassShout.Classroom/bin/Release/net10.0-windows/ClassShout.Classroom.exe'
    $app=Start-Process $exe -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 4
    if($app.HasExited){throw '隔离教室端启动后退出。'}
    $test=Join-Path $repoRoot 'tools/ClassShout.DesignPreview/bin/Release/net10.0-windows/ClassShout.DesignPreview.exe'
    & $test --teacher-extension-selftest
    $result=$LASTEXITCODE
}finally{
    if($app -and -not $app.HasExited){Stop-Process -Id $app.Id;$app.WaitForExit(5000)|Out-Null}
    $env:CLASSSHOUT_DATA_DIR=$previousData
    $env:CLASSSHOUT_LOG_DIR=$previousLog
    # 只清理本脚本新建且已核对边界的隔离状态目录。
    if($state.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase)){
        Remove-Item -LiteralPath $state -Recurse -Force
    }
}
exit $result
