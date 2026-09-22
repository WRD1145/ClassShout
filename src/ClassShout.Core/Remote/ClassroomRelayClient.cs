using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClassShout.Core.Remote;

/// <summary>
/// 教室端的中继客户端。
///
/// 工作方式：
///   1. 启动时先查询自己的 UUID 在服务器上是否存在 —— 决定是"首次注册"还是"带口令重连"；
///   2. 注册成功后拿到会话令牌，用它长轮询接收教师端的喊话；
///   3. 长轮询断开时按指数退避重连，并在令牌失效（401）时自动重新注册。
///
/// 关于"Webhook"：服务器并没有主动 POST 到教室端 —— 那在 NAT 之后做不到。
/// 这里用一个长期挂起的 GET 代替，服务器有喊话时才响应。
/// 对上层使用者的观感与收到 webhook 回调完全一致，见 README 的说明。
/// </summary>
public sealed class ClassroomRelayClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ClassroomRelaySettings _settings;

    private CancellationTokenSource? _cts;
    private Task? _pollLoop;
    private string? _token;
    private long _since;

    /// <summary>当前是否正处于一路语音之中（见过 audioStart、还没见到 audioEnd / stop）。</summary>
    private bool _audioOpen;

    public ClassroomRelayClient(HttpClient http, ClassroomRelaySettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>收到教师端的喊话事件（文字、音频分片、停止指令）。</summary>
    public event Action<RelayEnvelope>? ShoutReceived;

    /// <summary>与服务器的连接状态变化。</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>运行日志。</summary>
    public event Action<string>? Log;

    /// <summary>是否已完成注册且长轮询在运行。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>服务器上是否已有本教室的注册记录。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>本次注册是否新建了记录（界面据此提示"请记住口令"）。</summary>
    public bool WasNewlyRegistered { get; private set; }

    public string ServerUrl => _settings.ServerUrl ?? string.Empty;

    public string Uuid => _settings.Uuid;

    /// <summary>
    /// 把线路路径拼成绝对地址。
    ///
    /// 不用 HttpClient.BaseAddress 的原因：同一个 HttpClient 可能被多台教室/多个服务器复用，
    /// 而 BaseAddress 一旦设定就不可变。服务器地址又是用户随时可改的，
    /// 所以每次请求都按当前配置现拼，避免"改了地址却还在往老服务器发"。
    /// </summary>
    private string Url(string path) => $"{(_settings.ServerUrl ?? string.Empty).TrimEnd('/')}{path}";

    /// <summary>
    /// 服务器最终采用的口令。首次注册时若客户端没提供，服务器会生成一个并回传，
    /// 界面需要把它显示给管理员转抄给老师。
    /// </summary>
    public string? Secret => _settings.Secret;

    /// <summary>
    /// 查询并注册。返回是否成功，失败时 <paramref name="error"/> 给出可直接展示给用户的原因。
    /// </summary>
    public async Task<bool> RegisterAsync(Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            log?.Invoke("尚未填写中继服务器地址。");
            return false;
        }

        _settings.EnsureUuid();

        try
        {
            // 先查询：区分"首次注册"与"重复连接"，两者的失败含义完全不同
            var lookup = await _http.GetFromJsonAsync<ClassroomLookupResponse>(
                Url(string.Format(RelayPaths.LookupClassroom, Uri.EscapeDataString(_settings.Uuid))),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            IsRegistered = lookup?.Exists ?? false;

            if (IsRegistered && string.IsNullOrWhiteSpace(_settings.Secret))
            {
                log?.Invoke("该 UUID 已在服务器上注册过，但本机没有保存口令。请向管理员确认口令后填入。");
                WasNewlyRegistered = false;
                return false;
            }

            var request = new ClassroomRegisterRequest(_settings.Uuid, _settings.ClassroomName, _settings.Secret);

            using var response = await _http.PostAsJsonAsync(
                Url(RelayPaths.RegisterClassroom), request, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"注册请求失败：HTTP {(int)response.StatusCode}");
                return false;
            }

            var result = await response.Content
                .ReadFromJsonAsync<ClassroomRegisterResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (result is null || !result.Ok)
            {
                log?.Invoke(result?.Error ?? "服务器拒绝了注册请求。");
                return false;
            }

            WasNewlyRegistered = result.IsNew;
            IsRegistered = true;
            _token = result.Token;

            // 服务器可能在首次注册时生成了口令，必须落盘，否则重启后就再也连不上了
            if (!string.IsNullOrEmpty(result.Secret))
            {
                _settings.Secret = result.Secret;
            }

            log?.Invoke(result.IsNew
                ? $"已在服务器上注册本教室（{result.Name}）。"
                : $"已连接服务器（{result.Name}）。");

            SetConnected(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log?.Invoke($"连接服务器失败：{ex.Message}");
            SetConnected(false);
            return false;
        }
    }

    /// <summary>启动长轮询循环。调用前应先完成一次 <see cref="RegisterAsync"/>。</summary>
    public void StartPolling()
    {
        if (_pollLoop is not null || _token is null)
        {
            return;
        }

        _cts = new CancellationTokenSource();

        // since 归零对服务器意味着"从此刻开始，不补发历史"。
        // 语音状态也要一并归零：新会话里不该继承上一次那个"正在播放"的判断，
        // 否则重连后第一段没有 audioStart 的分片会被误当成接着在播。
        _since = 0;
        _audioOpen = false;
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        // 下一条应当收到的序号。用它和实际到达的序号对比，就能发现断线期间漏掉了多少。
        var expected = _since > 0 ? _since + 1 : 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var batch = await PollOnceAsync(_since, cancellationToken).ConfigureAwait(false);
                if (batch is null)
                {
                    // 令牌失效：重新注册换取新令牌
                    Log?.Invoke("会话已失效，正在重新注册…");
                    SetConnected(false);
                    if (!await RegisterAsync(null, cancellationToken).ConfigureAwait(false))
                    {
                        await DelayAsync(backoff, cancellationToken).ConfigureAwait(false);
                        backoff = Next(backoff);
                        continue;
                    }

                    continue;
                }

                _since = batch.Next;
                backoff = TimeSpan.FromSeconds(1);
                SetConnected(true);

                // 序号缺口检测。
                //
                // 服务器只保留最近 2048 条历史，长时间离线再回来时，中间那段已经被挤出去了。
                // 序号是连续的，所以"缺了多少条"是能算出来的 —— 以前没人算，
                // 于是重连之后会把历史整段回放，其中包括一段没有 audioStart 打头的
                // 半截音频，教室端拿着没有格式说明的数据就去播。
                if (_since > 0 && batch.Events.Count > 0 && batch.Events[0].Sequence > expected + 1)
                {
                    var missed = batch.Events[0].Sequence - expected - 1;

                    if (_audioOpen)
                    {
                        // 正在播的那一路，结尾多半就在丢掉的那段里。
                        // 继续把后面的分片当成同一次喊话播放，只会播出一段没头没尾的声音。
                        _audioOpen = false;
                        Log?.Invoke("断线期间丢失了部分喊话，已结束当前语音。");
                    }

                    Log?.Invoke($"断线期间错过 {missed} 条事件。服务器只保留最近一段历史，长时间离线后会丢内容。");
                }

                expected = _since + 1;
                var droppedAudio = 0;

                foreach (var envelope in batch.Events)
                {
                    switch (envelope.Kind)
                    {
                        case RelayKinds.AudioStart:
                            _audioOpen = true;
                            break;

                        case RelayKinds.AudioEnd:
                        case RelayKinds.Stop:
                            _audioOpen = false;
                            break;

                        case RelayKinds.Audio when !_audioOpen:
                            // 没有 audioStart 打头的分片 —— 丢掉。
                            // 播放器连采样率和声道数都不知道，播出去是噪声或者直接抛异常。
                            droppedAudio++;
                            continue;
                    }

                    ShoutReceived?.Invoke(envelope);
                }

                if (droppedAudio > 0)
                {
                    Log?.Invoke($"丢弃了 {droppedAudio} 段没有起始标记的音频分片。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                SetConnected(false);
                Log?.Invoke($"与服务器的连接中断：{ex.Message}，{backoff.TotalSeconds:F0} 秒后重试。");
                await DelayAsync(backoff, cancellationToken).ConfigureAwait(false);
                backoff = Next(backoff);

                // 断线重连时重新注册，让服务器知道本教室又上线了
                try
                {
                    await RegisterAsync(null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception reregisterError) when (reregisterError is HttpRequestException or TaskCanceledException)
                {
                    // 下一轮循环会再试
                }
            }
        }
    }

    private async Task<EventBatch?> PollOnceAsync(long since, CancellationToken cancellationToken)
    {
        var url = Url($"{string.Format(RelayPaths.ClassroomEvents, Uri.EscapeDataString(_settings.Uuid))}?since={since}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(RelayPaths.TokenHeader, _token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<EventBatch>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>上报本教室的静音与音量，服务器会转发给已绑定的教师端。</summary>
    public async Task ReportStatusAsync(bool muted, int volume, string state, CancellationToken cancellationToken = default)
    {
        if (_token is null)
        {
            return;
        }

        try
        {
            var url = Url(string.Format(RelayPaths.ClassroomStatus, Uri.EscapeDataString(_settings.Uuid)));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new ClassroomStatusRequest(muted, volume, state), options: JsonOptions),
            };
            request.Headers.TryAddWithoutValidation(RelayPaths.TokenHeader, _token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 状态上报失败不影响主流程，下一次状态变化会再报
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

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private static TimeSpan Next(TimeSpan current) => TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * 2, 15));

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_pollLoop is not null)
        {
            try
            {
                await _pollLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 退出路径上不等了
            }

            _pollLoop = null;
        }

        _cts?.Dispose();
        _cts = null;
        _token = null;
    }
}
