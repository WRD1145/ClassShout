using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ClassShout.RelayServer;

/// <summary>一个教师端的绑定会话。</summary>
public sealed class TeacherBinding
{
    public string Token { get; init; } = string.Empty;

    public string ClassroomUuid { get; init; } = string.Empty;

    public string TeacherName { get; init; } = string.Empty;

    /// <summary>绑定时已登录用户的 Id；未登录时为空。</summary>
    public string? UserId { get; init; }

    public DateTimeOffset BoundAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>累计收到的音频字节数，便于运维观察链路是否真的在传音频。</summary>
    public long AudioBytes { get; set; }
}

/// <summary>
/// 会话表：谁绑定了哪个教室、教室端的会话令牌是否有效。
///
/// 全部放内存而不落盘：会话是短生命周期的，服务器重启后两端自动重连即可，
/// 持久化反而会引入"陈旧会话"这类需要清理的麻烦。
/// 需要长期保存的只有教室注册记录，那部分在 <see cref="ClassroomStore"/> 里。
/// </summary>
public sealed class RelaySessions
{
    private readonly ConcurrentDictionary<string, TeacherBinding> _teachers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _teachersByClassroom = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _classroomTokens = new(StringComparer.Ordinal);

    /// <summary>为教室端签发会话令牌。同一教室重复注册会拿到新令牌，旧令牌随即失效。</summary>
    public string IssueClassroomToken(string uuid)
    {
        var token = NewToken();
        _classroomTokens[token] = uuid;
        return token;
    }

    public bool ValidateClassroomToken(string uuid, string? token)
        => !string.IsNullOrEmpty(token)
           && _classroomTokens.TryGetValue(token, out var boundUuid)
           && string.Equals(boundUuid, uuid, StringComparison.OrdinalIgnoreCase);

    public void RevokeClassroomTokens(string uuid)
    {
        foreach (var (token, bound) in _classroomTokens)
        {
            if (string.Equals(bound, uuid, StringComparison.OrdinalIgnoreCase))
            {
                _classroomTokens.TryRemove(token, out _);
            }
        }
    }

    public TeacherBinding BindTeacher(string uuid, string teacherName, string? userId = null)
    {
        var binding = new TeacherBinding
        {
            Token = NewToken(),
            ClassroomUuid = uuid,
            TeacherName = string.IsNullOrWhiteSpace(teacherName) ? "教师端" : teacherName,
            UserId = userId,
        };

        _teachers[binding.Token] = binding;
        _teachersByClassroom.GetOrAdd(uuid, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[binding.Token] = 0;

        return binding;
    }

    public bool TryGetTeacher(string token, out TeacherBinding binding) => _teachers.TryGetValue(token, out binding!);

    public void UnbindTeacher(string token)
    {
        if (!_teachers.TryRemove(token, out var binding))
        {
            return;
        }

        if (_teachersByClassroom.TryGetValue(binding.ClassroomUuid, out var tokens))
        {
            tokens.TryRemove(token, out _);
        }
    }

    /// <summary>列出某个教室当前绑定的教师会话令牌。</summary>
    public IReadOnlyList<string> TeacherTokensOf(string uuid)
        => _teachersByClassroom.TryGetValue(uuid, out var tokens) ? [.. tokens.Keys] : [];

    /// <summary>教室端上线/离线时，把所有绑定教师标记一次，便于教师端界面显示状态。</summary>
    public int TeacherCountOf(string uuid) => _teachersByClassroom.TryGetValue(uuid, out var tokens) ? tokens.Count : 0;

    /// <summary>当前所有教室绑定的教师会话总数（概览统计用）。</summary>
    public int TeacherCountTotal() => _teachers.Count;

    private static string NewToken()
    {
        // 32 字节随机数的 Base64Url 形式，既是会话标识也当作凭据，不可预测
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
