using System.Net;
using System.Net.Sockets;
using ClassShout.Core.Audio;
using ClassShout.Core.Protocol;

namespace ClassShout.Core.Net;

/// <summary>
/// 教师端 TCP 客户端：连接教室端，发送文字喊话与语音流。
/// </summary>
public sealed class TeacherClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _receiveLoop;

    /// <summary>收到教室端控制消息（状态、回执、错误）。</summary>
    public event Action<ShoutMessage>? ControlReceived;

    /// <summary>运行日志。</summary>
    public event Action<string>? Log;

    /// <summary>连接断开，参数为原因。</summary>
    public event Action<string>? Disconnected;

    /// <summary>当前是否处于已连接状态。</summary>
    public bool IsConnected => _stream is not null && _client?.Connected == true;

    /// <summary>握手后教室端回报的名称。</summary>
    public string? ClassroomName { get; private set; }

    /// <summary>已连接教室的地址。</summary>
    public string? Endpoint { get; private set; }

    /// <summary>连接并在后台开始收包。</summary>
    public async Task ConnectAsync(IPEndPoint endpoint, string clientName, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };

        try
        {
            await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 连接失败必须把这个 TcpClient 释放掉。
            //
            // 它在成功之前不会被赋给 _client，于是 DisconnectAsync 也管不到它 ——
            // 每次连接超时就漏一个 socket。用户在教室里反复点"连接"，
            // 漏掉的句柄会一直堆着，直到本机可用端口被耗尽，
            // 那时候连浏览器都打不开网页，而没人会想到是这里。
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        _cts = new CancellationTokenSource();
        Endpoint = endpoint.ToString();

        try
        {
            await SendAsync(new HelloMessage
            {
                ClientId = Guid.NewGuid().ToString("N"),
                ClientName = clientName,
                ProtocolVersion = ShoutProtocol.Version,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 握手发不出去：把这半条连接收干净，别留一个"看起来连着"的对象
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stream, _cts.Token));
        Log?.Invoke($"已连接教室端 {endpoint}");
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var reason = "连接已关闭";

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await FrameProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.Kind != FrameKind.Control)
                {
                    continue;
                }

                var message = ShoutCodec.Decode(frame.Value.Payload);
                if (message is null)
                {
                    continue;
                }

                if (message is StatusMessage status && !string.IsNullOrWhiteSpace(status.ClassroomName))
                {
                    ClassroomName = status.ClassroomName;
                }
                else if (message is ErrorMessage error)
                {
                    reason = $"教室端报错：{error.Message}";
                }

                ControlReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException ex)
        {
            reason = $"网络中断：{ex.Message}";
        }
        catch (InvalidDataException ex)
        {
            reason = $"协议错误：{ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Disconnected?.Invoke(reason);
            }
        }
    }

    /// <summary>发送文字喊话。</summary>
    public Task SendTextAsync(TextShoutMessage message, CancellationToken cancellationToken = default)
    {
        message.Id = string.IsNullOrEmpty(message.Id) ? Guid.NewGuid().ToString("N") : message.Id;
        return SendAsync(message, cancellationToken);
    }

    /// <summary>开始一段语音喊话。</summary>
    public Task SendAudioStartAsync(string id, AudioFormat format, CancellationToken cancellationToken = default)
        => SendAsync(new AudioStartMessage
        {
            Id = id,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = format.BitsPerSample,
        }, cancellationToken);

    /// <summary>发送一段 PCM。</summary>
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null || pcm.IsEmpty)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameProtocol.WriteAsync(stream, FrameKind.Audio, pcm, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>结束语音喊话。</summary>
    public Task SendAudioEndAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync(new AudioEndMessage { Id = id }, cancellationToken);

    /// <summary>发送一段图片字节。图片本身走 <see cref="FrameKind.Image"/> 帧，与控制消息分开。</summary>
    private async Task SendImageChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null || chunk.IsEmpty)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameProtocol.WriteAsync(stream, FrameKind.Image, chunk, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 发一条图片喊话：先声明（带大小与说明文字），再切成片，最后收尾。
    ///
    /// 分片而不是整张塞进一条 JSON：单帧上限 1 MiB，而 base64 还要再膨胀三分之一，
    /// 一张手机照片必然超；而且教室里那台电脑可以边收边拼，
    /// 不必等一个几十兆的 JSON 解析完才开始有反应。
    /// </summary>
    /// <param name="message">图片喊话的说明部分。</param>
    /// <param name="image">图片原始字节。</param>
    /// <param name="chunkSize">每片多少字节。默认 48 KiB。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task SendImageAsync(
        ImageStartMessage message,
        ReadOnlyMemory<byte> image,
        int chunkSize = ShoutProtocol.DefaultImageChunkSize,
        CancellationToken cancellationToken = default)
    {
        message.Id = string.IsNullOrEmpty(message.Id) ? Guid.NewGuid().ToString("N") : message.Id;
        message.TotalBytes = image.Length;

        await SendAsync(message, cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < image.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, image.Length - offset);
            await SendImageChunkAsync(image.Slice(offset, length), cancellationToken).ConfigureAwait(false);
        }

        await SendAsync(new ImageEndMessage { Id = message.Id }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>要求教室端停止当前播放/朗读。</summary>
    public Task SendStopAsync(string reason = "教师端中止", CancellationToken cancellationToken = default)
        => SendAsync(new StopMessage { Reason = reason }, cancellationToken);

    private async Task SendAsync(ShoutMessage message, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameProtocol.WriteAsync(stream, FrameKind.Control, ShoutCodec.Encode(message), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>断开连接。</summary>
    public async Task DisconnectAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            if (_stream is not null)
            {
                await SendAsync(new ByeMessage(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
            // 断开时发送失败可忽略
        }

        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        Endpoint = null;

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
        }

        _receiveLoop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
