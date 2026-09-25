using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassShout.RelayServer;

/// <summary>
/// 一个分享出去的绑定链接。
/// </summary>
public sealed class ShareLinkRecord
{
    /// <summary>链接里的令牌。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>这份链接覆盖的教室 UUID。</summary>
    public List<string> ClassroomUuids { get; set; } = [];

    /// <summary>谁生成的（管理员的显示名），用于审计与显示。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>过期时间。过了就一律拒绝。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>已经被哪些账号用过（账号 Id）。</summary>
    public List<string> ClaimedBy { get; set; } = [];

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}

/// <summary>
/// 分享链接的存储。
///
/// 和授权表一样落盘：管理员上午生成链接、下午才发给老师，
/// 中间服务器重启一次就全失效的话，这个功能在真实运维里没法用。
///
/// 链接自身是**凭据**：拿到它的人就能把自己绑定到这几个班。
/// 所以它有三个约束：有过期时间、可被管理员撤销、以及兑现时必须先登录 ——
/// 最后一条让"谁绑的"始终有据可查，而不是匿名扩散。
/// </summary>
public sealed class ShareStore
{
    /// <summary>默认有效期。一次教研活动、一个学期初的排班，都够用。</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    /// <summary>同一时刻最多留多少条。旧链接过期后会被顺手清掉。</summary>
    private const int MaxLinks = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ConcurrentDictionary<string, ShareLinkRecord> _links = new(StringComparer.Ordinal);
    private readonly string _statePath;
    private readonly ILogger<ShareStore> _logger;
    private readonly Lock _saveLock = new();

    public ShareStore(string statePath, ILogger<ShareStore> logger)
    {
        _statePath = statePath;
        _logger = logger;
        Load();
    }

    public int Count => _links.Count;

    /// <summary>生成一条新的分享链接。</summary>
    public ShareLinkRecord Create(IEnumerable<string> classroomUuids, string createdBy, TimeSpan? lifetime = null)
    {
        var link = new ShareLinkRecord
        {
            Token = NewToken(),
            ClassroomUuids = classroomUuids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CreatedBy = createdBy,
            ExpiresAt = DateTimeOffset.UtcNow + (lifetime ?? DefaultLifetime),
        };

        _links[link.Token] = link;
        Prune();
        Save();

        return link;
    }

    /// <summary>按令牌取一条，顺带判过期。</summary>
    public ShareLinkRecord? Get(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_links.TryGetValue(token, out var link))
        {
            return null;
        }

        if (link.IsExpired(DateTimeOffset.UtcNow))
        {
            return null;
        }

        return link;
    }

    /// <summary>撤销一条链接。返回是否存在。</summary>
    public bool Revoke(string token)
    {
        var removed = _links.TryRemove(token, out _);

        if (removed)
        {
            Save();
        }

        return removed;
    }

    /// <summary>管理员用的列表（含过期，便于看清有哪些还活着）。</summary>
    public IReadOnlyList<ShareLinkRecord> List()
        => _links.Values.OrderByDescending(link => link.CreatedAt).ToList();

    /// <summary>记下"这个账号用这条链接绑过"，并落盘。</summary>
    public void MarkClaimed(ShareLinkRecord link, string userId)
    {
        lock (_saveLock)
        {
            if (!link.ClaimedBy.Contains(userId, StringComparer.Ordinal))
            {
                link.ClaimedBy.Add(userId);
            }
        }

        Save();
    }

    /// <summary>清掉过期太久的链接，避免文件无限增长。</summary>
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (token, link) in _links)
        {
            // 过期后再留一天：管理员可能正在排查"为什么老师点不开"，
            // 立刻删掉会让"链接已过期"和"链接不存在"变得无法区分。
            if (link.ExpiresAt + TimeSpan.FromDays(1) < now)
            {
                _links.TryRemove(token, out _);
            }
        }

        if (_links.Count <= MaxLinks)
        {
            return;
        }

        foreach (var stale in _links.Values.OrderBy(link => link.CreatedAt).Take(_links.Count - MaxLinks))
        {
            _links.TryRemove(stale.Token, out _);
        }
    }

    /// <summary>令牌用密码学随机数，不是 Guid —— 它是一条凭据。</summary>
    private static string NewToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private void Save()
    {
        try
        {
            AtomicStateFile.Write(_statePath, JsonSerializer.Serialize(_links.Values.ToList(), SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存分享链接失败：{Path}", _statePath);
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
            var loaded = JsonSerializer.Deserialize<List<ShareLinkRecord>>(
                File.ReadAllText(_statePath), SerializerOptions);

            if (loaded is null)
            {
                return;
            }

            foreach (var link in loaded)
            {
                if (!string.IsNullOrWhiteSpace(link.Token))
                {
                    _links[link.Token] = link;
                }
            }

            Prune();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "载入分享链接失败，将从空开始：{Path}", _statePath);
        }
    }
}
