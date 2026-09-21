namespace ClassShout.RelayServer;

/// <summary>一个需要持久化的状态文件，用于启动前体检。</summary>
public sealed record StateFileSpec(string Label, string Path);

/// <summary>体检发现的一处问题。</summary>
public sealed record StateProblem(string Title, string Detail);

/// <summary>
/// 启动前对全部状态文件做一次性体检。
///
/// 为什么要在监听端口之前做这件事：
///   这几个文件是"配一次用一学期"的东西。如果读不出来，最坏的结局不是启动失败，
///   而是某个 Store 安静地降级成空表，服务照常起来，然后运维在毫无察觉的情况下
///   把教室注册记录覆盖掉。所以宁可起不来，也不能带着空数据对外服务。
///
/// 为什么一次性检查全部而不是遇到第一个就退出：
///   部署期最常见的场面是"改一处、重启、发现还有一处、再改、再重启"。
///   运维手上是一台没有图形界面的服务器，每多一轮就多一次 sudo 和 journalctl。
///   一次把问题报全，能省掉大半的往返。
/// </summary>
public static class StatePreflight
{
    /// <summary>
    /// 配置类错误的退出码，取自 BSD sysexits.h 的 EX_CONFIG。
    /// systemd 可以据此区分"配置错了，重启也没用"和"进程意外挂了"，
    /// 从而不再无脑重启（见 README 的 unit 文件模板）。
    /// </summary>
    public const int ConfigErrorExitCode = 78;

    /// <summary>逐项检查，返回发现的所有问题。空列表表示健康。</summary>
    public static IReadOnlyList<StateProblem> Inspect(IReadOnlyList<StateFileSpec> specs)
    {
        var problems = new List<StateProblem>();
        var checkedDirectories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            var full = Path.GetFullPath(spec.Path);
            var directory = Path.GetDirectoryName(full);

            if (string.IsNullOrEmpty(directory))
            {
                problems.Add(new StateProblem($"{spec.Label}：路径无效", spec.Path));
                continue;
            }

            if (checkedDirectories.Add(directory))
            {
                var directoryProblem = CheckDirectory(directory);
                if (directoryProblem is not null)
                {
                    problems.Add(directoryProblem);
                }
            }

            if (!File.Exists(full))
            {
                continue;
            }

            // 空文件几乎总是上一次写入中途断电留下的残骸。
            // 直接当"没有"来处理，就等于把已有数据静默丢掉。
            if (new FileInfo(full).Length == 0)
            {
                problems.Add(new StateProblem($"{spec.Label}：文件是空的", full));
                continue;
            }

            var readError = TryRead(full);
            if (readError is not null)
            {
                problems.Add(new StateProblem($"{spec.Label}：文件存在但读不出来", $"{full}（{readError}）"));
            }
        }

        return problems;
    }

    /// <summary>
    /// 打印体检结果。返回 true 表示可以继续启动。
    /// </summary>
    public static bool Report(IReadOnlyList<StateFileSpec> specs, ILogger logger)
    {
        IReadOnlyList<StateProblem> problems;
        try
        {
            problems = Inspect(specs);
        }
        catch (Exception ex)
        {
            // 体检本身不该成为新的崩溃点：真出了意料之外的情况，记一笔然后放行，
            // 让各个 Store 按各自的老逻辑去处理。
            logger.LogWarning(ex, "状态文件体检未能完成，将继续启动。");
            return true;
        }

        if (problems.Count == 0)
        {
            return true;
        }

        var user = Environment.UserName;

        logger.LogError("──────────────────────────────────────────────────────────");
        logger.LogError("  启动中止：状态文件无法使用（共 {Count} 处问题）", problems.Count);
        logger.LogError(string.Empty);

        foreach (var problem in problems)
        {
            logger.LogError("  · {Title}", problem.Title);
            logger.LogError("      {Detail}", problem.Detail);
        }

        logger.LogError(string.Empty);
        logger.LogError("  当前进程以账号「{User}」运行。常见原因是：", user);
        logger.LogError("    · 曾用 sudo 或 root 手工创建过这些文件，使宿主变成了 root；");
        logger.LogError("    · 按安全建议收紧过权限，但只改了 chmod，没有一起改 chown；");
        logger.LogError("    · 或者配置文件里填的路径写错了。");

        if (!OperatingSystem.IsWindows())
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(specs[0].Path)) ?? "/opt/classshout";
            logger.LogError(string.Empty);
            logger.LogError("  Linux 上修正（把 {User} 换成服务实际的运行账号）：", user);
            logger.LogError("      sudo chown -R {User}:{User} {Dir}", user, user, directory);
            logger.LogError("      sudo chmod 700 {Dir}", directory);
            logger.LogError("      sudo chmod 600 {Dir}/*.json", directory);
            logger.LogError("  改完执行：sudo systemctl restart classshout");
        }
        else
        {
            logger.LogError(string.Empty);
            logger.LogError("  Windows 上修正：确认目录 ACL 允许当前账号读写，");
            logger.LogError("  或用 CLASSSHOUT_* 环境变量把状态文件指到可写目录。");
        }

        logger.LogError("──────────────────────────────────────────────────────────");

        return false;
    }

    /// <summary>
    /// 检查目录本身能不能用。
    ///
    /// 注意检查的是"能不能在目录里新建文件"，而不是"目录里的文件能不能写"。
    /// 所有保存都是先写 .tmp 再原子替换，这个动作在 Linux 上要求的是目录的写权限，
    /// 目标文件自身的模式位反而是次要的。只查文件权限会漏掉真正的故障。
    /// </summary>
    private static StateProblem? CheckDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new StateProblem("状态目录不存在", directory);
        }

        var probe = Path.Combine(directory, ".classshout-probe-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StateProblem("状态目录不可写", $"{directory}（{ex.GetType().Name}）");
        }

        return null;
    }

    /// <summary>试读一次，返回 null 表示可读。</summary>
    private static string? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
