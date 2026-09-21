using System.Net;
using System.Net.Sockets;
using ClassShout.Core.Protocol;

namespace ClassShout.Core.Net;

/// <summary>
/// 教室端视角下的一条教师端连接。
/// 所有上行内容（文字、语音）以事件形式抛出，由界面层决定如何处理。
/// </summary>
public sealed class TeacherSession
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private NetworkStream? _stream;

    internal TeacherSession(string id, string remoteEndPoint)
    {
        Id = id;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>连接标识（进程内唯一）。</summary>
    public string Id { get; }

    /// <summary>对端地址，用于日志显示。</summary>
    public string RemoteEndPoint { get; }

    /// <summary>握手后得到的教师端名称。</summary>
    public string ClientName { get; private set; } = "未知教师端";

    /// <summary>是否已通过协议版本握手。</summary>
    public bool IsHandshaken { get; private set; }

    /// <summary>收到文字喊话。</summary>
    public event EventHandler<TextShoutMessage>? TextShoutReceived;

    /// <summary>语音流开始。</summary>
    public event EventHandler<AudioStartMessage>? AudioStarted;

    /// <summary>收到一段 PCM。</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? AudioChunkReceived;

    /// <summary>语音流结束。</summary>
    public event EventHandler<AudioEndMessage>? AudioEnded;

    /// <summary>对方要求停止播放。</summary>
    public event EventHandler<StopMessage>? StopRequested;

    /// <summary>连接断开（无论正常或异常）。</summary>
    public event EventHandler<string>? Closed;

    /// <summary>
    /// 握手完成。
    /// 单独暴露这个事件，是为了让「收到 hello 之后要回一条状态」成为协议行为，
    /// 而不是某个界面层的自发动作 —— 否则直接使用 ClassroomServer 的消费方
    /// 会拿不到握手确认。
    /// </summary>
    public event EventHandler? Handshaken;

    internal void Attach(NetworkStream stream) => _stream = stream;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("会话尚未绑定网络流。");
        var closeReason = "对端已断开";

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await FrameProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Value.Kind)
                {
                    case FrameKind.Control:
                        HandleControl(frame.Value.Payload);
                        break;

                    case FrameKind.Audio:
                        AudioChunkReceived?.Invoke(this, frame.Value.Payload);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            closeReason = "连接已关闭";
        }
        catch (IOException ex)
        {
            closeReason = $"网络中断：{ex.Message}";
        }
        catch (InvalidDataException ex)
        {
            closeReason = $"协议错误：{ex.Message}";
        }
        finally
        {
            Closed?.Invoke(this, closeReason);
        }
    }

    private void HandleControl(byte[] payload)
    {
        var message = ShoutCodec.Decode(payload);
        switch (message)
        {
            case HelloMessage hello:
                ClientName = string.IsNullOrWhiteSpace(hello.ClientName) ? "未命名教师端" : hello.ClientName;
                IsHandshaken = hello.ProtocolVersion == ShoutProtocol.Version;
                Handshaken?.Invoke(this, EventArgs.Empty);
                break;

            case TextShoutMessage text:
                TextShoutReceived?.Invoke(this, text);
                break;

            case AudioStartMessage start:
                AudioStarted?.Invoke(this, start);
                break;

            case AudioEndMessage end:
                AudioEnded?.Invoke(this, end);
                break;

            case StopMessage stop:
                StopRequested?.Invoke(this, stop);
                break;

            case ByeMessage:
                break;
        }
    }

    /// <summary>向该教师端发送一条控制消息。发送失败不会抛出，只返回 false。</summary>
    public async Task<bool> SendAsync(ShoutMessage message, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null)
        {
            return false;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameProtocol.WriteAsync(stream, FrameKind.Control, ShoutCodec.Encode(message), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal void Close()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (IOException)
        {
            // 关闭时的异常无需关心
        }
    }
}

/// <summary>
/// 教室端 TCP 服务：监听教师端连接，把每条连接包装成 <see cref="TeacherSession"/>。
/// </summary>
public sealed class ClassroomServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TeacherSession> _sessions = [];
    private readonly int _port;

    private TcpListener? _listener;
    private Task? _acceptLoop;

    public ClassroomServer(int port = ShoutProtocol.DefaultTcpPort)
    {
        _port = port;
    }

    /// <summary>教室端名称，握手后会通过状态消息回给教师端。</summary>
    public string ClassroomName { get; set; } = "教室";

    /// <summary>
    /// 握手后回给教师端的状态消息。
    /// 应用层可以覆盖它，把静音、音量等运行态一并带上；
    /// 不设置时回一条只含教室名的默认状态。
    /// </summary>
    public Func<StatusMessage>? StatusFactory { get; set; }

    /// <summary>新教师端接入。</summary>
    public event EventHandler<TeacherSession>? SessionOpened;

    /// <summary>教师端断开。</summary>
    public event EventHandler<TeacherSession>? SessionClosed;

    /// <summary>运行日志，供界面直接显示。</summary>
    public event Action<string>? Log;

    public int Port => _port;

    public IReadOnlyList<TeacherSession> Sessions => _sessions;

    /// <summary>开始监听。端口被占用会抛出 <see cref="SocketException"/>。</summary>
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _listener = listener;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        Log?.Invoke($"教室端已监听 0.0.0.0:{_port}，等待教师端连接。");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log?.Invoke($"接受连接失败：{ex.Message}");
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "未知地址";
        var session = new TeacherSession(Guid.NewGuid().ToString("N")[..8], remote);
        session.Attach(client.GetStream());

        _sessions.Add(session);
        Log?.Invoke($"教师端接入：{remote}");
        SessionOpened?.Invoke(this, session);

        session.Handshaken += (_, _) =>
        {
            var status = StatusFactory?.Invoke() ?? new StatusMessage
            {
                State = "idle",
                ClassroomName = ClassroomName,
            };

            _ = session.SendAsync(status);
        };

        try
        {
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessions.Remove(session);
            session.Close();
            client.Dispose();
            Log?.Invoke($"教师端断开：{session.ClientName}（{remote}）");
            SessionClosed?.Invoke(this, session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _listener = null;

        foreach (var session in _sessions.ToArray())
        {
            session.Close();
        }

        _sessions.Clear();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
        }

        _acceptLoop = null;
        _cts.Dispose();
    }
}
