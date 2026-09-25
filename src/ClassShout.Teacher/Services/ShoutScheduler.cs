using Avalonia.Threading;
using ClassShout.Core.Remote;

namespace ClassShout.Teacher.Services;

/// <summary>一条定时任务被触发时的结果，供界面提示。</summary>
/// <param name="Item">任务本身。</param>
/// <param name="Ok">是否发出去。</param>
/// <param name="Message">给人看的一句话。</param>
public readonly record struct ScheduledSendResult(ScheduledShout Item, bool Ok, string? Message);

/// <summary>
/// 到点把定时喊话发出去。
///
/// 关于可靠性，这里有一条必须说清楚的边界：**它只在应用运行时有效**。
/// 老师把手机上的应用彻底划掉之后，到点不会有人替他发 —— 这不是实现偷懒，
/// 而是本地定时在移动系统上的固有边界（要做"关掉也能发"必须走服务器定时）。
/// 所以错过的任务不会被悄悄补发：超过宽限窗口的一律只标出来给老师看，
/// 因为"下课前五分钟提醒交作业"过期十分钟之后已经没意义了。
/// </summary>
public sealed class ShoutScheduler : IDisposable
{
    /// <summary>
    /// 迟发宽限：到点之后多久之内还允许发。
    ///
    /// 三分钟足够覆盖"手机刚醒过来、界面重新连上教室"这类短暂延迟，
    /// 又不至于让过期的提醒在下一节课上突然冒出来。
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    /// <summary>检查间隔。一秒一次足够精确到"分钟"这个量级，代价也可以忽略。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly TeacherScheduleSettings _settings;
    private DispatcherTimer? _timer;
    private bool _busy;

    public ShoutScheduler(TeacherScheduleSettings settings)
    {
        _settings = settings;

        // 启动时先扫一遍：应用刚打开时可能已经有任务到点了（甚至过期了），
        // 等下一次 tick 才发现会白白晚一秒，而且"过期"要在启动时就标出来。
        Sweep(DateTimeOffset.Now, sendDue: false);
    }

    /// <summary>真正把一条任务发出去。由外壳注入 —— 调度器不该知道链路怎么走。</summary>
    public Func<ScheduledShout, Task<(bool Ok, string? Message)>>? SendAsync { get; set; }

    /// <summary>任务被处理（发出、失败、错过）时通知界面刷新。</summary>
    public event Action<ScheduledSendResult>? Handled;

    /// <summary>设置变化（新增/取消任务）后，界面已经改过对象，这里只需要重新排一下期。</summary>
    public void Start()
    {
        _timer ??= new DispatcherTimer { Interval = Tick };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    /// 立刻检查一轮。
    /// </summary>
    /// <param name="now">当前时间（测试可传入固定值）。</param>
    /// <param name="sendDue">是否真的把到点的任务发出去；false 表示只做"过期"标记。</param>
    public void Sweep(DateTimeOffset now, bool sendDue = true)
    {
        if (_busy)
        {
            // 上一轮还没发完就不再进来：发送要走网络，慢的时候会超过一个 tick，
            // 并发跑两轮会让同一条任务被发两次。
            return;
        }

        var due = new List<ScheduledShout>();

        foreach (var item in _settings.Items.ToList())
        {
            if (item.IsMissedBeyond(now, Grace))
            {
                // 过期太久：只标记，不补发
                item.LastError = "已错过（应用当时没有运行）";
                _settings.MoveToHistory(item);
                Handled?.Invoke(new ScheduledSendResult(item, false, $"「{item.SummaryText}」已错过，没有发送。"));
                continue;
            }

            if (sendDue && item.IsDue(now))
            {
                due.Add(item);
            }
        }

        if (!sendDue || due.Count == 0)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var item in due)
                {
                    await SendOneAsync(item).ConfigureAwait(false);
                }
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private async Task SendOneAsync(ScheduledShout item)
    {
        if (SendAsync is null)
        {
            return;
        }

        bool ok;
        string? message;

        try
        {
            (ok, message) = await SendAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            ok = false;
            message = $"发送失败：{ex.Message}";
        }

        item.SentAt = ok ? DateTimeOffset.Now : null;
        item.LastError = ok ? null : message;

        // 发失败的任务留在列表里：老师能看见"这条没发出去"并手动重试，
        // 否则它会在没人知道的情况下消失。
        if (ok)
        {
            _settings.MoveToHistory(item);
        }

        Handled?.Invoke(new ScheduledSendResult(item, ok, message));
    }

    private void OnTick(object? sender, EventArgs e) => Sweep(DateTimeOffset.Now);

    public void Dispose()
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }
    }
}
