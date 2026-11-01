using System.Net;
using System.Net.Sockets;
using ClassShout.Core.Remote;

namespace ClassShout.Classroom.ViewModels;

public partial class ClassroomViewModel : IMaintenanceContext
{
    private readonly int _maintenancePort;
    public bool IsClassroomMaintenance => true;
    public bool CanImportConfiguration => !IsSpeechLocked;
    public async Task<IReadOnlyList<DiagnosticItem>> DiagnoseAsync(CancellationToken token)
    {
        var server = RelayServerUrl;
        var items = new List<DiagnosticItem>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, _maintenancePort, deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            items.Add(new("教室", "通过", "本机接收端口可连接；仍需从教师端确认远端网络和防火墙。"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { items.Add(new("教室", "失败", "本机接收端口不可连接，请检查监听服务。")); }
        items.Add(await ConnectionDiagnostics.ProbeRelayAsync(server, deadline.Token));
        items.Add(await ConnectionDiagnostics.ProbeSpeechAsync(Services.ClassroomPlatform.HasSystemSpeech, deadline.Token));
        items.Add(await ConnectionDiagnostics.ProbeClassIslandAsync(deadline.Token));
        return items;
    }
}
