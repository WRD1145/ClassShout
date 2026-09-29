using System.Text.Json;
using System.Text.Json.Serialization;
using ClassShout.Core.Remote;

namespace ClassShout.RelayServer;

/// <summary>某位老师在某个班里同步到服务器上的名单与呼叫模板。</summary>
public sealed class TeacherRosterRecord
{
    /// <summary>用户 Id（内置管理员也用它自己的 Id）。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// 这份名单属于哪个班。
    ///
    /// 名单**按班隔离**：一位老师教三个班，三个班的学生名单是三分不同的数据 ——
    /// 混成一份的话，二班的名单会被当成三班的用，教室里叫出来的人根本不是那个班的。
    ///
    /// 空串表示"还没指定班级"的那一份：升级前的老记录就是这个样子。
    /// 读的时候先找精确匹配，找不到再退回这一份（见 <see cref="RosterStore.Get"/>），
    /// 于是升级后老师第一次同步之前，界面上仍然是"原来那份名单"，不会突然变空。
    /// </summary>
    public string ClassroomUuid { get; set; } = string.Empty;

    public List<StudentRoster> Rosters { get; set; } = [];

    public string? ActiveRosterId { get; set; }

    public List<CallTemplate> Templates { get; set; } = [];

    public string? ActiveTemplateId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 名单与呼叫模板的存储（每位老师 × 每个班一份）。
///
/// 为什么要存在服务器上：客户端的「呼叫」以名单为前提，而 WebUI 跑在服务器上、
/// 看不到老师手机里的那份名单 —— 同步一份上来，WebUI 才能用同一份名单、
/// 同一套拼装规则（Core 里的 CallComposer）拼出同样的话。
///
/// 为什么要按班分开：一位老师教好几个班，而"叫人"永远是在某一个班里进行的。
/// 只有一份名单的话，老师给二班同步完名单、再去三班呼叫，叫出来的是二班的学生；
/// 反过来 WebUI 上给三班呼叫时也没法知道该用哪一份。
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
                return _records.Select(r => r.UserId).Distinct(StringComparer.Ordinal).Count();
            }
        }
    }

    /// <summary>
    /// 取某位老师在某个班的名单。
    ///
    /// <paramref name="classroomUuid"/> 为空、或这个班还没有专门的一份时，
    /// 退回"没指定班级"的那一份 —— 也就是升级前老师已经同步上来的那份。
    /// </summary>
    public TeacherRosterRecord? Get(string userId, string? classroomUuid = null)
    {
        var wanted = classroomUuid?.Trim() ?? string.Empty;

        lock (_lock)
        {
            var exact = _records.FirstOrDefault(r =>
                string.Equals(r.UserId, userId, StringComparison.Ordinal) &&
                string.Equals(r.ClassroomUuid ?? string.Empty, wanted, StringComparison.OrdinalIgnoreCase));

            if (exact is not null || wanted.Length == 0)
            {
                return exact;
            }

            return _records.FirstOrDefault(r =>
                string.Equals(r.UserId, userId, StringComparison.Ordinal) &&
                string.IsNullOrEmpty(r.ClassroomUuid));
        }
    }

    /// <summary>这个班到底有没有"专门为它同步的那一份"（用于区分"用的正是本班名单"与"退回旧的那份"）。</summary>
    public bool HasExact(string userId, string classroomUuid)
    {
        lock (_lock)
        {
            return _records.Any(r =>
                string.Equals(r.UserId, userId, StringComparison.Ordinal) &&
                string.Equals(r.ClassroomUuid ?? string.Empty, classroomUuid, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>按（账号 + 班级）保存（覆盖）。</summary>
    public bool Save(TeacherRosterRecord record)
    {
        var uuid = record.ClassroomUuid ?? string.Empty;

        lock (_lock)
        {
            var index = _records.FindIndex(r =>
                string.Equals(r.UserId, record.UserId, StringComparison.Ordinal) &&
                string.Equals(r.ClassroomUuid ?? string.Empty, uuid, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>某个班被删除时清掉挂在它名下的名单。</summary>
    public bool RemoveClassroom(string classroomUuid)
    {
        lock (_lock)
        {
            var removed = _records.RemoveAll(r =>
                string.Equals(r.ClassroomUuid ?? string.Empty, classroomUuid, StringComparison.OrdinalIgnoreCase));

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
