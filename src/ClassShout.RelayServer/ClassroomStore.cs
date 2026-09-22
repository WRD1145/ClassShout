using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassShout.RelayServer;

/// <summary>一条教室注册记录。</summary>
public sealed class ClassroomRecord
{
    public string Uuid { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>口令的 PBKDF2 派生值（Base64）。服务器不保存口令明文。</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>口令的随机盐（Base64）。</summary>
    public string SecretSalt { get; set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// 教室注册表，带 JSON 文件持久化。
///
/// 为什么必须持久化：教室的 UUID 和口令是"配一次用一学期"的东西，
/// 服务器重启后如果注册记录没了，所有教室都得重新填口令，这在学期中是灾难。
/// </summary>
public sealed class ClassroomStore
{
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Dictionary<string, ClassroomRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly string _statePath;
    private readonly ILogger<ClassroomStore> _logger;

    public ClassroomStore(string statePath, ILogger<ClassroomStore> logger)
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
                return _records.Count;
            }
        }
    }

    public bool Exists(string uuid)
    {
        lock (_lock)
        {
            return _records.ContainsKey(uuid);
        }
    }

    public ClassroomRecord? Get(string uuid)
    {
        lock (_lock)
        {
            return _records.TryGetValue(uuid, out var record) ? record : null;
        }
    }

    public void Touch(string uuid)
    {
        lock (_lock)
        {
            if (_records.TryGetValue(uuid, out var record))
            {
                record.LastSeenAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// 注册或校验。三种情形：
    ///   1. UUID 不存在  → 新建注册，口令取客户端提供的，未提供则由服务器生成；
    ///   2. UUID 已存在且口令正确 → 直接通过（教室端重启后重新连接）；
    ///   3. UUID 已存在但口令错误 → 拒绝，这通常意味着有人在冒用别人的 UUID。
    /// </summary>
    public (ClassroomRecord? Record, bool IsNew, string? PlainSecret, string? Error) RegisterOrVerify(
        string uuid,
        string name,
        string? secret)
    {
        lock (_lock)
        {
            if (_records.TryGetValue(uuid, out var existing))
            {
                if (string.IsNullOrEmpty(secret))
                {
                    return (null, false, null, "该教室已注册，必须提供正确的口令才能连接。");
                }

                if (!VerifySecret(secret, existing.SecretHash, existing.SecretSalt))
                {
                    return (null, false, null, "口令不正确。若忘记口令，请由服务器管理员删除该教室的注册记录后重新注册。");
                }

                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(existing.Name, name, StringComparison.Ordinal))
                {
                    var previousName = existing.Name;
                    existing.Name = name;

                    if (!SaveLocked())
                    {
                        // 改名只是一次记账，写不进去不该拦住教室上线 ——
                        // 但内存也要跟着退回去，免得读写不一致。
                        existing.Name = previousName;
                    }
                }

                existing.LastSeenAt = DateTimeOffset.UtcNow;
                return (existing, false, secret, null);
            }

            // 新建：口令由客户端指定，未指定则服务器生成一个便于口头转达的随机串
            var plain = string.IsNullOrWhiteSpace(secret) ? GenerateSecret() : secret;
            var record = new ClassroomRecord
            {
                Uuid = uuid,
                Name = string.IsNullOrWhiteSpace(name) ? "未命名教室" : name,
                RegisteredAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            };

            (record.SecretHash, record.SecretSalt) = HashSecret(plain);
            _records[uuid] = record;

            if (!SaveLocked())
            {
                // 注册必须落盘才算数：写不进去就撤掉，
                // 否则教室端会以为自己注册好了，而服务器重启后这条记录消失，
                // 老师手上的 UUID 与口令一起作废。
                _records.Remove(uuid);
                return (null, false, null, "服务器无法写入教室注册表，注册未生效。请检查磁盘空间与文件权限。");
            }

            return (record, true, plain, null);
        }
    }

    public bool Verify(string uuid, string secret)
    {
        lock (_lock)
        {
            return _records.TryGetValue(uuid, out var record)
                   && VerifySecret(secret, record.SecretHash, record.SecretSalt);
        }
    }

    /// <summary>删除注册记录（忘记口令时由管理员使用）。返回 false 表示没删成或没写进磁盘。</summary>
    public bool Remove(string uuid)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(uuid, out var removed))
            {
                return false;
            }

            _records.Remove(uuid);

            if (!SaveLocked())
            {
                // 删不掉就放回去。返回 false 让接口如实报告失败 ——
                // 管理员点了"删除"，界面说成功、重启后教室又回来了，这最误导人。
                _records[uuid] = removed;
                return false;
            }

            return true;
        }
    }

    /// <summary>控制台用的完整记录列表（含 UUID 与时间，不含口令）。</summary>
    public IReadOnlyList<ClassroomRecord> ListForConsole()
    {
        lock (_lock)
        {
            return _records.Values.OrderByDescending(r => r.LastSeenAt).ToList();
        }
    }

    /// <summary>供旧版管理接口使用的摘要列表。刻意不含口令或其派生值。</summary>
    public IReadOnlyList<object> ExportSummaries()
    {
        lock (_lock)
        {
            return _records.Values
                .OrderByDescending(record => record.LastSeenAt)
                .Select(record => (object)new
                {
                    record.Uuid,
                    record.Name,
                    record.RegisteredAt,
                    record.LastSeenAt,
                })
                .ToList();
        }
    }

    // ======================== 口令处理 ========================

    /// <summary>
    /// 用 PBKDF2 派生，而不是直接存 SHA256。
    /// 教室口令一般是老师自己想出来的短词，熵很低，必须加慢哈希与随机盐，
    /// 否则服务器文件一旦泄露，口令可被离线爆破。
    /// </summary>
    private static (string Hash, string Salt) HashSecret(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool VerifySecret(string secret, string hashBase64, string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, expected.Length);

            // 固定时间比较，避免通过响应耗时逐字节试探口令
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>生成便于口述与手输的口令：去掉容易看错的 0/O/1/I/l。</summary>
    private static string GenerateSecret()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return string.Create(8, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }

    // ======================== 持久化 ========================

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
            var snapshot = _records.Values.ToList();
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);

            // 先写临时文件再替换，避免写一半断电导致状态文件损坏
            AtomicStateFile.Write(_statePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存注册表失败：{Path}", _statePath);
            return false;
        }
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            _logger.LogInformation("未找到注册表文件，将从空开始：{Path}", _statePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var records = JsonSerializer.Deserialize<List<ClassroomRecord>>(json, SerializerOptions);
            if (records is null)
            {
                return;
            }

            foreach (var record in records)
            {
                if (!string.IsNullOrWhiteSpace(record.Uuid))
                {
                    _records[record.Uuid] = record;
                }
            }

            _logger.LogInformation("已载入 {Count} 条教室注册记录。", _records.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "载入注册表失败，将从空开始：{Path}", _statePath);
        }
    }
}
