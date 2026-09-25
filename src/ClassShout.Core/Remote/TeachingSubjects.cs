namespace ClassShout.Core.Remote;

/// <summary>
/// 「这位老师在这间教室里教什么」—— 任教科目的按班级覆盖。
///
/// 为什么需要它：一位老师常常教好几个班，而且**在不同班教不同科目**
/// （信息技术老师给三个班上信息课、顺手还给一个班带数学）。
/// 只记一个科目的话，教室里看到的来源永远是错的那么一半。
///
/// 两处必须用同一套规则算出同一个名字，所以放在 Core 里共用：
///   · 服务器贴到喊话上的来源（权威 —— 客户端自填的姓名不可信）；
///   · 教师端在**局域网直连**时自己贴的来源（那条路不经过服务器）。
///
/// 规则很短，但错了很难发现：名字看着正常，只是科目那两个字不对。
/// </summary>
public static class TeachingSubjects
{
    /// <summary>没有按班级覆盖时用的那一份。</summary>
    public static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取这位老师在某个班的科目：先看这个班有没有单独指定，没有就用账号上的默认科目。
    /// </summary>
    /// <param name="defaultSubject">账号上的默认科目，可为空。</param>
    /// <param name="byClassroom">按班级的覆盖表，可为空。</param>
    /// <param name="uuid">教室 UUID，可为空（为空时直接用默认科目）。</param>
    public static string? For(string? defaultSubject, IReadOnlyDictionary<string, string>? byClassroom, string? uuid)
    {
        if (!string.IsNullOrWhiteSpace(uuid) &&
            byClassroom is not null &&
            byClassroom.TryGetValue(uuid.Trim(), out var specific) &&
            !string.IsNullOrWhiteSpace(specific))
        {
            return specific.Trim();
        }

        return string.IsNullOrWhiteSpace(defaultSubject) ? null : defaultSubject.Trim();
    }

    /// <summary>
    /// 拼成教室里看到的那个来源：「数学张老师」。没科目就只报姓名。
    /// </summary>
    public static string Format(string? subject, string displayName)
        => string.IsNullOrWhiteSpace(subject) ? displayName : $"{subject.Trim()}{displayName}";

    /// <summary>一次算完：这位老师在某个班的喊话来源。</summary>
    public static string ShoutName(
        string displayName,
        string? defaultSubject,
        IReadOnlyDictionary<string, string>? byClassroom,
        string? uuid)
        => Format(For(defaultSubject, byClassroom, uuid), displayName);

    /// <summary>
    /// 收拾一份覆盖表：去掉空科目与空 UUID、裁掉首尾空白、统一按小写不敏感的键。
    ///
    /// 不做"空表就退回默认"这种事 —— 空表示"这个班没特殊要求"，
    /// 而它和"默认科目也是空"是两回事，混起来会让某位老师的科目莫名其妙地冒出来。
    /// </summary>
    public static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string?>? byClassroom)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (byClassroom is null)
        {
            return result;
        }

        foreach (var (uuid, subject) in byClassroom)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(subject))
            {
                continue;
            }

            result[uuid.Trim()] = subject.Trim();
        }

        return result;
    }

    /// <summary>
    /// 存下来的那一份（值非空）转成"可以往里写 null"的那一份。
    ///
    /// 读与写共用同一个形状：GET 拿回来的东西改一改就能 POST 回去，
    /// 少一份需要同步维护的模型。值的可空性不一样，转换要在这里做一次。
    /// </summary>
    public static Dictionary<string, string?>? AsNullable(IReadOnlyDictionary<string, string>? byClassroom)
    {
        if (byClassroom is null || byClassroom.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (uuid, subject) in byClassroom)
        {
            result[uuid] = subject;
        }

        return result;
    }
}
