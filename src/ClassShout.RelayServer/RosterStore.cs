using System.Text.Json;
using System.Text.Json.Serialization;
using ClassShout.Core.Remote;

namespace ClassShout.RelayServer;

/// <summary>某位老师同步到服务器上的名单与呼叫模板。</summary>
public sealed class TeacherRosterRecord
{
    /// <summary>用户 Id（内置管理员也用它自己的 Id）。</summary>
    public string UserId { get; set; } = string.Empty;

    public List<StudentRoster> Rosters { get; set; } = [];

    public string? ActiveRosterId { get; set; }

    public List<CallTemplate> Templates { get; set; } = [];

    public string? ActiveTemplateId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 名单与呼叫模板的存储（每位老师一份）。
///
/// 为什么要存在服务器上：客户端的「呼叫」以名单为前提，而 WebUI 跑在服务器上、
/// 看不到老师手机里的那份名单 —— 同步一份上来，WebUI 才能用同一份名单、
/// 同一套拼装规则（Core 里的 CallComposer）拼出同样的话。
///
/// 与其它状态文件一样的规矩：单独一个文件、原子写、写失败要能让接口如实报错。
/// </summary>
public sealed class RosterStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<TeacherRosterRecord> _records = [];
    private readonly Lock _lock = new();
    private readonly string _statePath;
    private readonly ILogger<RosterStore> _logger;

    public RosterStore(string statePath, ILogger<RosterStore> logger)
    {
        _statePath = statePath;
        _logger = logger;
        Load();
    }

    /// <summary>已经同步过名单的账号数（控制台概览用）。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _records.Count;
            }
        }
    }

    public TeacherRosterRecord? Get(string userId)
    {
        lock (_lock)
        {
            return _records.FirstOrDefault(r => string.Equals(r.UserId, userId, StringComparison.Ordinal));
        }
    }

    /// <summary>保存（覆盖）某位老师的名单与模板。</summary>
    public bool Save(TeacherRosterRecord record)
    {
        lock (_lock)
        {
            var index = _records.FindIndex(r => string.Equals(r.UserId, record.UserId, StringComparison.Ordinal));
            TeacherRosterRecord? previous = index >= 0 ? _records[index] : null;

            if (index >= 0)
            {
                _records[index] = record;
            }
            else
            {
                _records.Add(record);
            }

            if (SaveLocked())
            {
                return true;
            }

            // 写盘失败就把内存改回去：接口不能一边说"同步成功"、一边重启后什么都不剩
            if (index >= 0)
            {
                _records[index] = previous!;
            }
            else
            {
                _records.Remove(record);
            }

            return false;
        }
    }

    /// <summary>账号被删除时顺手清掉它的名单（别人不该读到离职老师的班级名单）。</summary>
    public bool Remove(string userId)
    {
        lock (_lock)
        {
            var removed = _records.RemoveAll(r => string.Equals(r.UserId, userId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return true;
            }

            return SaveLocked();
        }
    }

    private bool SaveLocked()
    {
        try
        {
            AtomicStateFile.Write(_statePath, JsonSerializer.Serialize(_records.ToList(), SerializerOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存名单失败：{Path}", _statePath);
            return false;
        }
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<TeacherRosterRecord>>(
                File.ReadAllText(_statePath), SerializerOptions);

            if (loaded is not null)
            {
                _records.AddRange(loaded);
            }

            _logger.LogInformation("已载入 {Count} 位老师的名单。", _records.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "载入名单失败，将从空开始：{Path}", _statePath);
        }
    }
}
