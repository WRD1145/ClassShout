using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassShout.RelayServer;

/// <summary>
/// 服务器配置，落盘为 relay-config.json。
///
/// 关于管理员口令为什么是明文存这里：
///   这是自建服务器的运维账号，不是终端用户账号。它的口令必须能被运维查出来 ——
///   否则一旦没记下来，就只能删文件重启来重新生成。放在服务器本地的配置文件里，
///   靠文件权限保护，是这个规模下的合理取舍。
///   面向老师的普通账号则完全不同：那些口令只以 PBKDF2 派生值保存，服务器自己也读不出来。
///   详见 README 的安全说明。
/// </summary>
public sealed class ServerConfig
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // 口令字母表里有 & + < > 这类字符，默认编码器会把它们转义成 \u0026 这种形式。
        // 可这个文件的用途之一就是让运维直接打开把口令抄出来，
        // 一屏 \uXXXX 显然做不到这件事。这里不对 HTML 负责，只对人负责。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>内置管理员账号名。</summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>内置管理员口令（明文，见类注释）。</summary>
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>口令生成时间，便于判断是否还是初始口令。</summary>
    public DateTimeOffset AdminPasswordGeneratedAt { get; set; }

    /// <summary>是否还是首次自动生成的口令。界面上据此提醒尽快修改。</summary>
    public bool AdminPasswordIsInitial { get; set; } = true;

    [JsonIgnore]
    public string Path { get; private set; } = string.Empty;

    public static ServerConfig LoadOrCreate(string path, ILogger logger)
    {
        var config = new ServerConfig { Path = path };

        if (File.Exists(path))
        {
            string raw;

            try
            {
                raw = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 关键：读不出来的时候绝不能"顺手重新生成一份"。
                //
                // 这个文件里存着管理员明文口令。旧的实现把权限错误和内容损坏
                // 一起当成"读不了就重建"，结果是进程直接带着未捕获的异常崩掉；
                // 而如果当时只是把它 catch 住，那更糟 —— 口令会被静默换掉，
                // 运维手上那张抄着口令的纸当场作废，日志里还只有一行"将使用新生成的配置"。
                //
                // 权限问题没有任何"自动修复"的余地，只能报错让人来改。
                // 正常情况下启动前的体检已经拦住了这类问题，这里是最后一道保险。
                throw new IOException(
                    $"无法读取服务器配置 {path}：{ex.Message}。"
                    + "该文件与所在目录需要对运行本服务的账号可读写。",
                    ex);
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                // 空文件几乎总是上次写入中途中断留下的残骸。
                // 内容已经没了，重新生成是唯一出路，但必须让人知道口令变了。
                logger.LogWarning("服务器配置是空文件，将重新生成管理员口令：{Path}", path);
            }

            ServerConfig? loaded = null;

            try
            {
                loaded = JsonSerializer.Deserialize<ServerConfig>(raw, SerializerOptions);
                if (loaded is null && !string.IsNullOrWhiteSpace(raw))
                {
                    logger.LogWarning("服务器配置内容不是有效对象，将重新生成管理员口令：{Path}", path);
                }
            }
            catch (JsonException ex)
            {
                // 内容损坏和权限问题是两码事：文件已经不可能再用，
                // 重新生成是安全的。但先把原始字节留一份 ——
                // 抄过口令的运维还有机会从这里把旧口令捞回来。
                var backup = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                TryBackup(path, backup, logger);
                logger.LogError(ex, "服务器配置内容损坏，原文件已备份为 {Backup}，将重新生成管理员口令。", backup);
            }

            if (loaded is not null)
            {
                loaded.Path = path;
                config = loaded;

                if (!string.IsNullOrWhiteSpace(config.AdminPassword))
                {
                    logger.LogInformation("已载入服务器配置：{Path}（管理员账号 {Admin}）", path, config.AdminUsername);
                    return config;
                }

                logger.LogWarning("配置文件里没有管理员口令，将重新生成。");
            }
        }

        config.AdminUsername = string.IsNullOrWhiteSpace(config.AdminUsername) ? "admin" : config.AdminUsername;
        config.AdminPassword = GenerateStrongPassword();
        config.AdminPasswordGeneratedAt = DateTimeOffset.UtcNow;
        config.AdminPasswordIsInitial = true;

        if (!config.Save(logger))
        {
            // 写不进去还继续跑，等于给运维一个"记下来、重启后却登录不上"的口令。
            // 与其留下这种陷阱，不如直接拒绝启动。
            throw new IOException(
                $"无法把新生成的服务器配置写入 {path}。"
                + "请确认运行本服务的账号对该文件与所在目录有写权限。");
        }

        // 醒目地打一条：这是运维第一次拿到口令的唯一机会，
        // 混在其他日志里很容易被忽略掉。
        logger.LogWarning("──────────────────────────────────────────────────────────");
        logger.LogWarning("  已生成管理员账号，请立即记录并妥善保存：");
        logger.LogWarning("      账号：{Admin}", config.AdminUsername);
        logger.LogWarning("      口令：{Password}", config.AdminPassword);
        logger.LogWarning("  该口令同时保存在配置文件：{Path}", path);
        logger.LogWarning("  登录后可在此修改口令，修改后配置里的值会同步更新。");
        logger.LogWarning("──────────────────────────────────────────────────────────");

        return config;
    }

    /// <summary>修改管理员口令并落盘。返回 false 表示落盘失败，本次修改没有生效。</summary>
    public bool UpdateAdminPassword(string newPassword, ILogger logger)
    {
        var previousPassword = AdminPassword;
        var previousGeneratedAt = AdminPasswordGeneratedAt;
        var previousIsInitial = AdminPasswordIsInitial;

        AdminPassword = newPassword;
        AdminPasswordGeneratedAt = DateTimeOffset.UtcNow;
        AdminPasswordIsInitial = false;

        // 先落盘再报成功。否则会出现"界面说改好了、重启后又是旧口令"这种
        // 最难排查的情况 —— 运维多半会以为是自己记错了。
        if (!Save(logger))
        {
            // 回滚内存状态，让"内存里的口令"和"磁盘上的口令"始终一致：
            // 要么都是新的，要么都是旧的，不留一个只在当前进程有效的中间态。
            AdminPassword = previousPassword;
            AdminPasswordGeneratedAt = previousGeneratedAt;
            AdminPasswordIsInitial = previousIsInitial;

            logger.LogError("管理员口令修改未能写入磁盘，已放弃本次修改：{Path}", Path);
            return false;
        }

        logger.LogInformation("管理员口令已更新。");
        return true;
    }

    /// <summary>把配置写到磁盘。返回 false 表示没有写成功。</summary>
    public bool Save(ILogger logger)
    {
        try
        {
            AtomicStateFile.Write(Path, JsonSerializer.Serialize(this, SerializerOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "保存服务器配置失败：{Path}", Path);
            return false;
        }
    }

    /// <summary>把损坏的配置复制一份留档，失败也不影响主流程。</summary>
    private static void TryBackup(string path, string backup, ILogger logger)
    {
        try
        {
            File.Copy(path, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "备份损坏的配置文件失败：{Path}", path);
        }
    }

    /// <summary>
    /// 用固定时间比较校验管理员口令，避免通过响应耗时逐字节试探。
    /// </summary>
    public bool VerifyAdmin(string username, string password)
        => string.Equals(username, AdminUsername, StringComparison.OrdinalIgnoreCase)
           && FixedTimeEquals(AdminPassword, password);

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// 生成便于手抄的强口令：20 位，含大小写、数字与符号，
    /// 并剔除 0/O、1/l/I 这类容易看错的字符 —— 运维是照着日志手抄的。
    /// </summary>
    private static string GenerateStrongPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const string all = upper + lower + digits + symbols;

        var chars = new char[20];

        // 先各取一个，保证四类字符都出现，避免生成出"看起来够长实则很弱"的口令
        chars[0] = Pick(upper);
        chars[1] = Pick(lower);
        chars[2] = Pick(digits);
        chars[3] = Pick(symbols);

        for (var i = 4; i < chars.Length; i++)
        {
            chars[i] = Pick(all);
        }

        // 打乱固定位置，否则前四位永远是"大写 小写 数字 符号"这种可预测的排列
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
