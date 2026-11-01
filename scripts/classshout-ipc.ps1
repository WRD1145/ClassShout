<# 本机教室端 IPC 客户端；令牌只从配置文件读取，不输出到终端。 #>
[CmdletBinding(DefaultParameterSetName='Status')]
param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'ClassShout'),
    [Parameter(Mandatory,ParameterSetName='Send')][string]$Text,
    [Parameter(ParameterSetName='Send')][string]$Id = ([guid]::NewGuid().ToString('N')),
    [Parameter(ParameterSetName='Send')][switch]$Speak,
    [Parameter(Mandatory,ParameterSetName='Receipt')][string]$Receipt,
    [Parameter(Mandatory,ParameterSetName='Stop')][switch]$Stop
)
$ErrorActionPreference='Stop'
$settings=Get-Content -LiteralPath (Join-Path $DataDirectory 'extensions.json') -Raw | ConvertFrom-Json
if(-not $settings.IpcEnabled){throw '教室端尚未启用本机 IPC。'}
$request=@{Uri='http://127.0.0.1:45903/v1/status';Method='Get';NoProxy=$true;TimeoutSec=10;Headers=@{Authorization='Bearer '+$settings.Token}}
switch($PSCmdlet.ParameterSetName){
    'Send' {$request.Uri='http://127.0.0.1:45903/v1/shouts';$request.Method='Post';$request.ContentType='application/json; charset=utf-8';$request.Body=[Text.Encoding]::UTF8.GetBytes((@{id=$Id;text=$Text;speak=[bool]$Speak}|ConvertTo-Json))}
    'Receipt' {$request.Uri='http://127.0.0.1:45903/v1/receipts/'+[Uri]::EscapeDataString($Receipt)}
    'Stop' {$request.Uri='http://127.0.0.1:45903/v1/stop';$request.Method='Post'}
}
Invoke-RestMethod @request | ConvertTo-Json -Depth 12
