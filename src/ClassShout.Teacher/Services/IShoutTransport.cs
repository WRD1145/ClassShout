using ClassShout.Core.Audio;
using ClassShout.Core.Remote;

namespace ClassShout.Teacher.Services;

/// <summary>
/// 喊话传输的抽象。
///
/// 教师端有两条互斥的链路：
///   · 局域网直连（<see cref="ShoutChannel"/>）—— 和教室在同一网络时用，延迟最低；
///   · 公网中继（<see cref="RelayShoutTransport"/>）—— 跨网络时用。
/// 上层的文字页与语音页只认这个接口，因此不必知道当前走的是哪条路。
/// </summary>
public interface IShoutTransport
{
    /// <summary>当前是否可用（已连上教室或已绑定服务器）。</summary>
    bool IsConnected { get; }

    Task<bool> SendTextAsync(
        string text,
        int rate,
        int volume,
        string? voiceName,
        bool interrupt,
        CancellationToken cancellationToken = default);

    Task BeginAudioAsync(AudioFormat format, CancellationToken cancellationToken = default);

    Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default);

    Task EndAudioAsync(CancellationToken cancellationToken = default);

    Task RequestStopAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>把公网中继客户端适配成统一的喊话传输。</summary>
/// <remarks>
/// 注意音频：中继客户端内部会累积到 100 毫秒再发（每秒 50 个 HTTP 请求是不可接受的），
/// 所以这里的"发送"实际是"交给它攒着"，接口层面保持异步以兼容局域网路径。
/// </remarks>
public sealed class RelayShoutTransport : IShoutTransport
{
    private readonly TeacherRelayClient _client;

    public RelayShoutTransport(TeacherRelayClient client) => _client = client;

    public bool IsConnected => _client.IsBound;

    public Task<bool> SendTextAsync(
        string text,
        int rate,
        int volume,
        string? voiceName,
        bool interrupt,
        CancellationToken cancellationToken = default)
        => _client.SendTextAsync(text, rate, volume, interrupt, cancellationToken);

    public Task BeginAudioAsync(AudioFormat format, CancellationToken cancellationToken = default)
        => _client.SendAudioStartAsync(format, cancellationToken);

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        _client.AccumulateAudio(pcm.Span);
        return Task.CompletedTask;
    }

    public Task EndAudioAsync(CancellationToken cancellationToken = default)
        => _client.SendAudioEndAsync(cancellationToken);

    public Task RequestStopAsync(string reason, CancellationToken cancellationToken = default)
        => _client.SendStopAsync(cancellationToken);
}

/// <summary>
/// 在两条链路之间切换的转发器。
///
/// 为什么不让上层直接持有具体实现：文字页和语音页在启动时就要拿到一个传输对象，
/// 而"用哪条链路"是之后才决定的（用户可能先连局域网、再改成服务器绑定）。
/// 用一层转发器，上层拿到的引用始终不变，切换链路时不必重建视图模型。
///
/// 未指定活动链路时所有调用都是安全空操作 —— 界面上的按钮本来也会被禁用，
/// 但空操作能保证即使竞态下按下也不会抛异常。
/// </summary>
public sealed class ShoutTransportRouter : IShoutTransport
{
    /// <summary>当前生效的链路；为 null 时所有发送都被丢弃。</summary>
    public IShoutTransport? Active { get; set; }

    public bool IsConnected => Active?.IsConnected ?? false;

    public Task<bool> SendTextAsync(
        string text,
        int rate,
        int volume,
        string? voiceName,
        bool interrupt,
        CancellationToken cancellationToken = default)
        => Active?.SendTextAsync(text, rate, volume, voiceName, interrupt, cancellationToken)
           ?? Task.FromResult(false);

    public Task BeginAudioAsync(AudioFormat format, CancellationToken cancellationToken = default)
        => Active?.BeginAudioAsync(format, cancellationToken) ?? Task.CompletedTask;

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
        => Active?.SendAudioAsync(pcm, cancellationToken) ?? Task.CompletedTask;

    public Task EndAudioAsync(CancellationToken cancellationToken = default)
        => Active?.EndAudioAsync(cancellationToken) ?? Task.CompletedTask;

    public Task RequestStopAsync(string reason, CancellationToken cancellationToken = default)
        => Active?.RequestStopAsync(reason, cancellationToken) ?? Task.CompletedTask;
}
