using System.Text.Json;
using ClassShout.Core.Remote;

namespace ClassShout.RelayServer;

/// <summary>
/// 服务器上的定时喊话：一张表 + 一段音频。
///
/// 存盘（<c>relay-schedule.json</c>）而不是只放内存：这个功能的全部意义就是
/// "老师关掉手机以后它照样发"，而服务器自己也会重启 —— 存在内存里的话，
/// 重启一次所有未来的任务就没了，而老师那边还以为排好了。
///
/// 音频单独放一个目录（<c>relay-schedule-audio/</c>）：一条 30 秒的语音约 1 MB，
/// 塞进 JSON 会让整张表在每次读写时都被 base64 膨胀一遍。
/// </summary>
public sealed class ScheduledShoutStore
{
    /// <summary>每位老师最多排几条。这是"今天剩下的几件事"，不是日历。</summary>
    public const int MaxPerOwner = 30;

    /// <summary>已处理的历史每人留几条。</summary>
    public const int MaxHistoryPerOwner = 20;

    /// <summary>整台服务器最多存多少条待发。防止一个脚本把磁盘写满。</summary>
    public const int MaxTotalPending = 500;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _statePath;
    private readonly string _audioDirectory;
    private readonly Lock _lock = new();
    private readonly List<ServerScheduledShout> _items;
    private readonly ILogger<ScheduledShoutStore> _logger;

    public ScheduledShoutStore(ILogger<ScheduledShoutStore> logger, string? statePath = null, string? audioDirectory = null)
    {
        _logger = logger;
        _statePath = statePath ?? Path.Combine(Directory.GetCurrentDirectory(), "relay-schedule.json");
        _audioDirectory = audioDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "relay-schedule-audio");
        _items = Load();
    }

    /// <summary>音频目录（写入时用）。</summary>
    public string AudioDirectory => _audioDirectory;

    private List<ServerScheduledShout> Load()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return [];
            }

            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<List<ServerScheduledShout>>(json, Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 读不出来不该让服务器起不来：宁可丢掉这张表，也不要整台服务器不转
            _logger.LogError(ex, "定时任务表读取失败，本次以空表启动：{Path}", _statePath);
            return [];
        }
    }

    private bool SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, Options));
            File.Move(temp, _statePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "定时任务表写入失败：{Path}", _statePath);
            return false;
        }
    }

    /// <summary>加一条。返回错误文案；成功时为 null。</summary>
    public string? Add(ServerScheduledShout item)
    {
        lock (_lock)
        {
            var pendingOfOwner = _items.Count(existing => existing.OwnerUserId == item.OwnerUserId && existing.IsPending);
            if (pendingOfOwner >= MaxPerOwner)
            {
                return $"最多同时排 {MaxPerOwner} 条定时任务，请先取消几条。";
            }

            if (_items.Count(existing => existing.IsPending) >= MaxTotalPending)
            {
                return "服务器上的定时任务太多了，请稍后再试。";
            }

            _items.Add(item);

            if (!SaveLocked())
            {
                _items.Remove(item);
                return "服务器暂时写不进磁盘，这条定时没有排上。";
            }

            return null;
        }
    }

    /// <summary>某个账号的任务（待发在前、历史在后，各自按时间排）。</summary>
    public IReadOnlyList<ServerScheduledShout> OfOwner(string ownerUserId)
    {
        lock (_lock)
        {
            return _items.Where(item => item.OwnerUserId == ownerUserId).ToList();
        }
    }

    /// <summary>取一条（用于取消前的归属校验）。</summary>
    public ServerScheduledShout? Get(string id)
    {
        lock (_lock)
        {
            return _items.FirstOrDefault(item => item.Id == id);
        }
    }

    /// <summary>该到点的任务（含已经到点但还没处理的）。</summary>
    public IReadOnlyList<ServerScheduledShout> DuePending(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _items
                .Where(item => item.IsPending && item.SendAt <= now)
                .OrderBy(item => item.SendAt)
                .ToList();
        }
    }

    /// <summary>取消一条。返回错误文案；成功时为 null。</summary>
    public string? Cancel(string id, string? ownerUserId, bool asAdmin)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(existing => existing.Id == id);
            if (item is null)
            {
                return "找不到这条定时任务。";
            }

            if (!asAdmin && !string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal))
            {
                // 别人的任务：连"存在与否"都不该确认得太具体
                return "找不到这条定时任务。";
            }

            if (!item.IsPending)
            {
                return "这条已经处理过了，取消不了。";
            }

            item.Status = ServerScheduleStatus.Cancelled;
            item.HandledAt = DateTimeOffset.UtcNow;
            item.Results.Add("已取消。");

            SaveLocked();
            DeleteAudioLocked(item);
            return null;
        }
    }

    /// <summary>标记一条的结果并落盘（发送完成后调用）。</summary>
    public void Complete(ServerScheduledShout item, string status, string? error, IEnumerable<string> results)
    {
        lock (_lock)
        {
            item.Status = status;
            item.HandledAt = DateTimeOffset.UtcNow;
            item.Error = error;
            item.Results.AddRange(results);

            // 发出去的语音就没有用了：留着只会让磁盘慢慢被旧音频吃满
            if (!item.IsPending)
            {
                DeleteAudioLocked(item);
            }

            TrimHistoryLocked(item.OwnerUserId);
            SaveLocked();
        }
    }

    private void TrimHistoryLocked(string ownerUserId)
    {
        var handled = _items
            .Where(item => item.OwnerUserId == ownerUserId && !item.IsPending)
            .OrderByDescending(item => item.HandledAt ?? item.SendAt)
            .ToList();

        foreach (var stale in handled.Skip(MaxHistoryPerOwner))
        {
            _items.Remove(stale);
        }
    }

    private void DeleteAudioLocked(ServerScheduledShout item)
    {
        if (string.IsNullOrWhiteSpace(item.AudioFile))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_audioDirectory, item.AudioFile);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着：几百 KB 的垃圾不值得让一次发送失败
            _logger.LogWarning(ex, "定时语音文件删除失败：{File}", item.AudioFile);
        }
    }

    /// <summary>把一段 PCM 存成音频文件，返回文件名（不含目录）。</summary>
    public string? SaveAudio(string id, ReadOnlySpan<byte> pcm)
    {
        try
        {
            Directory.CreateDirectory(_audioDirectory);
            var fileName = id + ".pcm";
            File.WriteAllBytes(Path.Combine(_audioDirectory, fileName), pcm.ToArray());
            return fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "定时语音写入失败：{Id}", id);
            return null;
        }
    }

    /// <summary>读回一段音频。读不出来时返回 null（调用方按"发不出去"处理）。</summary>
    public byte[]? ReadAudio(ServerScheduledShout item)
    {
        if (string.IsNullOrWhiteSpace(item.AudioFile))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(_audioDirectory, item.AudioFile);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "定时语音读取失败：{File}", item.AudioFile);
            return null;
        }
    }
}
