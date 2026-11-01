using System.Text.Json;
using Avalonia.Threading;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

public partial class TeacherShellViewModel
{
    private LocalExtensionSettings _teacherExtensions = LocalSettings.Load("teacher-extensions.json", static () => new LocalExtensionSettings());
    private LocalIpcServer? _teacherIpc;
    private readonly List<ProcessPluginHost> _teacherPlugins = [];
    private readonly Dictionary<string, (string Id, string Text, bool Speak)> _teacherExtensionIds = [];
    public bool SupportsTeacherExtensions => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
    public bool TeacherIpcEnabled { get => _teacherExtensions.IpcEnabled; set { _teacherExtensions.IpcEnabled = value; OnPropertyChanged(); } }
    public string TeacherExtensionStatus { get; private set; } = "桌面扩展默认关闭；配置文件为 teacher-extensions.json。";
    public string TeacherExtensionDirectory => LocalSettings.Directory;

    public void StartTeacherExtensions()
    {
        if (!SupportsTeacherExtensions) return;
        try
        {
            if (_teacherExtensions.IpcEnabled)
            {
                _teacherIpc = new LocalIpcServer(_teacherExtensions.Token, (m, p, b) => DispatchTeacherExtensionAsync(m, p, b, "ipc:"), 45904);
                _teacherIpc.Start();
            }
            TeacherExtensionStatus = _teacherIpc is null ? "教师端 IPC 已关闭。" : "教师端 IPC 已启动：127.0.0.1:45904。";
        }
        catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
        { _teacherIpc = null; TeacherExtensionStatus = "教师端 IPC 启动失败：令牌无效或端口被占用。"; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in (_teacherExtensions.Plugins ?? []).Take(16))
        {
            if (plugin is null || !plugin.Enabled) continue;
            if (string.IsNullOrWhiteSpace(plugin.Id) || plugin.Id.Length > 64 || !ids.Add(plugin.Id)
                || plugin.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) continue;
            try
            {
                var host = new ProcessPluginHost(plugin, (m, p, b) => DispatchTeacherExtensionAsync(m, p, b, "plugin:" + plugin.Id + ":"),
                    message => Post(() => AddLog($"插件 {plugin.Id}：{message}")));
                host.Start();
                _teacherPlugins.Add(host);
                AddLog($"教师端插件已启动：{plugin.Id}。");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { AddLog($"教师端插件启动失败：{plugin.Id}，请检查清单和可执行路径。"); }
        }
        OnPropertyChanged(nameof(TeacherExtensionStatus));
        AddLog(TeacherExtensionStatus);
    }

    [RelayCommand]
    private async Task ApplyTeacherExtensionsAsync()
    {
        if (!SupportsTeacherExtensions) return;
        var enabled = _teacherExtensions.IpcEnabled;
        _teacherExtensions = LocalSettings.Load("teacher-extensions.json", static () => new LocalExtensionSettings());
        _teacherExtensions.IpcEnabled = enabled;
        if (!LocalSettings.Save("teacher-extensions.json", _teacherExtensions))
        { TeacherExtensionStatus = "教师端扩展配置保存失败。"; OnPropertyChanged(nameof(TeacherExtensionStatus)); return; }
        await StopTeacherExtensionsAsync();
        StartTeacherExtensions();
    }

    private async Task StopTeacherExtensionsAsync()
    {
        if (_teacherIpc is { } server) { _teacherIpc = null; await server.DisposeAsync(); }
        foreach (var plugin in _teacherPlugins) await plugin.DisposeAsync();
        _teacherPlugins.Clear();
    }

    private async Task<IpcResponse> DispatchTeacherExtensionAsync(string method, string path, string body, string scope)
    {
        // 全部状态读写在 UI 线程执行；网络和插件线程不能直接访问消息列表。
        return await Dispatcher.UIThread.InvokeAsync(() => ExecuteTeacherExtensionAsync(method, path, body, scope));
    }

    private async Task<IpcResponse> ExecuteTeacherExtensionAsync(string method, string path, string body, string scope)
    {
        if (method == "GET" && path == "/v1/status")
            return new(200, new { connected = IsConnected, classroom = ClassroomName, apiVersion = 1, role = "teacher" });
        if (method == "POST" && path == "/v1/shouts")
        {
            var request = JsonSerializer.Deserialize<TeacherExtensionShout>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 80
                || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 10000)
                return new(400, new { error = "需要有效的 id（最多 80 字符）和 text（最多 10000 字）。" });
            var key = scope + request.Id;
            if (_teacherExtensionIds.TryGetValue(key, out var old))
                return old.Text == request.Text && old.Speak == request.Speak
                    ? new(200, new { id = request.Id, accepted = true, duplicate = true })
                    : new(409, new { error = "同一消息标识不能用于不同内容。" });
            if (!_transport.IsConnected) return new(409, new { error = "教师端尚未连接教室。" });
            if (_teacherExtensionIds.Count >= 1000) return new(429, new { error = "本次运行已达 1000 条扩展消息上限。" });
            var message = new TextShoutMessage { Id = MessageIds.New(), Text = request.Text, Speak = request.Speak, Interrupt = false };
            _teacherExtensionIds[key] = (message.Id, request.Text, request.Speak);
            var accepted = await _transport.SendTextAsync(message);
            return new(accepted ? 202 : 502, accepted
                ? new { id = request.Id, accepted = true, duplicate = false }
                : (object)new { error = "发送失败，请查询回执查看原因。" });
        }
        const string prefix = "/v1/receipts/";
        if (method == "GET" && path.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (!_teacherExtensionIds.TryGetValue(scope + Uri.UnescapeDataString(path[prefix.Length..]), out var entry))
                return new(404, new { error = "没有该扩展消息。" });
            var receipt = Deliveries.FirstOrDefault(item => item.Receipt.Id == entry.Id)?.Receipt;
            return receipt is null ? new(410, new { error = "回执已从最近 100 条记录中移除。" })
                : new(200, new { id = Uri.UnescapeDataString(path[prefix.Length..]), stage = receipt.Stage, failure = receipt.Failure, timeline = receipt.Timeline.ToArray() });
        }
        if (method == "POST" && path == "/v1/stop" && scope == "ipc:")
        {
            if (!_transport.IsConnected) return new(409, new { error = "教师端尚未连接教室。" });
            await _transport.RequestStopAsync("教师端本机 IPC 请求停止");
            return new(200, new { requested = true });
        }
        return new(404, new { error = "不支持的教师端扩展接口。" });
    }

    private sealed record TeacherExtensionShout(string Id, string Text, bool Speak = false);
}
