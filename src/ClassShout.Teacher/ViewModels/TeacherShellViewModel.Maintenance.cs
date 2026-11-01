using ClassShout.Core.Remote;

namespace ClassShout.Teacher.ViewModels;

public partial class TeacherShellViewModel : IMaintenanceContext
{
    public bool IsClassroomMaintenance => false;
    public bool CanImportConfiguration => true;
    public async Task<IReadOnlyList<DiagnosticItem>> DiagnoseAsync(CancellationToken token)
    {
        var connected = IsConnected;
        var relay = IsUsingRelay;
        var server = _relaySettings.ServerUrl;
        var uuid = _relay?.BoundUuid;
        var items = new List<DiagnosticItem>
        {
            new("教室", !connected ? "未连接" : relay ? "待确认" : "通过", !connected
                ? "教师端尚未连接目标教室。" : relay ? "已建立中继绑定；需要教室送达或上屏回执确认实际执行。" : "局域网 TCP 已连接；本次诊断不发送测试喊话。"),
        };
        items.Add(await ConnectionDiagnostics.ProbeRelayAsync(server, token));
        items.Add(new("TTS", "远程项目", "语音在教室电脑执行；请在教室端诊断并试听，本机不冒充远程语音状态。"));
        if (uuid is null || !_relaySettings.IsSignedIn)
            items.Add(new("ClassIsland", "未验证", "需要登录并绑定教室，才能读取教室实时课表；局域网连接不会检查教师电脑的 ClassIsland。"));
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var snapshot = await new ClassIslandScheduleClient(_http, _relaySettings).ReadAsync(uuid, timeout.Token);
            if (_relay?.BoundUuid != uuid || _relaySettings.ServerUrl != server)
                items.Add(new("ClassIsland", "未验证", "诊断期间教室或服务器已切换，请重试。"));
            else items.Add(new("ClassIsland", snapshot.Available ? "通过" : "未通过", snapshot.Available
                ? "已通过中继读取绑定教室的实时课表。" : "无法确认教室课表可用，请检查教室在线、授权、插件和当天课表。"));
        }
        return items;
    }
}
