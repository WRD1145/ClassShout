using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClassShout.Core.Audio;

namespace ClassShout.Core.Remote;

/// <summary>
/// 教师端的中继客户端。
///
/// 发喊话走普通 HTTP POST；收教室端状态走长轮询。
/// 音频在这里做一层累积：采集端每 20 毫秒出一片，直接发就是每秒 50 个请求，
/// 公网上这个频率既浪费又容易被中间设备限流。累积到 100 毫秒再发，
/// 请求降到每秒 10 个，而端到端延迟只增加 100 毫秒 —— 对"喊话"完全够用。
/// </summary>
public sealed class TeacherRelayClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>音频累积上限：100 毫秒。既降频又不明显增加延迟。</summary>
    private static readonly TimeSpan AudioBatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _http;
    private readonly TeacherRelaySettings _settings;
    private readonly Lock _audioLock = new();

    /// <summary>是否已经给服务器发过 audioStart（也就是教室端是否已进入播放状态）。</summary>
    private volatile bool _audioSessionOpen;

    private readonly List<byte> _audioBuffer = [];
    private CancellationTokenSource? _cts;
    private Task? _pollLoop;
    private Task? _audioFlusher;
    private string? _token;
    private long _since;

    public TeacherRelayClient(HttpClient http, TeacherRelaySettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>收到教室端事件（状态、上线/离线通知）。</summary>
    public event Action<RelayEnvelope>? EventReceived;

    public event Action<bool>? ConnectionChanged;

    public event Action<string>? Log;

    public bool IsBound => _token is not null;

    /// <summary>把线路路径拼成绝对地址。服务器地址用户可随时改，所以每次现拼。</summary>
    private string Url(string path) => $"{(_settings.ServerUrl ?? string.Empty).TrimEnd('/')}{path}";

    public bool IsConnected { get; private set; }

    public string? BoundUuid { get; private set; }

    public string? BoundClassroomName { get; private set; }

    /// <summary>
    /// 绑定教室。UUID 定位教室，口令鉴权。
    /// </summary>
    public async Task<(bool Ok, string? Error)> BindAsync(
        string uuid,
        string secret,
        string teacherName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            return (false, "尚未填写中继服务器地址。");
        }

        try
        {
            var request = new TeacherBindRequest(uuid.Trim(), secret, teacherName);

            // 带上登录令牌：服务器会以账号里的姓名为准，
            // 这样教室端弹窗上显示的老师姓名才是可信的，而不是客户端随便填的字符串。
            using var message = new HttpRequestMessage(HttpMethod.Post, Url(RelayPaths.BindTeacher))
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };

            if (_settings.IsSignedIn)
            {
                message.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, _settings.AuthToken);
            }

            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"绑定请求失败：HTTP {(int)response.StatusCode}");
            }

            var result = await response.Content
                .ReadFromJsonAsync<TeacherBindResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (result is null || !result.Ok)
            {
                return (false, result?.Error ?? "绑定失败。");
            }

            _token = result.Token;
            BoundUuid = uuid.Trim();
            BoundClassroomName = result.ClassroomName;

            // 记住教室，但不记口令 —— 口令让老师自己保管
            _settings.LastUuid = BoundUuid;
            RememberClassroom(BoundUuid, result.ClassroomName ?? "教室");

            Log?.Invoke($"已绑定教室「{result.ClassroomName}」");
            return (true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, $"连接服务器失败：{ex.Message}");
        }
    }

    private void RememberClassroom(string uuid, string name)
    {
        _settings.RecentClassrooms.RemoveAll(item => string.Equals(item.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
        _settings.RecentClassrooms.Insert(0, new BoundClassroom(uuid, name, DateTimeOffset.UtcNow));

        while (_settings.RecentClassrooms.Count > 8)
        {
            _settings.RecentClassrooms.RemoveAt(_settings.RecentClassrooms.Count - 1);
        }
    }

    /// <summary>
    /// 列出管理员在控制台上授权给当前账号的教室。
    /// 有了这个，老师手机上一点即可绑定，既不用抄 UUID，也不用传口令。
    /// </summary>
    public async Task<IReadOnlyList<AuthorizedClassroom>> GetAuthorizedClassroomsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsSignedIn || string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(RelayPaths.TeacherAuthorized));
            request.Headers.TryAddWithoutValidation(RelayPaths.AuthTokenHeader, _settings.AuthToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var list = await response.Content
                .ReadFromJsonAsync<List<AuthorizedClassroom>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return list ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log?.Invoke($"获取已授权教室失败：{ex.Message}");
            return [];
        }
    }

    // ======================== 发送 ========================

    public async Task<bool> SendTextAsync(
        string text,
        int rate,
        int volume,
        bool interrupt,
        CancellationToken cancellationToken = default)
    {
        if (_token is null || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return await PostAsync(
            Url(string.Format(RelayPaths.TeacherText, _token)),
            JsonContent.Create(new TextShoutRequest(text.Trim(), rate, volume, interrupt), options: JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAudioStartAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        if (_token is null)
        {
            return;
        }

        ClearAudioBuffer();

        // 发出去之后才记"会话已开"，与局域网那条链路同样的道理：
        // 先记后发的话，一旦这次 POST 失败，通道就以为会话开着，
        // 后面攒下来的裸 PCM 会被发到一个从没收到过 audioStart 的教室。
        await PostAsync(
            Url(string.Format(RelayPaths.TeacherAudioStart, _token)),
            JsonContent.Create(new AudioStartRequest(format.SampleRate, format.Channels, format.BitsPerSample), options: JsonOptions),
            cancellationToken).ConfigureAwait(false);

        _audioSessionOpen = true;
    }

    /// <summary>
    /// 送入一段 PCM。会先攒在本地，达到 100 毫秒才真正发出去。
    ///
    /// 没有开着的会话就一片都不收：录音途中链路切换（局域网断开、回落到中继）时
    /// 会走到这里，而这条中继链路从来没收到过 audioStart ——
    /// 发过去只会让教室端收到一段没有采样率和声道数的裸字节。
    /// </summary>
    public void AccumulateAudio(ReadOnlySpan<byte> pcm)
    {
        if (_token is null || pcm.IsEmpty || !_audioSessionOpen)
        {
            return;
        }

        var shouldFlush = false;

        lock (_audioLock)
        {
            _audioBuffer.AddRange(pcm);

            // 16 kHz / 16 bit / 单声道下 100 毫秒是 3200 字节
            if (_audioBuffer.Count >= 3200)
            {
                shouldFlush = true;
            }
        }

        if (shouldFlush)
        {
            _ = FlushAudioAsync(CancellationToken.None);
        }
    }

    public async Task SendAudioEndAsync(CancellationToken cancellationToken = default)
    {
        if (_token is null)
        {
            return;
        }

        await FlushAudioAsync(cancellationToken).ConfigureAwait(false);
        ClearAudioBuffer();
        _audioSessionOpen = false;

        await PostAsync(
            Url(string.Format(RelayPaths.TeacherAudioEnd, _token)),
            content: null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task SendStopAsync(CancellationToken cancellationToken = default)
        => _token is null
            ? Task.CompletedTask
            : PostAsync(Url(string.Format(RelayPaths.TeacherStop, _token)), content: null, cancellationToken);

    private async Task FlushAudioAsync(CancellationToken cancellationToken)
    {
        byte[] payload;

        lock (_audioLock)
        {
            if (_audioBuffer.Count == 0)
            {
                return;
            }

            payload = [.. _audioBuffer];
            _audioBuffer.Clear();
        }

        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            await PostAsync(Url(string.Format(RelayPaths.TeacherAudio, _token)), content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log?.Invoke($"音频发送失败：{ex.Message}");
        }
    }

    private void ClearAudioBuffer()
    {
        lock (_audioLock)
        {
            _audioBuffer.Clear();
        }
    }

    private async Task<bool> PostAsync(string path, HttpContent? content, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateBinding("会话已失效，请重新绑定教室。");
                return false;
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetConnected(false);
            Log?.Invoke($"发送失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 令牌失效：把它真的清掉，让"已绑定"这件事跟着变成否。
    ///
    /// 原来这里只调用 SetConnected(false)，_token 留着不动 —— 而 IsBound 看的正是
    /// _token，于是界面上依旧显示"已连接"、喊话按钮依然可点，
    /// 每一次喊话都在 PostAsync 里被 401 悄悄丢掉，只留一行日志。
    /// 老师看到的是"我按了、界面也正常"，教室里毫无动静：
    /// 这比直接报错难查得多。令牌清掉之后界面会自己回到"未绑定"。
    /// </summary>
    private void InvalidateBinding(string reason)
    {
        _token = null;

        lock (_audioLock)
        {
            _audioBuffer.Clear();
        }

        _audioSessionOpen = false;
        SetConnected(false);

        // 轮询循环靠 _token 工作，令牌没了就该停，否则它会一直空转打 401
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过
        }

        _pollLoop = null;
        _audioFlusher = null;

        Log?.Invoke(reason);
    }

    // ======================== 接收 ========================

    /// <summary>启动长轮询接收教室端状态。绑定成功后调用。</summary>
    public void StartPolling()
    {
        if (_pollLoop is not null || _token is null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _since = 0;

        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
        _audioFlusher = Task.Run(() => AudioFlushLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var url = Url($"{string.Format(RelayPaths.TeacherEvents, _token)}?since={_since}");
                var batch = await _http.GetFromJsonAsync<EventBatch>(url, JsonOptions, cancellationToken).ConfigureAwait(false);

                backoff = TimeSpan.FromSeconds(1);
                SetConnected(true);

                if (batch is null)
                {
                    continue;
                }

                _since = batch.Next;

                foreach (var envelope in batch.Events)
                {
                    EventReceived?.Invoke(envelope);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                SetConnected(false);
                Log?.Invoke($"状态通道中断：{ex.Message}，{backoff.TotalSeconds:F0} 秒后重试。");

                try
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 15));
            }
        }
    }

    /// <summary>
    /// 定时把没攒满一批的音频也发出去。
    /// 没有这个的话，说话末尾的不足 100 毫秒会被一直压在缓冲里，
    /// 直到下一次开口才补发 —— 听起来就是"最后一个字丢了"。
    /// </summary>
    private async Task AudioFlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AudioBatchInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAudioAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value)
        {
            return;
        }

        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    /// <summary>解除绑定。</summary>
    public async Task UnbindAsync()
    {
        var token = _token;
        _token = null;

        if (token is not null)
        {
            try
            {
                using var response = await _http.DeleteAsync(Url(string.Format(RelayPaths.TeacherUnbind, token))).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 服务器不响应也无妨，会话会在服务端超时
            }
        }

        BoundUuid = null;
        BoundClassroomName = null;
        SetConnected(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (var task in new[] { _pollLoop, _audioFlusher })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 退出路径上不等了
            }
        }

        _pollLoop = null;
        _audioFlusher = null;
        _cts?.Dispose();
        _cts = null;
    }
}
