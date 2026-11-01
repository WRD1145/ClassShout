using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassShout.Core.Net;

/// <summary>本机 HTTP IPC；单请求连接、有限资源、独立令牌，不开放浏览器跨站调用。</summary>
public sealed class LocalIpcServer : IAsyncDisposable
{
    public const int DefaultPort = 45903;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(16);
    private readonly byte[] _tokenHash;
    private readonly Func<string, string, string, Task<IpcResponse>> _dispatch;
    private Task? _loop;

    public LocalIpcServer(string token, Func<string, string, string, Task<IpcResponse>> dispatch, int port = DefaultPort)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 32) throw new ArgumentException("IPC 令牌至少需要 32 个字符。");
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _dispatch = dispatch;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public void Start() { _listener.Start(); _loop = AcceptAsync(); }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { break; }
            if (!_slots.Wait(0)) { client.Dispose(); continue; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var token = deadline.Token;
            try
            {
                var stream = client.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0) return;
                    header.WriteByte(one[0]);
                    if (header.Length > 8192) { await ReplyAsync(stream, new(400, new { error = "请求头过大。" }), token); return; }
                    var data = header.GetBuffer();
                    var n = (int)header.Length;
                    if (n >= 4 && data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
                        throw new InvalidDataException("请求头无效或重复。");
                }
                if (first.Length != 3 || first[2] != "HTTP/1.1" || headers.ContainsKey("Origin")
                    || headers.ContainsKey("Transfer-Encoding")
                    || !headers.TryGetValue("Host", out var host)
                    || !(host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("仅接受本机原生 HTTP 请求。");
                if (!headers.TryGetValue("Authorization", out var authorization) || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(_tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
                { await ReplyAsync(stream, new(401, new { error = "IPC 令牌无效。" }), token); return; }
                var length = 0;
                if (headers.TryGetValue("Content-Length", out var lengthText)
                    && (!int.TryParse(lengthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) || length > 32768))
                    throw new InvalidDataException("正文长度无效或超过 32 KiB。");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
                var response = await _dispatch(first[0], first[1], Encoding.UTF8.GetString(body)).WaitAsync(token).ConfigureAwait(false);
                await ReplyAsync(stream, response, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                try { await ReplyAsync(client.GetStream(), new(400, new { error = "IPC 请求格式无效。" }), token); }
                catch (Exception nested) when (nested is IOException or OperationCanceledException or SocketException) { }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            catch (Exception)
            {
                try { await ReplyAsync(client.GetStream(), new(500, new { error = "IPC 执行失败，请检查教室日志。" }), token); }
                catch (Exception) { }
            }
            finally { _slots.Release(); }
        }
    }

    private static async Task ReplyAsync(NetworkStream stream, IpcResponse response, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Result\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        // 在途连接仍会退出并释放信号量，不能提前释放这些对象。
    }
}

public sealed record IpcResponse(int Status, object Body);
