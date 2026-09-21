using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ClassShout.RelayServer;

/// <summary>
/// 登录会话。
///
/// 只用内存：会话是短生命周期的东西，服务器重启后老师重新登录即可。
/// 持久化反而会带来"长期不失效的令牌"这种安全隐患。
/// 需要长期保存的只有账号本身，那部分在 <see cref="UserStore"/> 里。
/// </summary>
public sealed class UserSessions
{
    /// <summary>令牌有效期。给足一个学期，过期后重新登录。</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    /// <summary>为用户签发令牌。</summary>
    public string Issue(string userId, bool isBuiltInAdmin = false)
    {
        Purge();

        var token = NewToken();
        _sessions[token] = new Session(userId, isBuiltInAdmin, DateTimeOffset.UtcNow + Lifetime);
        return token;
    }

    /// <summary>解析令牌，返回用户 Id；无效或过期返回 null。</summary>
    public string? Resolve(string? token) => ResolveSession(token)?.UserId;

    /// <summary>解析令牌并判断是否为内置管理员会话。</summary>
    public bool IsAdminSession(string? token) => ResolveSession(token)?.IsBuiltInAdmin == true;

    private Session? ResolveSession(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var session))
        {
            return null;
        }

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        return session;
    }

    /// <summary>撤销所有内置管理员会话（管理员改口令后要求重新登录）。</summary>
    public void RevokeAdminSessions()
    {
        foreach (var (token, session) in _sessions)
        {
            if (session.IsBuiltInAdmin)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            _sessions.TryRemove(token, out _);
        }
    }

    /// <summary>让某个用户的所有令牌失效（停用账号时用）。</summary>
    public void RevokeAllOf(string userId)
    {
        foreach (var (token, session) in _sessions)
        {
            if (session.UserId == userId)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, session) in _sessions)
        {
            if (session.ExpiresAt <= now)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed record Session(string UserId, bool IsBuiltInAdmin, DateTimeOffset ExpiresAt);
}
