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
public static class AppLog
{
    /// <summary>保留多少天的日志。</summary>
    public const int RetainDays = 7;

    /// <summary>日志子目录名。</summary>
    public const string FolderName = "logs";

    private static readonly Lock Gate = new();
    private static bool _pruned;

    /// <summary>日志目录（不存在会自动创建）。</summary>
    public static string Directory
    {
        get
        {
            var path = Path.Combine(LocalSettings.Directory, FolderName);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>某一天的日志文件名。</summary>
    public static string FileNameFor(DateTimeOffset day) => $"classshout-{day:yyyy-MM-dd}.log";

    /// <summary>今天的日志文件全路径。</summary>
    public static string CurrentFilePath => Path.Combine(Directory, FileNameFor(DateTimeOffset.Now));

    /// <summary>
    /// 写一条日志。
    /// </summary>
    /// <param name="kind">来源，例如「喊话」「朗读」「网络」。</param>
    /// <param name="message">内容。</param>
    public static void Write(string kind, string message)
    {
        try
        {
            lock (Gate)
            {
                PruneLocked(DateTimeOffset.Now);

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {message}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(Directory, FileNameFor(DateTimeOffset.Now)), line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 日志写不进去不该影响应用本身
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
