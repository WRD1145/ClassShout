using System.Text.Json;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Classroom.ViewModels;

public partial class ClassroomViewModel : IPluginManagementContext
{
    private LocalIpcSettings _extensions = SeparatedExtensionSettings.LoadIpc("classroom");
    private LocalIpcServer? _ipcServer;
    private PluginManager? _pluginManager;
    private readonly Dictionary<string, DeliveryReceipt> _extensionReceipts = [];
    private readonly Dictionary<string, (string InternalId, string Text, bool Speak)> _extensionRequests = [];
    private readonly object _extensionLock = new();
    private static readonly JsonSerializerOptions ExtensionJson = new(JsonSerializerDefaults.Web);
    public bool SupportsPlugins => true;
    public bool CanManagePlugins => !IsSpeechLocked;
    public PluginManager Plugins => _pluginManager ??= new("classroom", DispatchExtensionAsync,
        message => Post(() => AddLog("插件", message)), action => Post(action), () => CanManagePlugins);
    public bool IpcEnabled { get => _extensions.IpcEnabled; set { _extensions.IpcEnabled = value; OnPropertyChanged(); } }
    public string ExtensionDirectory => LocalSettings.Directory;
    public string ExtensionStatus { get; private set; } = "IPC 默认关闭，配置文件为 classroom-ipc.json。";

    [RelayCommand]
    private async Task ApplyExtensionsAsync()
    {
        if (IsSpeechLocked) { ExtensionStatus = "请先解锁受保护的 IPC 设置。"; OnPropertyChanged(nameof(ExtensionStatus)); return; }
        if (!LocalSettings.Save(SeparatedExtensionSettings.IpcFile("classroom"), _extensions))
        {
            ExtensionStatus = "IPC 配置保存失败，未应用。";
            OnPropertyChanged(nameof(ExtensionStatus));
            return;
        }
        // 插件与 IPC 独立启停，应用监听开关不重启插件。
        await StopIpcAsync();
        StartIpc();
    }

    private void StartExtensions()
    {
        StartIpc();
        Plugins.StartEnabled();
    }

    private void StartIpc()
    {
        if (_extensions.IpcEnabled && _ipcServer is null)
        {
            try
            {
                _ipcServer = new LocalIpcServer(_extensions.Token, DispatchExtensionAsync);
                _ipcServer.Start();
                ExtensionStatus = "IPC 已启动：127.0.0.1:45903；令牌保存在 classroom-ipc.json。";
            }
            catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
            {
                _ipcServer = null;
                ExtensionStatus = "IPC 启动失败：令牌无效或端口被占用。";
            }
        }
        else ExtensionStatus = "IPC 已关闭。";
        OnPropertyChanged(nameof(ExtensionStatus));
        AddLog("IPC", ExtensionStatus);
    }

    private Task<IpcResponse> DispatchExtensionAsync(string method, string path, string body)
        => DispatchExtensionAsync(method, path, body, "ipc:");

    private async Task<IpcResponse> DispatchExtensionAsync(string method, string path, string body, string scope)
    {
        if (method == "GET" && path == "/v1/status")
        {
            object status = new { };
            await PostAsync(() => status = new { classroom = ClassroomName, muted = IsMuted, volume = Volume, apiVersion = 1 });
            return new(200, status);
        }
        if (method == "POST" && path == "/v1/shouts")
        {
            var request = JsonSerializer.Deserialize<ExtensionShout>(body, ExtensionJson);
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 80
                || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 10000)
                return new(400, new { error = "消息需要 1–80 字符的 id 和 1–10000 字的 text。" });
            var key = scope + request.Id;
            string actual;
            lock (_extensionLock)
            {
                if (_extensionRequests.TryGetValue(key, out var old))
                    return old.Text == request.Text && old.Speak == request.Speak
                        ? new(200, new { id = request.Id, accepted = true, duplicate = true })
                        : new(409, new { error = "同一 id 不能用于不同内容。" });
                if (_extensionRequests.Count >= 1000) return new(429, new { error = "本次会话扩展消息已达 1000 条上限，请重启应用。" });
                actual = "extension-" + Guid.NewGuid().ToString("N");
                _extensionRequests[key] = (actual, request.Text, request.Speak);
                var receipt = new DeliveryReceipt { Id = request.Id };
                receipt.Apply(new("sent", DateTimeOffset.UtcNow));
                _extensionReceipts[actual] = receipt;
            }
            await PostAsync(() => PresentTextShout("本机扩展", new TextShoutMessage
            {
                Id = actual, Text = request.Text, Speak = request.Speak, Interrupt = false,
            }));
            return new(202, new { id = request.Id, accepted = true, duplicate = false });
        }
        const string receiptPath = "/v1/receipts/";
        if (method == "GET" && path.StartsWith(receiptPath, StringComparison.Ordinal))
        {
            lock (_extensionLock)
            {
                if (!_extensionRequests.TryGetValue(scope + Uri.UnescapeDataString(path[receiptPath.Length..]), out var entry))
                    return new(404, new { error = "没有该消息的本地回执。" });
                var receipt = _extensionReceipts[entry.InternalId];
                return new(200, new { id = receipt.Id, stage = receipt.Stage, failure = receipt.Failure, timeline = receipt.Timeline.ToArray() });
            }
        }
        if (method == "POST" && path == "/v1/stop" && scope == "ipc:")
        {
            await PostAsync(() => StopEverything("本机 IPC 请求停止"));
            return new(200, new { stopped = true });
        }
        return new(404, new { error = "不支持的扩展接口。" });
    }

    private bool UpdateExtensionReceipt(string id, string stage, DateTimeOffset at, string? detail)
    {
        lock (_extensionLock)
        {
            if (!_extensionReceipts.TryGetValue(id, out var receipt)) return false;
            receipt.Apply(new(stage, at, detail));
            return true;
        }
    }

    private async Task StopIpcAsync()
    {
        if (_ipcServer is { } server) { _ipcServer = null; await server.DisposeAsync(); }
    }

    private async Task StopExtensionsAsync()
    {
        await StopIpcAsync();
        if (_pluginManager is not null) await _pluginManager.DisposeAsync();
    }

    private sealed record ExtensionShout(string Id, string Text, bool Speak = false);
}
