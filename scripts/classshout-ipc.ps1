<# 本机教师端或教室端 IPC 客户端；令牌只从配置文件读取，不输出到终端。 #>
[CmdletBinding(DefaultParameterSetName='Status')]
param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'ClassShout'),
    [ValidateSet('Classroom','Teacher')][string]$Role = 'Classroom',
    [Parameter(Mandatory,ParameterSetName='Send')][string]$Text,
    [Parameter(ParameterSetName='Send')][string]$Id = ([guid]::NewGuid().ToString('N')),
    [Parameter(ParameterSetName='Send')][switch]$Speak,
    [Parameter(Mandatory,ParameterSetName='Receipt')][string]$Receipt,
    [Parameter(Mandatory,ParameterSetName='Stop')][switch]$Stop
)
$ErrorActionPreference='Stop'
$settingsFile=if($Role -eq 'Teacher'){'teacher-extensions.json'}else{'extensions.json'}
$port=if($Role -eq 'Teacher'){45904}else{45903}
$settings=Get-Content -LiteralPath (Join-Path $DataDirectory $settingsFile) -Raw | ConvertFrom-Json
if(-not $settings.IpcEnabled){throw '当前端尚未启用本机 IPC。'}
$request=@{Uri='http://127.0.0.1:45903/v1/status';Method='Get';NoProxy=$true;TimeoutSec=10;Headers=@{Authorization='Bearer '+$settings.Token}}
switch($PSCmdlet.ParameterSetName){
    'Send' {$request.Uri='http://127.0.0.1:45903/v1/shouts';$request.Method='Post';$request.ContentType='application/json; charset=utf-8';$request.Body=[Text.Encoding]::UTF8.GetBytes((@{id=$Id;text=$Text;speak=[bool]$Speak}|ConvertTo-Json))}
    'Receipt' {$request.Uri='http://127.0.0.1:45903/v1/receipts/'+[Uri]::EscapeDataString($Receipt)}
    'Stop' {$request.Uri='http://127.0.0.1:45903/v1/stop';$request.Method='Post'}
}
$request.Uri=$request.Uri.Replace(':45903',(':'+$port))
Invoke-RestMethod @request | ConvertTo-Json -Depth 12
