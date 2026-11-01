using System.Text.Json;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Classroom.ViewModels;

public partial class ClassroomViewModel
{
    private LocalExtensionSettings _extensions = LocalSettings.Load("extensions.json", static () => new LocalExtensionSettings());
    private LocalIpcServer? _ipcServer;
    private readonly List<ProcessPluginHost> _pluginHosts = [];
    private readonly Dictionary<string, DeliveryReceipt> _extensionReceipts = [];
    private readonly Dictionary<string, (string InternalId, string Text, bool Speak)> _extensionRequests = [];
    private readonly object _extensionLock = new();
    private static readonly JsonSerializerOptions ExtensionJson = new(JsonSerializerDefaults.Web);
    public bool IpcEnabled { get => _extensions.IpcEnabled; set { _extensions.IpcEnabled = value; OnPropertyChanged(); } }
    public string ExtensionDirectory => LocalSettings.Directory;
    public string ExtensionStatus { get; private set; } = "扩展默认关闭。令牌和插件清单位于本机 extensions.json。";

    [RelayCommand]
    private async Task ApplyExtensionsAsync()
    {
        var enabled = _extensions.IpcEnabled;
        _extensions = LocalSettings.Load("extensions.json", static () => new LocalExtensionSettings());
        _extensions.IpcEnabled = enabled;
        if (!LocalSettings.Save("extensions.json", _extensions))
        {
            ExtensionStatus = "扩展配置保存失败，未应用。";
            OnPropertyChanged(nameof(ExtensionStatus));
            return;
        }
        await StopExtensionsAsync();
        StartExtensions();
    }

    private void StartExtensions()
    {
        if (_extensions.IpcEnabled)
        {
            try
            {
                _ipcServer = new LocalIpcServer(_extensions.Token, DispatchExtensionAsync);
                _ipcServer.Start();
                ExtensionStatus = "IPC 已启动：127.0.0.1:45903；调用需要 extensions.json 中的令牌。";
            }
            catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
            {
                _ipcServer = null;
                ExtensionStatus = "IPC 启动失败：令牌无效或端口被占用。";
            }
        }
        else ExtensionStatus = "IPC 已关闭。";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in (_extensions.Plugins ?? []).Take(16))
        {
            if (plugin is null || !plugin.Enabled) continue;
            if (string.IsNullOrWhiteSpace(plugin.Id) || plugin.Id.Length > 64
                || plugin.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) || !ids.Add(plugin.Id))
            { AddLog("插件", "插件标识为空、过长或重复，未启动。"); continue; }
            try
            {
                var id = plugin.Id;
                // 插件只能调用自己的本地命令命名空间，不能拿到 IPC 或中继令牌。
                var host = new ProcessPluginHost(plugin, (method, path, body) => DispatchExtensionAsync(method, path, body, "plugin:" + id + ":"),
                    message => Post(() => AddLog("插件", $"{id}：{message}")));
                host.Start();
                _pluginHosts.Add(host);
                AddLog("插件", $"已启动插件：{id}。");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or System.ComponentModel.Win32Exception)
            { AddLog("插件", $"无法启动插件：{plugin.Id}，请检查可执行路径和清单。"); }
        }
        OnPropertyChanged(nameof(ExtensionStatus));
        AddLog("扩展", ExtensionStatus);
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

    private async Task StopExtensionsAsync()
    {
        if (_ipcServer is { } server) { _ipcServer = null; await server.DisposeAsync(); }
        foreach (var host in _pluginHosts) await host.DisposeAsync();
        _pluginHosts.Clear();
    }

    private sealed record ExtensionShout(string Id, string Text, bool Speak = false);
}
