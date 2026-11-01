# JSON 行插件示例：stdout 只写协议，诊断请写 stderr；不需要 IPC 令牌。
$ErrorActionPreference='Stop'
$id=[guid]::NewGuid().ToString('N')
@{method='POST';path='/v1/shouts';body=@{id=$id;text='ClassShout 示例插件已启动。';speak=$false}} | ConvertTo-Json -Compress -Depth 5 | Write-Output
$accepted=[Console]::ReadLine() | ConvertFrom-Json
if($accepted.status -notin 200,202){exit 1}
for($n=0;$n -lt 30;$n++){
    @{method='GET';path=('/v1/receipts/'+$id);body=@{}} | ConvertTo-Json -Compress | Write-Output
    $receipt=[Console]::ReadLine() | ConvertFrom-Json
    if($receipt.body.stage -in 'displayed','completed'){exit 0}
    if($receipt.body.stage -eq 'failed'){exit 1}
    Start-Sleep -Milliseconds 200
}
exit 2
