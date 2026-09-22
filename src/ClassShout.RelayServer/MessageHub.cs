using ClassShout.Core.Remote;

namespace ClassShout.RelayServer;

/// <summary>一次长轮询的结果。</summary>
/// <param name="Events">本次投递的事件，可能为空（超时）。</param>
/// <param name="Next">下次轮询应带的 since。</param>
/// <param name="TimedOut">是否因超时返回。</param>
public readonly record struct PollResult(IReadOnlyList<RelayEnvelope> Events, long Next, bool TimedOut);

/// <summary>
/// 单条投递通道（一个教室或一个教师会话一条）。
///
/// 用长轮询而不是 WebSocket：教室端可能部署在只放行 HTTP 的网络里，
/// 长轮询走的是最普通的 GET，穿透性最好；而且断线后重连的语义天然幂等
/// （带上 since 就能续上），不需要自己实现心跳与重连状态机。
///
/// 历史只保留最近若干条：客户端断线重连时能补上短暂丢失的内容，
/// 但不会因为一个客户端长期不在线而把内存吃掉。
/// </summary>
public sealed class MessageQueue
{
    /// <summary>历史条数上限。按 100 毫秒一批音频算，约合 3 分钟的内容。</summary>
    private const int MaxHistory = 2048;

    private readonly Lock _lock = new();
    private readonly LinkedList<RelayEnvelope> _history = new();
    private readonly List<TaskCompletionSource> _waiters = [];
    private long _sequence;

    public long LastSequence
    {
        get
        {
            lock (_lock)
            {
                return _sequence;
            }
        }
    }

    /// <summary>投递一条事件，唤醒所有正在等待的轮询。</summary>
    public void Publish(RelayEnvelope envelope)
    {
        TaskCompletionSource[] waiters;

        lock (_lock)
        {
            envelope.Sequence = ++_sequence;
            envelope.At = DateTimeOffset.UtcNow;
            _history.AddLast(envelope);

            while (_history.Count > MaxHistory)
            {
                _history.RemoveFirst();
            }

            waiters = [.. _waiters];
            _waiters.Clear();
        }

        // 在锁外唤醒，避免被唤醒的续体在持锁状态下同步执行
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>
    /// 等到有新事件或超时。
    /// <paramref name="since"/> 传 0 表示"从此刻开始"，不补发历史 ——
    /// 客户端首次连接时不应该收到上一次会话遗留的音频。
    /// </summary>
    public async Task<PollResult> WaitAsync(long since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 检查历史与登记等待者必须在同一次持锁里完成，否则中间会有一个丢唤醒的窗口：
        //   Publish 恰好发生在"历史检查完"与"等待者登记上"之间时，
        //   它既没被这次检查看见（检查已经过去了），也没能唤醒任何人（等待者还没登记），
        //   于是这一条消息要一直等到超时 —— 最长 25 秒才送到教室。
        //   单条文字喊话最容易被这个窗口打中，因为它的"一次投递"就只有这一条。
        //
        // 放在同一个锁里之后，只可能是两种结果：
        //   · Publish 先拿到锁 —— 那么 _sequence > since，这里立刻返回，不用等；
        //   · 这个锁先被拿到 —— 那么等待者已经登记好，Publish 一定会唤醒它。
        // 没有第三种可能。
        long pending;
        lock (_lock)
        {
            if (since <= 0)
            {
                since = _sequence;
            }

            if (_sequence > since)
            {
                var ready = ReadSinceLocked(since);
                return new PollResult(ready, _sequence, false);
            }

            _waiters.Add(signal);
            pending = since;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var registration = timeoutSource.Token.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(), signal);

        try
        {
            await signal.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时属正常路径：客户端收到空批次后会立刻再发起一次轮询
        }
        finally
        {
            lock (_lock)
            {
                _waiters.Remove(signal);
            }
        }

        // since 已经是登记那一刻的序号（传 0 时就地取过 _sequence），
        // 所以这里读到的正好是"登记之后新产生的那批"，不多不少。
        var events = ReadSince(pending);
        return new PollResult(events, LastSequence, events.Count == 0);
    }

    private List<RelayEnvelope> ReadSince(long since)
    {
        lock (_lock)
        {
            return ReadSinceLocked(since);
        }
    }

    /// <summary>与 <see cref="ReadSince"/> 相同，但要求调用方已经持有 <see cref="_lock"/>。</summary>
    private List<RelayEnvelope> ReadSinceLocked(long since)
    {
        var result = new List<RelayEnvelope>();
        foreach (var envelope in _history)
        {
            if (envelope.Sequence > since)
            {
                result.Add(envelope);
            }
        }

        return result;
    }

    /// <summary>唤醒所有等待者（会话结束时用），让长轮询立刻返回而不是干等到超时。</summary>
    public void WakeAll()
    {
        TaskCompletionSource[] waiters;
        lock (_lock)
        {
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }
}

/// <summary>按 key 管理所有投递通道。</summary>
public sealed class MessageHub
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MessageQueue> _queues = new(StringComparer.Ordinal);

    public static string ClassroomKey(string uuid) => $"cls:{uuid}";

    public static string TeacherKey(string token) => $"tch:{token}";

    public MessageQueue Queue(string key) => _queues.GetOrAdd(key, static _ => new MessageQueue());

    public void Remove(string key)
    {
        if (_queues.TryRemove(key, out var queue))
        {
            queue.WakeAll();
        }
    }

    public void Publish(string key, RelayEnvelope envelope) => Queue(key).Publish(envelope);

    /// <summary>把一条事件广播给某个教室当前绑定的所有教师。</summary>
    public void PublishToTeachers(IEnumerable<string> tokens, RelayEnvelope envelope)
    {
        foreach (var token in tokens)
        {
            Publish(TeacherKey(token), envelope);
        }
    }
}
