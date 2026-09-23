namespace ClassShout.Teacher.Services;

/// <summary>
/// 一条待发送的喊话。
/// </summary>
/// <param name="Ticket">入队序号，用于算"前面还有几个"。</param>
/// <param name="Text">喊话内容，用于日志。</param>
/// <param name="Send">真正发送。返回 false 表示没发出去。</param>
internal sealed record QueuedShout(long Ticket, string Text, Func<CancellationToken, Task<bool>> Send);

/// <summary>
/// 文字喊话队列：按入队先后一条一条发，并让界面能回答"我这条前面还有几个"。
///
/// 为什么需要排队：老师连点几下发送，或者网络慢的时候连发几条，
/// 如果不排队就会有几条同时在飞 —— 教室端那边看到的是乱序的朗读，
/// 而老师完全不知道发生了什么。排队之后顺序就是"喊出的先后"，
/// 这也正是教室里听到的顺序应该遵循的唯一依据。
///
/// 只排文字，不排语音：语音是边录边流的实时链路，攒起来再播就没有意义了
/// （教室里要的是"现在就说"，不是"三秒前那句"）。
/// </summary>
public sealed class ShoutQueue : IDisposable
{
    private readonly List<QueuedShout> _queue = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();

    private long _nextTicket;

    public ShoutQueue() => _ = Task.Run(PumpAsync);

    /// <summary>队列里还有多少条（含正在发送的那一条）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>队列有变化（入队或出队）。界面据此刷新"前面还有几个"。</summary>
    public event Action? Changed;

    /// <summary>一条发完了。参数是内容与是否成功。</summary>
    public event Action<string, bool>? Sent;

    /// <summary>
    /// 入队。返回这张票，用 <see cref="PositionOf"/> 可以随时问"前面还有几个"。
    ///
    /// 返回票据而不是"当时前面有几个"：队列随时在动，
    /// 入队那一瞬间的数字过一秒就不对了，界面要的是"现在"还剩几个。
    /// </summary>
    public long Enqueue(string text, Func<CancellationToken, Task<bool>> send)
    {
        long ticket;

        lock (_lock)
        {
            ticket = ++_nextTicket;
            _queue.Add(new QueuedShout(ticket, text, send));
        }

        Changed?.Invoke();

        // SemaphoreSlim 的计数上限压到 1：这里只需要"有活干了"这一个信号，
        // 队列本身才是真相来源。放开计数会出现信号数多于实际条目、
        // 泵空转甚至负数的麻烦。
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已经有信号了，不用再加
            }
        }

        return ticket;
    }

    /// <summary>这张票前面还有几条（不含它自己）。已经在发或者已经发完就返回 0。</summary>
    public int PositionOf(long ticket)
    {
        lock (_lock)
        {
            var ahead = 0;

            foreach (var item in _queue)
            {
                if (item.Ticket == ticket)
                {
                    return ahead;
                }

                ahead++;
            }

            // 不在队列里了：要么正在发（已经从队列取出），要么已经发完
            return 0;
        }
    }

    /// <summary>某一个教室的连接断了：把队列里还没发的都清掉，避免它们发到别处或残留。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
        }

        Changed?.Invoke();
    }

    private async Task PumpAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (true)
            {
                QueuedShout? item;

                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        break;
                    }

                    item = _queue[0];
                    _queue.RemoveAt(0);
                }

                // 先通知一次：这条已经离开队列，界面上的"前面还有几个"应当立刻变小，
                // 而不是等它真的发完才更新 —— 那会让老师觉得队列卡住了。
                Changed?.Invoke();

                var ok = false;

                try
                {
                    ok = await item.Send(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException
                                              or ObjectDisposedException
                                              or System.Net.Sockets.SocketException
                                              or HttpRequestException
                                              or TaskCanceledException
                                              or OperationCanceledException)
                {
                    // 发不出去就是发不出去：队列继续走，不能让一条失败卡住后面所有喊话
                }

                Sent?.Invoke(item.Text, ok);
                Changed?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _signal.Dispose();
    }
}