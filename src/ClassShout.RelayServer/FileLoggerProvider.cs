using ClassShout.Core.Remote;

namespace ClassShout.RelayServer;

/// <summary>
/// 把服务器日志同时写一份到磁盘（<c>logs/classshout-YYYY-MM-DD.log</c>）。
///
/// 服务器本来只往 stdout 写，而 systemd 会把 stdout 收进 journald ——
/// 那套东西对"翻昨天的日志"并不友好（要会 <c>journalctl --since</c>，
/// 而日志还会被 journald 自己轮转掉）。压缩包里本来就带着一个 logs\ 目录，
/// 让它真的有东西，运维就不必先学一遍 journald。
///
/// **刻意只收有用的那些**：ASP.NET 自己那套 Information 里包含每一条长轮询请求，
/// 一晚上能把磁盘写满，而它们对排障毫无价值。所以：
///   · 我们自己代码（ClassShout.*）的 Information 及以上照收；
///   · 别的类别（Microsoft.*）只收 Warning 及以上。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    public void Dispose()
    {
        // 没有需要释放的资源：写文件用的是 AppLog 里那个带锁的静态实现
    }

    private sealed class FileLogger(string category) : ILogger
    {
        /// <summary>是不是我们自己代码的类别（那种 Information 也要留）。</summary>
        private readonly bool _isOurs = category.StartsWith("ClassShout", StringComparison.OrdinalIgnoreCase);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= (_isOurs ? LogLevel.Information : LogLevel.Warning);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);

            if (exception is not null)
            {
                message = $"{message} —— {exception.GetType().Name}：{exception.Message}";
            }

            // 来源取类别最后一段，日志里看着短一点（ClassShout.RelayServer.MessageHub → MessageHub）
            var dot = category.LastIndexOf('.');
            var kind = dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;

            AppLog.Write($"{Level(logLevel)} {kind}", message);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => "调试",
            LogLevel.Information => "信息",
            LogLevel.Warning => "警告",
            LogLevel.Error => "错误",
            LogLevel.Critical => "严重",
            _ => "信息",
        };
    }
}
