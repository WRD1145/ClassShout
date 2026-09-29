using System.Text.Json;
using System.Text.Json.Serialization;
using ClassShout.Core.Remote;

namespace ClassShout.RelayServer;

/// <summary>
/// 班主任给某个班上传的统一名单。
///
/// 与"老师自己那份"（<see cref="TeacherRosterRecord"/>）是两码事：
///   · 老师自己那份 —— 每人每班一份，谁也看不到别人的；
///   · 这一份 —— **属于班级**：同一个班的老师看到的是同一份，由班主任维护。
///
/// 为什么要有它：一个班里几位老师各录一份名单，叫出来的人可能对不上
/// （转学生补了没有、谁已经调班了）。班主任把它统一起来，全班就用同一份；
/// 再打开 <see cref="Enforced"/> 之后，任课老师连上传入口都没有了 ——
/// 一个班到底按哪份名单叫人，必须只有一个答案。
/// </summary>
public sealed class ClassroomRosterRecord
{
    /// <summary>教室 UUID。</summary>
    public string ClassroomUuid { get; set; } = string.Empty;

    /// <summary>班主任上传的名单。</summary>
    public List<StudentRoster> Rosters { get; set; } = [];

    public string? ActiveRosterId { get; set; }

    /// <summary>
    /// 是否强制：开了之后这个班的任课老师只能用这一份，不能再上传自己的。
    /// 关着的时候它只是一份"默认名单"—— 任课老师自己没传时用它，传了就用自己的。
    /// </summary>
    public bool Enforced { get; set; }

    /// <summary>最后修改的人（一个班可能有两位班主任，出了事要知道找谁）。</summary>
    public string? UpdatedByUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 班级统一名单的存储（每个班一条）。
///
/// 与其它状态文件同样的规矩：单独一个文件、原子写、写失败要能让接口如实报错。
/// </summary>
public sealed class ClassroomRosterStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<ClassroomRosterRecord> _records = [];
    private readonly Lock _lock = new();
    private readonly string _statePath;
    private readonly ILogger<ClassroomRosterStore> _logger;

    public ClassroomRosterStore(string statePath, ILogger<ClassroomRosterStore> logger)
    {
        _statePath = statePath;
        _logger = logger;
        Load();
    }

    /// <summary>有统一名单的班级数（控制台概览用）。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _records.Count(r => r.Rosters.Count > 0);
            }
        }
    }

    public ClassroomRosterRecord? Get(string? classroomUuid)
    {
        var wanted = classroomUuid?.Trim();

        if (string.IsNullOrEmpty(wanted))
        {
            return null;
        }

        lock (_lock)
        {
            return _records.FirstOrDefault(r =>
                string.Equals(r.ClassroomUuid, wanted, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>保存（覆盖）某个班的统一名单。</summary>
    public bool Save(ClassroomRosterRecord record)
    {
        lock (_lock)
        {
            var index = _records.FindIndex(r =>
                string.Equals(r.ClassroomUuid, record.ClassroomUuid, StringComparison.OrdinalIgnoreCase));

            ClassroomRosterRecord? previous = index >= 0 ? _records[index] : null;

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

            // 写盘失败就把内存改回去：接口不能一边说"存好了"、一边重启后什么都不剩
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

    /// <summary>班级被删除时清掉它的统一名单。</summary>
    public bool Remove(string classroomUuid)
    {
        lock (_lock)
        {
            var removed = _records.RemoveAll(r =>
                string.Equals(r.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase));

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
            _logger.LogError(ex, "保存班级统一名单失败：{Path}", _statePath);
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
            var loaded = JsonSerializer.Deserialize<List<ClassroomRosterRecord>>(
                File.ReadAllText(_statePath), SerializerOptions);

            if (loaded is not null)
            {
                _records.AddRange(loaded.Where(r => !string.IsNullOrWhiteSpace(r.ClassroomUuid)));
            }

            _logger.LogInformation("已载入 {Count} 个班级的统一名单。", _records.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "载入班级统一名单失败，将从空开始：{Path}", _statePath);
        }
    }
}
