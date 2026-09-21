using System.Net;
using ClassShout.Core.Audio;
using ClassShout.Core.Net;
using ClassShout.Core.Protocol;

namespace ClassShout.Teacher.Services;

/// <summary>
/// 教师端的喊话通道：把"连接教室"和"发文字/发语音"这两件事收在一处。
///
/// 视图模型只管业务语义（"把这句话喊出去"），不需要知道底下是 TCP 分帧、
/// 有多少个包、音频会话 ID 是什么。
/// </summary>
public sealed class ShoutChannel : IShoutTransport, IAsyncDisposable
{
    private readonly TeacherClient _client = new();
    private string? _audioSessionId;

    public ShoutChannel()
    {
        _client.Log += message => Log?.Invoke(message);
        _client.ControlReceived += message => MessageReceived?.Invoke(message);
        _client.Disconnected += reason =>
        {
            ConnectionChanged?.Invoke(false);
            Log?.Invoke($"连接断开：{reason}");
        };
    }

    /// <summary>运行日志。</summary>
    public event Action<string>? Log;

    /// <summary>收到教室端发来的控制消息。</summary>
    public event Action<ShoutMessage>? MessageReceived;

    /// <summary>连接状态变化。</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _client.IsConnected;

    /// <summary>当前连接的教室（通过自动发现连接时才有值）。</summary>
    public ClassroomAnnouncement? ConnectedClassroom { get; private set; }

    /// <summary>握手后教室端回报的名称。</summary>
    public string ClassroomName => _client.ClassroomName ?? ConnectedClassroom?.Name ?? "教室端";

    /// <summary>教室端是否静音（由状态消息更新）。</summary>
    public bool ClassroomMuted { get; private set; }

    /// <summary>教室端音量。</summary>
    public int ClassroomVolume { get; private set; } = 100;

    /// <summary>是否为语音会话进行中。</summary>
    public bool IsAudioSessionOpen => _audioSessionId is not null;

    public async Task ConnectAsync(ClassroomAnnouncement classroom, string clientName, CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(classroom.ToEndPoint(), clientName, cancellationToken).ConfigureAwait(false);
        ConnectedClassroom = classroom;
        ConnectionChanged?.Invoke(true);
    }

    public async Task ConnectAsync(IPEndPoint endpoint, string clientName, CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(endpoint, clientName, cancellationToken).ConfigureAwait(false);
        ConnectedClassroom = null;
        ConnectionChanged?.Invoke(true);
    }

    public async Task DisconnectAsync()
    {
        _audioSessionId = null;
        await _client.DisconnectAsync().ConfigureAwait(false);
        ConnectedClassroom = null;
        ConnectionChanged?.Invoke(false);
    }

    /// <summary>发送文字喊话（教室端用系统 TTS 朗读）。</summary>
    public async Task<bool> SendTextAsync(
        string text,
        int rate,
        int volume,
        string? voiceName,
        bool interrupt,
        CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        await _client.SendTextAsync(new TextShoutMessage
        {
            Text = text.Trim(),
            Rate = rate,
            Volume = volume,
            VoiceName = voiceName,
            Interrupt = interrupt,
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>开始一次语音喊话。</summary>
    public async Task BeginAudioAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        _audioSessionId = Guid.NewGuid().ToString("N");
        await _client.SendAudioStartAsync(_audioSessionId, format, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送一段 PCM。</summary>
    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
        => _client.SendAudioAsync(pcm, cancellationToken);

    /// <summary>结束语音喊话。</summary>
    public async Task EndAudioAsync(CancellationToken cancellationToken = default)
    {
        if (_audioSessionId is null)
        {
            return;
        }

        var id = _audioSessionId;
        _audioSessionId = null;
        await _client.SendAudioEndAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>要求教室端立即停止播放或朗读。</summary>
    public Task RequestStopAsync(string reason = "教师端中止", CancellationToken cancellationToken = default)
        => _client.SendStopAsync(reason, cancellationToken);

    /// <summary>处理教室端上报的状态。</summary>
    public void ApplyStatus(StatusMessage status)
    {
        ClassroomMuted = status.Muted;
        ClassroomVolume = status.Volume;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await EndAudioAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // 退出路径上的发送失败可忽略
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
