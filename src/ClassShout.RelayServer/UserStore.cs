using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClassShout.RelayServer;

/// <summary>一个用户账号。</summary>
public sealed class UserRecord
{
    public string Id { get; set; } = string.Empty;

    /// <summary>用户名，登录用。可为空（只注册了邮箱时）。</summary>
    public string? Username { get; set; }

    /// <summary>邮箱，登录用。可为空（只注册了用户名时）。</summary>
    public string? Email { get; set; }

    /// <summary>显示名，即"老师姓名"。教室端弹窗与教师端界面都显示它。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 任教科目，例如"数学"。可为空。
    ///
    /// 它出现在喊话来源里（"数学张老师"），因为教室里最常见的疑问就是
    /// "这是谁在说话"—— 同一间教室一天里会有好几位老师来喊，
    /// 只报姓名往往对不上人，加上科目就一眼能认出来。
    /// 刻意做成自由文本而不是固定列表：各校的科目叫法不一样（"道法""信息技术"），
    /// 而这份数据是给本校人看的。
    /// </summary>
    public string? Subject { get; set; }

    public string PasswordHash { get; set; } = string.Empty;

    public string PasswordSalt { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public bool Disabled { get; set; }
}

/// <summary>注册或登录后的公开信息。刻意不含口令相关字段。</summary>
public sealed record UserProfile(
    string Id,
    string? Username,
    string? Email,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    bool Disabled,
    string? Subject = null)
{
    /// <summary>喊话来源里显示的名字：有科目就带上（"数学张老师"），没有就只报姓名。</summary>
    public string ShoutName => string.IsNullOrWhiteSpace(Subject) ? DisplayName : $"{Subject}{DisplayName}";
}

/// <summary>用户名与邮箱的格式校验。集中在一处，注册与改资料共用同一套规则。</summary>
public static partial class AccountRules
{
    public const int MinPasswordLength = 6;
    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = 20;

    private static readonly Regex UsernamePattern = UsernameRegex();

    private static readonly Regex EmailPattern = EmailRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{2,19}$")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    public static bool IsValidUsername(string? username)
        => !string.IsNullOrWhiteSpace(username) && UsernamePattern.IsMatch(username);

    public static bool IsValidEmail(string? email)
        => !string.IsNullOrWhiteSpace(email) && EmailPattern.IsMatch(email);

    public static bool IsValidPassword(string? password)
        => !string.IsNullOrEmpty(password) && password.Length >= MinPasswordLength;
}

/// <summary>
/// 用户账号存储，带 JSON 文件持久化。
///
/// 与教室注册表一样必须落盘：账号是老师长期使用的东西，
/// 服务器重启后全部丢失会直接导致所有老师无法登录。
/// </summary>
public sealed class UserStore
{
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly List<UserRecord> _users = [];
    private readonly Lock _lock = new();
    private readonly string _statePath;
    private readonly ILogger<UserStore> _logger;

    public UserStore(string statePath, ILogger<UserStore> logger)
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
                return _users.Count;
            }
        }
    }

    public IReadOnlyList<UserProfile> List()
    {
        lock (_lock)
        {
            return _users
                .OrderByDescending(u => u.CreatedAt)
                .Select(ToProfile)
                .ToList();
        }
    }

    /// <summary>
    /// 注册新账号。
    /// 用户名与邮箱至少要有一个，两个都填时都可用于登录。
    /// </summary>
    public (UserProfile? Profile, string? Error) Register(
        string? username,
        string? email,
        string? subject,
        string displayName,
        string password)
    {
        username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

        if (username is null && email is null)
        {
            return (null, "用户名与邮箱至少填写一个。");
        }

        if (username is not null && !AccountRules.IsValidUsername(username))
        {
            return (null, $"用户名需以字母开头，由 3~20 位字母、数字或下划线组成。");
        }

        if (email is not null && !AccountRules.IsValidEmail(email))
        {
            return (null, "邮箱格式不正确。");
        }

        if (!AccountRules.IsValidPassword(password))
        {
            return (null, $"口令至少 {AccountRules.MinPasswordLength} 位。");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            // 没填显示名就退而用用户名或邮箱前缀，保证界面上总有东西可显示
            displayName = username ?? email!.Split('@')[0];
        }

        lock (_lock)
        {
            if (username is not null && _users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "该用户名已被注册。");
            }

            if (email is not null && _users.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "该邮箱已被注册。");
            }

            var (hash, salt) = HashPassword(password);
            var user = new UserRecord
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Username = username,
                Email = email,
                DisplayName = displayName.Trim(),
                Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
                PasswordHash = hash,
                PasswordSalt = salt,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            _users.Add(user);

            if (!SaveLocked())
            {
                // 内存与磁盘必须一起前进。写不进去就把这条记录撤掉，
                // 否则会出现"注册成功、重启后账号消失"—— 老师那边表现为
                // 今天还能登录、明天口令就不对了，且没有任何人改过东西。
                _users.Remove(user);
                return (null, "服务器无法写入用户表，注册未生效。请检查磁盘空间与文件权限。");
            }

            _logger.LogInformation("新用户注册：{Display}（用户名 {Username}，邮箱 {Email}）",
                user.DisplayName, user.Username ?? "-", user.Email ?? "-");

            return (ToProfile(user), null);
        }
    }

    /// <summary>登录。<paramref name="account"/> 可以是用户名，也可以是邮箱。</summary>
    public (UserProfile? Profile, string? Error) Login(string account, string password)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
        {
            return (null, "请填写账号与口令。");
        }

        var key = account.Trim();

        lock (_lock)
        {
            var user = _users.FirstOrDefault(u =>
                (u.Username is not null && string.Equals(u.Username, key, StringComparison.OrdinalIgnoreCase)) ||
                (u.Email is not null && string.Equals(u.Email, key, StringComparison.OrdinalIgnoreCase)));

            // 账号不存在与口令错误返回同一句话：不泄露"这个账号是否存在"
            if (user is null || !VerifyPassword(password, user.PasswordHash, user.PasswordSalt))
            {
                return (null, "账号或口令不正确。");
            }

            if (user.Disabled)
            {
                return (null, "该账号已被停用，请联系管理员。");
            }

            // 登录时间只是记账，写不进去不该让登录失败 —— 为它拒绝一次正常登录
            // 反而更糟。这里明确忽略返回值。
            user.LastLoginAt = DateTimeOffset.UtcNow;
            SaveLocked();

            return (ToProfile(user), null);
        }
    }

    public UserProfile? FindById(string id)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            return user is null ? null : ToProfile(user);
        }
    }

    /// <summary>停用 / 启用账号。返回 false 表示没有生效（账号不存在，或写盘失败）。</summary>
    public bool SetDisabled(string id, bool disabled)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null)
            {
                return false;
            }

            var previous = user.Disabled;
            user.Disabled = disabled;

            if (!SaveLocked())
            {
                user.Disabled = previous;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 重置某个账号的口令（管理控制台用）。
    /// 重置后该账号的旧口令立即失效，但已签发的登录令牌仍然有效，
    /// 所以调用方还应当顺带撤销它的会话。
    ///
    /// 返回错误文案而不是简单的 bool：写盘失败和"账号不存在"是完全两回事，
    /// 都回一句"账号或口令不符合要求"会把运维引到错误的方向。
    /// </summary>
    public (bool Ok, string? Error) SetPassword(string id, string newPassword)
    {
        if (!AccountRules.IsValidPassword(newPassword))
        {
            return (false, $"口令至少 {AccountRules.MinPasswordLength} 位。");
        }

        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null)
            {
                return (false, "账号不存在。");
            }

            var previousHash = user.PasswordHash;
            var previousSalt = user.PasswordSalt;
            (user.PasswordHash, user.PasswordSalt) = HashPassword(newPassword);

            if (!SaveLocked())
            {
                // 回滚，否则会出现"界面说改好了、重启后又是旧口令"——
                // 那种现象最难排查，运维多半会以为自己记错了。
                (user.PasswordHash, user.PasswordSalt) = (previousHash, previousSalt);
                return (false, "服务器无法写入用户表，口令未修改。请检查磁盘空间与文件权限。");
            }

            return (true, null);
        }
    }

    private static UserProfile ToProfile(UserRecord user)
        => new(user.Id, user.Username, user.Email, user.DisplayName, user.CreatedAt, user.LastLoginAt, user.Disabled, user.Subject);

    // ======================== 口令处理 ========================

    private static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool VerifyPassword(string password, string hashBase64, string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, expected.Length);

            // 固定时间比较，避免通过响应耗时逐字节试探口令
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
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
            var json = JsonSerializer.Serialize(_users.ToList(), SerializerOptions);
            AtomicStateFile.Write(_statePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存用户表失败：{Path}", _statePath);
            return false;
        }
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            _logger.LogInformation("未找到用户表，将从空开始：{Path}", _statePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var loaded = JsonSerializer.Deserialize<List<UserRecord>>(json, SerializerOptions);
            if (loaded is not null)
            {
                _users.AddRange(loaded.Where(u => !string.IsNullOrWhiteSpace(u.Id)));
            }

            _logger.LogInformation("已载入 {Count} 个用户账号。", _users.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "载入用户表失败，将从空开始：{Path}", _statePath);
        }
    }
}
