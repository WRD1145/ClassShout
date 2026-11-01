using System.Diagnostics;
using System.Text.Json;
using ClassShout.Core.Net;

namespace ClassShout.Core.Remote;

/// <summary>显式启用的可信本机插件；独立进程 JSON 行协议，非安全沙箱。</summary>
public sealed class ProcessPluginHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, string, string, Task<IpcResponse>> _dispatch;
    private readonly Action<string> _log;
    private Task? _reader;
    private Task? _errors;

    public ProcessPluginHost(ProcessPluginSettings settings,
        Func<string, string, string, Task<IpcResponse>> dispatch, Action<string> log)
    {
        if (!settings.Enabled || !Path.IsPathFullyQualified(settings.Executable) || !File.Exists(settings.Executable))
            throw new InvalidOperationException("插件必须显式启用，并指定存在的绝对可执行文件路径。");
        _dispatch = dispatch;
        _log = log;
        var start = new ProcessStartInfo(settings.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(settings.Executable),
        };
        foreach (var argument in settings.Arguments ?? []) start.ArgumentList.Add(argument);
        _process = new Process { StartInfo = start };
    }

    public void Start()
    {
        if (!_process.Start()) throw new InvalidOperationException("插件进程启动失败。");
        _reader = RunAsync();
        _errors = DrainErrorsAsync();
    }

    private async Task DrainErrorsAsync()
    {
        // 持续排空 stderr，避免子进程被管道阻塞；不自动把可能含密钥的输出写入日志。
        var buffer = new char[1024];
        try { while (await _process.StandardError.ReadAsync(buffer, _stop.Token).ConfigureAwait(false) > 0) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = new System.Text.StringBuilder();
                var one = new char[1];
                while (true)
                {
                    if (await _process.StandardOutput.ReadAsync(one, _stop.Token).ConfigureAwait(false) == 0) return;
                    if (one[0] == '\n') break;
                    if (line.Length >= 8192) throw new InvalidDataException("插件输出行超过 8192 字符。");
                    line.Append(one[0]);
                }
                IpcResponse result;
                try
                {
                    var command = JsonSerializer.Deserialize<PluginCommand>(line.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    result = command is null || string.IsNullOrEmpty(command.Method) || string.IsNullOrEmpty(command.Path)
                        || command.Body.ValueKind == JsonValueKind.Undefined ? new(400, new { error = "插件命令缺少 method、path 或 body。" })
                        : await _dispatch(command.Method, command.Path, command.Body.GetRawText()).WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
                }
                catch (JsonException) { result = new(400, new { error = "插件 JSON 命令无效。" }); }
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { status = result.Status, body = result.Body },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)).AsMemory(), _stop.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        { _log("插件通信已停止，请检查协议或执行超时。"); }
        catch (Exception) { _log("插件执行失败，已停止该插件。"); }
        finally
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _log("插件进程已退出；不会自动重启。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        foreach (var task in new[] { _reader, _errors }) if (task is not null) await task.ConfigureAwait(false);
        _process.Dispose();
        _stop.Dispose();
    }

    private sealed record PluginCommand(string Method, string Path, JsonElement Body);
}
