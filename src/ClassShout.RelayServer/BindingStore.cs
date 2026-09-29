using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassShout.RelayServer;

/// <summary>
/// 一条「某位老师有权使用某个教室」的授权。
///
/// 它和教室口令是两条并行的通路，区别在于谁来说"可以"：
///   · 教室口令 —— 教室端把口令交给老师，老师自己填。适合没有控制台的场合；
///   · 绑定授权 —— 管理员（或这个班的班主任）直接把班级指给某位老师。老师手机上一点即用，
///     不必抄 UUID、也不必传口令。口令一旦转发就会扩散，授权则始终收在服务器上。
/// </summary>
public sealed class ClassroomBinding
{
    /// <summary>被授权的用户 Id。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>被授权的教室 UUID。</summary>
    public string ClassroomUuid { get; set; } = string.Empty;

    /// <summary>
    /// 这条授权是不是"班主任"授权。
    ///
    /// 班主任与普通任课老师用的是同一张表：他们都能给这个班喊话，差别只在
    /// 班主任**还能管这个班的权限**。分成两张表的话，"他到底有没有这个班的权限"
    /// 每次都要问两处，而两处迟早会出现"授权了但喊不了"这种自相矛盾的状态。
    ///
    /// 老数据里没有这个字段，反序列化出来是 false —— 正好就是"普通任课老师"，
    /// 与升级前的行为一致。
    /// </summary>
    public bool AsHeadTeacher { get; set; }

    /// <summary>授权人（管理员账号名或班主任姓名），便于事后追查是谁开的权限。</summary>
    public string GrantedBy { get; set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>绑定授权表，带 JSON 文件持久化。</summary>
public sealed class BindingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly List<ClassroomBinding> _bindings = [];
    private readonly Lock _lock = new();
    private readonly string _statePath;
    private readonly ILogger<BindingStore> _logger;

    public BindingStore(string statePath, ILogger<BindingStore> logger)
    {
        _statePath = statePath;
        _logger = logger;
        Load();
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _bindings.Count;
            }
        }
    }

    public IReadOnlyList<ClassroomBinding> All()
    {
        lock (_lock)
        {
            return _bindings.ToList();
        }
    }

    /// <summary>某位老师被授权使用的教室 UUID。</summary>
    public IReadOnlyList<string> ClassroomsOf(string userId)
    {
        lock (_lock)
        {
            return _bindings
                .Where(b => b.UserId == userId)
                .Select(b => b.ClassroomUuid)
                .ToList();
        }
    }

    /// <summary>某位老师**当班主任**的那些教室 UUID —— 他能管权限的就是这几间。</summary>
    public IReadOnlyList<string> HeadTeacherClassroomsOf(string userId)
    {
        lock (_lock)
        {
            return _bindings
                .Where(b => b.UserId == userId && b.AsHeadTeacher)
                .Select(b => b.ClassroomUuid)
                .ToList();
        }
    }

    /// <summary>某位老师在这个班是不是班主任。</summary>
    public bool IsHeadTeacherOf(string userId, string classroomUuid)
    {
        lock (_lock)
        {
            return _bindings.Any(b =>
                b.UserId == userId &&
                b.AsHeadTeacher &&
                string.Equals(b.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>某个教室的全部授权（含是不是班主任），控制台与"我的班级"都用它。</summary>
    public IReadOnlyList<ClassroomBinding> OfClassroom(string classroomUuid)
    {
        lock (_lock)
        {
            return _bindings
                .Where(b => string.Equals(b.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>某个教室被授权给哪些老师。</summary>
    public IReadOnlyList<string> UsersOf(string classroomUuid)
    {
        lock (_lock)
        {
            return _bindings
                .Where(b => string.Equals(b.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase))
                .Select(b => b.UserId)
                .ToList();
        }
    }

    public bool IsAuthorized(string userId, string classroomUuid)
    {
        lock (_lock)
        {
            return _bindings.Any(b =>
                b.UserId == userId &&
                string.Equals(b.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 授权。已存在则返回 false，让调用方能区分"新建"与"本来就有"。
    /// 返回 <paramref name="Ok"/> 为 false 且 <paramref name="AlreadyExists"/> 为 false
    /// 时表示写盘失败 —— 调用方必须如实报告，否则管理员会以为授权成功了。
    ///
    /// 已经授权过的老师再被指定为班主任时**升级那条记录**（而不是报"本来就有"）：
    /// 管理员在控制台上做的事就是"把张老师也设成二班班主任"，回一句"已存在"
    /// 会让人以为没生效。
    /// </summary>
    public (bool Ok, bool AlreadyExists) Grant(string userId, string classroomUuid, string grantedBy, bool asHeadTeacher = false)
    {
        lock (_lock)
        {
            var existing = _bindings.FirstOrDefault(b =>
                b.UserId == userId &&
                string.Equals(b.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                if (!asHeadTeacher || existing.AsHeadTeacher)
                {
                    return (false, true);
                }

                existing.AsHeadTeacher = true;
                existing.GrantedBy = grantedBy;
                existing.GrantedAt = DateTimeOffset.UtcNow;

                if (!SaveLocked())
                {
                    existing.AsHeadTeacher = false;
                    return (false, true);
                }

                _logger.LogInformation("{By} 把教室 {Uuid} 的班主任权限加给了用户 {UserId}", grantedBy, classroomUuid, userId);
                return (true, false);
            }

            var binding = new ClassroomBinding
            {
                UserId = userId,
                ClassroomUuid = classroomUuid,
                AsHeadTeacher = asHeadTeacher,
                GrantedBy = grantedBy,
                GrantedAt = DateTimeOffset.UtcNow,
            };

            _bindings.Add(binding);

            if (!SaveLocked())
            {
                // 授权没落盘就撤掉，否则管理员今天点完、明天重启后授权凭空消失，
                // 老师那边则表现为"昨天还能一键绑定，今天又要口令"。
                _bindings.Remove(binding);
                return (false, false);
            }

            _logger.LogInformation("{By} 把教室 {Uuid} 授权给用户 {UserId}{Head}",
                grantedBy, classroomUuid, userId, asHeadTeacher ? "（班主任）" : string.Empty);
            return (true, false);
        }
    }

    public bool Revoke(string userId, string classroomUuid)
    {
        lock (_lock)
        {
            var removed = _bindings.Where(b =>
                b.UserId == userId &&
                string.Equals(b.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase)).ToList();

            if (removed.Count == 0)
            {
                return false;
            }

            _bindings.RemoveAll(b => removed.Contains(b));

            if (!SaveLocked())
            {
                _bindings.AddRange(removed);
                return false;
            }

            return true;
        }
    }

    /// <summary>教室被删除时清掉它的所有授权，避免留下指向不存在教室的悬空记录。</summary>
    public int RevokeClassroom(string classroomUuid)
    {
        lock (_lock)
        {
            var removed = _bindings.Where(b =>
                string.Equals(b.ClassroomUuid, classroomUuid, StringComparison.OrdinalIgnoreCase)).ToList();

            if (removed.Count == 0)
            {
                return 0;
            }

            _bindings.RemoveAll(b => removed.Contains(b));

            if (!SaveLocked())
            {
                _bindings.AddRange(removed);
                return 0;
            }

            return removed.Count;
        }
    }

    public int RevokeUser(string userId)
    {
        lock (_lock)
        {
            var removed = _bindings.Where(b => b.UserId == userId).ToList();
            if (removed.Count == 0)
            {
                return 0;
            }

            _bindings.RemoveAll(b => removed.Contains(b));

            if (!SaveLocked())
            {
                _bindings.AddRange(removed);
                return 0;
            }

            return removed.Count;
        }
    }

    /// <summary>
    /// 落盘。返回 false 表示这次修改没有写到磁盘上 —— 调用方必须据此回滚内存状态。
    ///
    /// 不返回结果是个隐蔽的坑：写入失败时只有一行日志，接口照样回"成功"，
    /// 于是老师看到"注册成功"，重启服务器后账号却不见了；
    /// 或者管理员改了口令、界面上说改好了，重启后又变回旧口令。
    /// 内存与磁盘要么一起前进，要么都不动。
    /// </summary>
    private bool SaveLocked()
    {
        try
        {
            AtomicStateFile.Write(_statePath, JsonSerializer.Serialize(_bindings.ToList(), SerializerOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存授权表失败：{Path}", _statePath);
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
            var loaded = JsonSerializer.Deserialize<List<ClassroomBinding>>(File.ReadAllText(_statePath), SerializerOptions);
            if (loaded is not null)
            {
                _bindings.AddRange(loaded.Where(b => !string.IsNullOrWhiteSpace(b.UserId)));
            }

            _logger.LogInformation("已载入 {Count} 条班级授权记录。", _bindings.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "载入授权表失败：{Path}", _statePath);
        }
    }
}
