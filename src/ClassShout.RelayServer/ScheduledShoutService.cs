using ClassShout.Core.Audio;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;

namespace ClassShout.RelayServer;

/// <summary>
/// 到点把服务器上的定时喊话发出去。
///
/// 这是"教师端不必在后台运行"这句话的全部实现：任务存在服务器上，
/// 由服务器自己看着表发 —— 老师关掉手机、甚至关机过周末，到点教室里照样响。
///
/// 两条与即时喊话刻意保持一致的规矩：
///   · **不在线的教室不发**。消息队列有历史上限，发给一间已经关机的教室，
///     它下次开机可能收到一条几小时前的"临时通知" —— 那种迟到比没收到更糟。
///   · **错过太久的只标出来、不补发**。服务器重启、或到点那一刻正好没人收，
///     过了宽限窗口就认为这条已经没有意义了。
///
/// 语音按**实时节奏**推：每 100 毫秒一片，与老师当场按住说话时教室端看到的
/// 数据流一模一样。一股脑塞进去虽然也能播，但那就等于给教室端引入一种
/// 只在定时语音上才会出现的输入模式 —— 而那种分支最容易在真机上出问题。
/// </summary>
public sealed class ScheduledShoutService : BackgroundService
{
    /// <summary>与教师端本机定时用的是同一个宽限窗口：三分钟内还允许发。</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    /// <summary>一片 100 毫秒：与中继客户端攒包的大小一致。</summary>
    private const int ChunkMs = 100;

    private readonly ScheduledShoutStore _store;
    private readonly ClassroomStore _classrooms;
    private readonly RelaySessions _sessions;
    private readonly MessageHub _hub;
    private readonly UserStore _users;
    private readonly ILogger<ScheduledShoutService> _logger;
    private readonly TimeSpan _tick;

    public ScheduledShoutService(
        ScheduledShoutStore store,
        ClassroomStore classrooms,
        RelaySessions sessions,
        MessageHub hub,
        UserStore users,
        ILogger<ScheduledShoutService> logger)
    {
        _store = store;
        _classrooms = classrooms;
        _sessions = sessions;
        _hub = hub;
        _users = users;
        _logger = logger;

        // 检查间隔可调：自检要把它压到几百毫秒，否则每个用例都得干等五秒。
        // 生产环境没必要更密 —— 定时精确到分钟，五秒的粒度绰绰有余。
        var configured = Environment.GetEnvironmentVariable("CLASSSHOUT_SCHEDULE_TICK_MS");
        _tick = int.TryParse(configured, out var ms) && ms is >= 50 and <= 60_000
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromSeconds(5);
    }

    /// <summary>教室多久没动静就算"不在线"。与集体喊话用的是同一个窗口。</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("定时任务调度已启动，检查间隔 {Tick}", _tick);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FireDueAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 一条任务出问题不该让整个调度停摆 —— 停了就再也不会有人发
                _logger.LogError(ex, "定时任务处理时出错，稍后继续");
            }

            try
            {
                await Task.Delay(_tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>把到点的任务处理一遍。自检直接调它，不必等真实的定时器。</summary>
    public async Task FireDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        foreach (var item in _store.DuePending(now))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await FireOneAsync(item, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "定时任务 {Id} 发送失败", item.Id);
                _store.Complete(item, ServerScheduleStatus.Missed, $"发送时出错：{ex.Message}", []);
            }
        }
    }

    private async Task FireOneAsync(ServerScheduledShout item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (item.SendAt + Grace < now)
        {
            _store.Complete(item, ServerScheduleStatus.Missed, "已错过（超过三分钟没有发出去）",
                [$"到点时间 {item.SendAt.ToLocalTime():MM-dd HH:mm}，已经晚了 {now - item.SendAt:hh\\:mm}。"]);
            return;
        }

        var profile = _users.FindById(item.OwnerUserId);
        var results = new List<string>();
        var sent = 0;
        var offline = 0;

        foreach (var uuid in item.TargetUuids)
        {
            var record = _classrooms.Get(uuid);
            var name = record?.Name ?? uuid;

            // 离线教室不发：见类注释里那条理由
            if (record is null || record.LastSeenAt < now - OnlineWindow)
            {
                offline++;
                results.Add($"{name}：不在线，未发送。");
                continue;
            }

            // 科目按**这一间**取：同一位老师在不同班可能教不同科目
            var from = profile?.ShoutNameFor(uuid) ?? item.OwnerDisplayName;

            try
            {
                await PublishAsync(item, uuid, from, cancellationToken).ConfigureAwait(false);
                sent++;
                results.Add($"{name}：已发出。");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                results.Add($"{name}：发送失败（{ex.Message}）。");
            }
        }

        var status = sent > 0 ? ServerScheduleStatus.Sent : ServerScheduleStatus.Missed;

        string? error = null;
        if (sent == 0)
        {
            error = offline > 0
                ? "目标教室都不在线，这条没有发出去。"
                : "没有可发送的目标教室。";
        }

        _store.Complete(item, status, error, results);

        _logger.LogInformation("服务器定时 #{Id}（{Kind}）已处理：发出 {Sent} 间，离线 {Offline} 间",
            item.Id, item.Kind, sent, offline);
    }

    private async Task PublishAsync(
        ServerScheduledShout item,
        string uuid,
        string from,
        CancellationToken cancellationToken)
    {
        var key = MessageHub.ClassroomKey(uuid);

        if (string.Equals(item.Kind, ScheduledShoutKinds.Voice, StringComparison.OrdinalIgnoreCase))
        {
            var pcm = _store.ReadAudio(item);
            if (pcm is null)
            {
                throw new InvalidOperationException("语音文件已丢失");
            }

            var format = new AudioFormat(item.AudioSampleRate, item.AudioChannels, item.AudioBitsPerSample);
            var chunkSize = format.BytesForDuration(ChunkMs);
            if (chunkSize <= 0)
            {
                throw new InvalidOperationException("语音格式不合法");
            }

            _hub.Publish(key, new RelayEnvelope
            {
                Kind = RelayKinds.AudioStart,
                From = from,
                SampleRate = format.SampleRate,
                Channels = format.Channels,
                BitsPerSample = format.BitsPerSample,
            });

            for (var offset = 0; offset < pcm.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, pcm.Length - offset);

                _hub.Publish(key, new RelayEnvelope
                {
                    Kind = RelayKinds.Audio,
                    From = from,
                    AudioBase64 = Convert.ToBase64String(pcm, offset, length),
                });

                // 实时节奏：教室端看到的是一条"正在说话的语音"。
                // 最后一片之后不再等，省掉一次无谓的延迟。
                if (offset + length < pcm.Length)
                {
                    await Task.Delay(ChunkMs, cancellationToken).ConfigureAwait(false);
                }
            }

            _hub.Publish(key, new RelayEnvelope
            {
                Kind = RelayKinds.AudioEnd,
                From = from,
            });

            return;
        }

        _hub.Publish(key, new RelayEnvelope
        {
            Kind = RelayKinds.TextShout,
            From = from,
            Text = item.Text,
            Rate = 1,
            Volume = 100,
            Interrupt = true,
            Display = item.Display,
            FontSize = item.FontSize,
            HoldMs = item.HoldMs,
            Speak = item.Speak,
        });
    }
}
