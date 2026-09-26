namespace ClassShout.Core.Remote;

/// <summary>
/// 日志详细程度。
///
/// 取值刻意与常见日志库同名（Trace / Debug / Info / Warning / Error），
/// 老师或运维不用另学一套说法；数值越大越严重，过滤规则就是"低于设定档位的丢掉"。
/// </summary>
public enum AppLogLevel
{
    /// <summary>最啰嗦：每一次网络收发、每一帧。</summary>
    Trace = 0,

    /// <summary>调试：连接、发现、重试这些过程的细节。</summary>
    Debug = 1,

    /// <summary>默认：正常操作与结果。</summary>
    Info = 2,

    /// <summary>只记警告与错误。</summary>
    Warning = 3,

    /// <summary>只记错误。</summary>
    Error = 4,
}

/// <summary>档位的显示名与解析。</summary>
public static class AppLogLevels
{
    /// <summary>出厂档位。默认记"正常操作与结果"—— 这也是排障时最常需要的那一档。</summary>
    public const AppLogLevel Default = AppLogLevel.Info;

    /// <summary>按严重程度从轻到重排列，设置界面的下拉框直接用。</summary>
    public static readonly AppLogLevel[] All =
    [
        AppLogLevel.Trace,
        AppLogLevel.Debug,
        AppLogLevel.Info,
        AppLogLevel.Warning,
        AppLogLevel.Error,
    ];

    /// <summary>日志行里那个方括号里的字（与服务器那份日志同一套说法）。</summary>
    public static string Label(AppLogLevel level) => level switch
    {
        AppLogLevel.Trace => "跟踪",
        AppLogLevel.Debug => "调试",
        AppLogLevel.Warning => "警告",
        AppLogLevel.Error => "错误",
        _ => "信息",
    };

    /// <summary>设置界面里那一行说明：选它之后会多看到什么。</summary>
    public static string Describe(AppLogLevel level) => level switch
    {
        AppLogLevel.Trace => "跟踪 —— 每一次广播与每一帧收发都记（最详细，日志会长得很快）",
        AppLogLevel.Debug => "调试 —— 记连接、发现、重试的过程；排查「扫不到教室」「发不出去」看这一档",
        AppLogLevel.Info => "信息 —— 默认：正常操作与结果",
        AppLogLevel.Warning => "警告 —— 只记警告与错误",
        _ => "错误 —— 只记错误",
    };

    /// <summary>下拉框里那一行的短标签（在日志行里也用这几个字）。</summary>
    public static string ShortLabel(AppLogLevel level) => Label(level);

    /// <summary>设置界面下拉框的选项。</summary>
    public static IReadOnlyList<AppLogLevelOption> Options { get; } =
        All.Select(level => new AppLogLevelOption(level, ShortLabel(level))).ToList();

    /// <summary>解析设置里存的字符串；认不出来就退回默认档（绝不因为设置写坏而丢日志）。</summary>
    public static AppLogLevel Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        return Enum.TryParse<AppLogLevel>(value.Trim(), ignoreCase: true, out var parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : Default;
    }
}

/// <summary>
/// 日志档位的设置（存本机 <c>log.json</c>）。
///
/// 为什么单独一个文件而不是塞进各端的设置里：教室端与教师端都要用它，
/// 而各端的设置文件已经各管各的（classroom.json / teacher.json）；
/// 单独一份谁都不欠谁，将来加第三端也不必改现有文件格式。
/// </summary>
public sealed class LogSettings
{
    /// <summary>档位名（<see cref="AppLogLevel"/> 的名字）。</summary>
    public string Level { get; set; } = nameof(AppLogLevel.Info);

    /// <summary>解析出来的档位。</summary>
    public AppLogLevel Resolved => AppLogLevels.Parse(Level);

    public static LogSettings Load() => LocalSettings.Load("log.json", static () => new LogSettings());

    public bool Save() => LocalSettings.Save("log.json", this);

    /// <summary>读出来并应用到 <see cref="AppLog.Minimum"/>；返回读到的设置。</summary>
    public static LogSettings LoadAndApply()
    {
        var settings = Load();
        AppLog.Minimum = settings.Resolved;
        return settings;
    }

    /// <summary>改档位并立刻落盘、立刻生效。</summary>
    public bool Apply(AppLogLevel level)
    {
        Level = level.ToString();
        AppLog.Minimum = level;
        return Save();
    }
}

/// <summary>设置界面下拉框里的一项：档位 + 显示名。</summary>
/// <param name="Value">档位。</param>
/// <param name="Label">显示名。</param>
public sealed record AppLogLevelOption(AppLogLevel Value, string Label);
