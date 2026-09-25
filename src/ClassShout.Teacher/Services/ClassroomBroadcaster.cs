using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;

namespace ClassShout.Teacher.Services;

/// <summary>一次多班喊话里，某一间的结果。</summary>
/// <param name="Classroom">目标教室。</param>
/// <param name="Ok">是否送到。</param>
/// <param name="Error">失败原因，可直接展示给老师。</param>
public readonly record struct BroadcastResult(BoundClassroom Classroom, bool Ok, string? Error);

/// <summary>
/// 把一条文字喊话同时发给多个班级。
///
/// 为什么单独一个类、而不让"当前绑定"那条连接多发几次：
/// 教室端的当前绑定是一条长连接（要收状态、要发语音），而"顺便也发给另外两个班"
/// 是另一回事 —— 它们不需要保持连接，只要在发送这一刻拿到一个有效的绑定令牌就够了。
/// 所以这里对每个目标按需绑定、发送，再把令牌缓存起来复用，
/// 而不是让教师端同时挂着五条长轮询（那是五倍的服务器连接与心跳）。
/// </summary>
public sealed class ClassroomBroadcaster
{
    private readonly HttpClient _http;
    private readonly TeacherRelaySettings _settings;

    /// <summary>已经绑定好的客户端，按教室 UUID 缓存。令牌失效时会重绑并替换。</summary>
    private readonly Dictionary<string, TeacherRelayClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public ClassroomBroadcaster(HttpClient http, TeacherRelaySettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>发给某一间的过程中要记的日志（绑定失败、重试等）。</summary>
    public event Action<string>? Log;

    /// <summary>串行发给每一间，逐个返回结果。</summary>
    /// <remarks>
    /// 刻意串行而不是并发：一个老师一次通常只发三五个班，串行也就多几百毫秒，
    /// 但日志顺序清楚、出错时能直接指出是哪一间没送到。
    /// 并发发送只会让"三间里有一间失败"变成一个需要自己去对号入座的谜题。
    /// </remarks>
    public async Task<IReadOnlyList<BroadcastResult>> SendTextAsync(
        IReadOnlyList<BoundClassroom> targets,
        TextShoutMessage message,
        string teacherName,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BroadcastResult>(targets.Count);

        foreach (var target in targets)
        {
            results.Add(await SendToOneAsync(target, message, teacherName, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>忘掉缓存的绑定。退出登录、换服务器、或用户手动切换班级时调用。</summary>
    public async Task ResetAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
    }

    private async Task<BroadcastResult> SendToOneAsync(
        BoundClassroom target,
        TextShoutMessage message,
        string teacherName,
        CancellationToken cancellationToken)
    {
        var client = await EnsureBoundAsync(target, teacherName, cancellationToken).ConfigureAwait(false);

        if (client is null)
        {
            return new BroadcastResult(target, false, $"无法绑定教室「{target.Name}」。");
        }

        if (await client.SendTextAsync(
                message.Text,
                message.Rate,
                message.Volume,
                message.Interrupt,
                message.Display,
                message.FontSize,
                message.HoldMs,
                message.Speak,
                cancellationToken).ConfigureAwait(false))
        {
            return new BroadcastResult(target, true, null);
        }

        // 发失败最常见的原因是令牌失效（服务器重启过、或会话被清理）。
        // 这种情况重绑一次就能恢复 —— 只试一次就放弃，会让"多班喊话"
        // 在服务器重启之后一直失败到有人手动去切换一遍班级为止。
        Log?.Invoke($"教室「{target.Name}」的绑定可能已失效，正在重新绑定…");
        await DropAsync(target.Uuid).ConfigureAwait(false);

        var retry = await EnsureBoundAsync(target, teacherName, cancellationToken).ConfigureAwait(false);
        if (retry is null)
        {
            return new BroadcastResult(target, false, $"重新绑定教室「{target.Name}」失败。");
        }

        var ok = await retry.SendTextAsync(
            message.Text,
            message.Rate,
            message.Volume,
            message.Interrupt,
            message.Display,
            message.FontSize,
            message.HoldMs,
            message.Speak,
            cancellationToken).ConfigureAwait(false);

        return ok
            ? new BroadcastResult(target, true, null)
            : new BroadcastResult(target, false, $"教室「{target.Name}」没有收到（可能不在线）。");
    }

    private async Task<TeacherRelayClient?> EnsureBoundAsync(
        BoundClassroom target,
        string teacherName,
        CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(target.Uuid, out var cached) && cached.IsBound)
        {
            return cached;
        }

        var client = new TeacherRelayClient(_http, _settings, target.ServerUrl);

        // remember: false —— 这里的绑定是为了发送，不该把保存列表重排一遍
        var (ok, error) = await client
            .BindAsync(target.Uuid, target.Secret ?? string.Empty, teacherName, remember: false, cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            Log?.Invoke($"绑定教室「{target.Name}」失败：{error}");
            return null;
        }

        _clients[target.Uuid] = client;
        return client;
    }

    private async Task DropAsync(string uuid)
    {
        if (_clients.Remove(uuid, out var client))
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
