using System.Text;

namespace ClassShout.Core.Remote;

/// <summary>
/// 落盘的运行日志。
///
/// 界面上那份日志是给人当场看的（而且一关就没了），但排障时最常见的情形是
/// "昨天下午那节课教室里没声音" —— 那时只能看文件。所以每条日志同时写一份到磁盘。
///
/// 几条刻意的做法：
///
/// · **按天分文件**（<c>logs/classshout-2026-09-26.log</c>）。单个文件一直追加的话，
///   几个月后它会大到打不开，而"按天"正好对得上"那节课是几号"这个问题。
/// · **只留最近 7 天**。教室电脑长期没人管，不清理的话磁盘会被慢慢吃掉；
///   而排障要看的几乎都是这几天内的事。
/// · **失败就闭嘴**。写不进去（目录只读、磁盘满）不影响应用继续干活 ——
///   日志是辅助，不该成为新的故障点。
/// </summary>
public static partial class AppLog
{
    /// <summary>保留多少天的日志。</summary>
    public const int RetainDays = 7;

    /// <summary>日志子目录名。</summary>
    public const string FolderName = "logs";

    /// <summary>指定日志目录的环境变量（启动脚本会设它，把日志放到软件目录下）。</summary>
    public const string DirectoryVariable = "CLASSSHOUT_LOG_DIR";

    private static readonly Lock Gate = new();
    private static bool _pruned;
    private static string? _resolved;

    /// <summary>
    /// 日志目录（不存在会自动创建）。
    ///
    /// 优先放在**软件自己的目录**下（<c>&lt;程序目录&gt;\logs</c>），这样"日志在哪"
    /// 与"软件在哪"是一件事 —— 找日志的人多半正站在那台机器前面。
    /// 但程序可能被装在 <c>Program Files</c> 这类只读位置，所以逐级退：
    ///
    ///   1. <c>CLASSSHOUT_LOG_DIR</c>（启动脚本设的，指向软件目录下的 logs）；
    ///   2. 程序目录下的 <c>logs</c>；
    ///   3. 用户数据目录下的 <c>logs</c>（只读安装时的落点）。
    ///
    /// 退到哪一级会显示在设置页的「关于」里 —— 否则"日志到底写哪去了"又是一场猜谜。
    /// </summary>
    public static string Directory
    {
        get
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            lock (Gate)
            {
                _resolved ??= ResolveDirectory();
                return _resolved;
            }
        }
    }

    private static string ResolveDirectory()
    {
        foreach (var candidate in Candidates())
        {
            if (candidate is null)
            {
                continue;
            }

            try
            {
                System.IO.Directory.CreateDirectory(candidate);

                // 建得出来不代表写得进去（只读介质、权限收紧），实际探一次
                var probe = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);

                // 日志里有排障信息（服务器地址、教室名、账号名），同机其他账号不该随便看
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(
                            candidate,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        // 设不了不致命
                    }
                }

                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 试下一个
            }
        }

        // 三个都写不进去：仍然返回用户数据目录那份，让写入静默失败而不是抛异常
        return Path.Combine(LocalSettings.Directory, FolderName);
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable(DirectoryVariable);

        // 程序目录：AppContext.BaseDirectory 末尾带分隔符，Path.Combine 会处理
        yield return SafeCombine(AppContext.BaseDirectory, FolderName);

        yield return SafeCombine(LocalSettings.Directory, FolderName);
    }

    private static string? SafeCombine(string? root, string child)
    {
        try
        {
            return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, child);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>日志实际写在哪（界面上显示给排障的人看）。</summary>
    public static string LocationHint
    {
        get
        {
            var directory = Directory;
            var dataDirectory = Path.Combine(LocalSettings.Directory, FolderName);

            return string.Equals(directory, dataDirectory, StringComparison.OrdinalIgnoreCase)
                ? $"{directory}（程序目录只读，退到用户数据目录）"
                : directory;
        }
    }

    /// <summary>某一天的日志文件名。</summary>
    public static string FileNameFor(DateTimeOffset day) => $"classshout-{day:yyyy-MM-dd}.log";

    /// <summary>今天的日志文件全路径。</summary>
    public static string CurrentFilePath => Path.Combine(Directory, FileNameFor(DateTimeOffset.Now));

    private static AppLogLevel _minimum = AppLogLevel.Info;

    /// <summary>
    /// 低于这一档的日志既不显示、也不落盘。
    ///
    /// 为什么要能调：默认的 Info 记的是"正常操作与结果"，而"自动发现扫不到教室"
    /// 这类问题要看的是**每一次广播发到哪、谁回了什么** —— 那些属于 Debug / Trace。
    /// 平时把它们全记下来会把日志刷成流水账，所以由使用者按需打开（见设置页）。
    /// </summary>
    public static AppLogLevel Minimum
    {
        get => _minimum;
        set => _minimum = value;
    }

    /// <summary>这一档现在要不要记。</summary>
    public static bool IsEnabled(AppLogLevel level) => level >= _minimum;

    /// <summary>
    /// 写一条日志。
    /// </summary>
    /// <param name="kind">来源，例如「喊话」「朗读」「网络」。</param>
    /// <param name="message">内容。</param>
    public static void Write(string kind, string message) => Write(AppLogLevel.Info, kind, message);

    /// <summary>按档位写一条日志；低于当前档位的直接丢掉。</summary>
    /// <param name="level">详细程度。</param>
    /// <param name="kind">来源，例如「喊话」「朗读」「网络」。</param>
    /// <param name="message">内容。</param>
    public static void Write(AppLogLevel level, string kind, string message)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                PruneLocked(DateTimeOffset.Now);

                // 凭据一律不进文件（见 Redact）：界面上显示口令是设计如此，
                // 但写进磁盘就是另一回事 —— 日志会被打包发给别人看、会被备份拷走。
                // 级别写在前面，和服务器那份日志（[信息 Relay]）保持同一种读法。
                var safeText = Redact(message);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{AppLogLevels.Label(level)} {kind}] {safeText}{Environment.NewLine}";
                var path = Path.Combine(Directory, FileNameFor(DateTimeOffset.Now));

                File.AppendAllText(path, line, Encoding.UTF8);
                RestrictPermissions(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 日志写不进去不该影响应用本身
        }
    }

    /// <summary>
    /// 把日志里的凭据换成「（已隐去）」。
    ///
    /// 两处真实存在的情况：
    ///   · 服务器首次启动的横幅里有「口令：xxxxxx」（管理员口令）；
    ///   · 教室端注册成功后界面上显示「口令：xxxxxx —— 请抄给老师」，
    ///     而这条也会被记进日志。
    /// 界面显示口令是设计如此，但**落到磁盘**就多了一份暴露面：
    /// 日志会被打包发给别人看排障、会被备份拷走、会随压缩包一起传播。
    ///
    /// 只抹掉"标签 + 冒号 + 值"这种形状，像「管理员口令已更新。」这种不带值的照旧留下 ——
    /// 否则日志会变得没法读。
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return CredentialPattern().Replace(message, "$1：（已隐去）");
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(口令|密钥|Secret|secret|Token|token|API[ _-]?[Kk]ey)\s*[：:]\s*[^\s，。；、]+",
        System.Text.RegularExpressions.RegexOptions.None,
        matchTimeoutMilliseconds: 200)]
    private static partial System.Text.RegularExpressions.Regex CredentialPattern();

    /// <summary>
    /// 收紧日志文件权限（只给宿主自己读写）。
    ///
    /// Windows 上用户目录本来就只有本人可读；Linux 上要显式设 ——
    /// 不然一份带排查信息的日志会是 0644，同机其他账号都能看。
    /// </summary>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 设不了就算了：内容里的凭据已经抹过一道
        }
    }

    /// <summary>
    /// 删掉超过保留期的日志文件，返回删掉了几个。
    /// </summary>
    public static int Prune(DateTimeOffset now)
    {
        lock (Gate)
        {
            return PruneLocked(now);
        }
    }

    private static int PruneLocked(DateTimeOffset now)
    {
        // 一个进程里只清理一次：每次写日志都扫目录太浪费
        if (_pruned)
        {
            return 0;
        }

        _pruned = true;
        return PruneFiles(Directory, now, RetainDays);
    }

    /// <summary>
    /// 按保留期清理某个目录下的日志文件。
    ///
    /// 单独抽出来是为了能直接断言：文件名里带着日期，所以判断依据是**文件名**
    /// 而不是文件时间戳 —— 后者会被复制、备份、解压等操作改掉，
    /// 而"这个文件是哪一天的"是它名字里写着的。
    /// </summary>
    /// <param name="directory">日志目录。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="retainDays">保留多少天（含今天）。</param>
    public static int PruneFiles(string directory, DateTimeOffset now, int retainDays = RetainDays)
    {
        var removed = 0;

        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return 0;
            }

            var cutoff = now.Date.AddDays(-(retainDays - 1));

            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "classshout-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(path);

                if (!name.StartsWith("classshout-", StringComparison.OrdinalIgnoreCase)
                    || !DateTime.TryParse(name["classshout-".Length..], out var day))
                {
                    // 不是我们按这个规则写的文件（手工放的、或名字被改过）：不动它
                    continue;
                }

                if (day.Date < cutoff)
                {
                    File.Delete(path);
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败无所谓：下次还会再试
        }

        return removed;
    }
}
