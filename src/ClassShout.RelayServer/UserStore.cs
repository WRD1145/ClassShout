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

    public string PasswordHash { get; set; } = string.Empty;

    public string PasswordSalt { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public bool Disabled { get; set; }
}

/// <summary>注册或登录后的公开信息。刻意不含口令相关字段。</summary>
public sealed record UserProfile(string Id, string? Username, string? Email, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, bool Disabled);

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
                PasswordHash = hash,
                PasswordSalt = salt,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            _users.Add(user);
            SaveLocked();

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

    public bool SetDisabled(string id, bool disabled)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null)
            {
                return false;
            }

            user.Disabled = disabled;
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// 重置某个账号的口令（管理控制台用）。
    /// 重置后该账号的旧口令立即失效，但已签发的登录令牌仍然有效，
    /// 所以调用方还应当顺带撤销它的会话。
    /// </summary>
    public bool SetPassword(string id, string newPassword)
    {
        if (!AccountRules.IsValidPassword(newPassword))
        {
            return false;
        }

        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null)
            {
                return false;
            }

            (user.PasswordHash, user.PasswordSalt) = HashPassword(newPassword);
            SaveLocked();
            return true;
        }
    }

    private static UserProfile ToProfile(UserRecord user)
        => new(user.Id, user.Username, user.Email, user.DisplayName, user.CreatedAt, user.LastLoginAt, user.Disabled);

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

    private void SaveLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_users.ToList(), SerializerOptions);
            AtomicStateFile.Write(_statePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存用户表失败：{Path}", _statePath);
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
