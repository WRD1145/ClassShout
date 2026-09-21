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
            try
            {
                var loaded = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), SerializerOptions);
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
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogError(ex, "读取服务器配置失败，将使用新生成的配置：{Path}", path);
            }
        }

        config.AdminUsername = string.IsNullOrWhiteSpace(config.AdminUsername) ? "admin" : config.AdminUsername;
        config.AdminPassword = GenerateStrongPassword();
        config.AdminPasswordGeneratedAt = DateTimeOffset.UtcNow;
        config.AdminPasswordIsInitial = true;
        config.Save(logger);

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

    /// <summary>修改管理员口令并落盘。</summary>
    public void UpdateAdminPassword(string newPassword, ILogger logger)
    {
        AdminPassword = newPassword;
        AdminPasswordGeneratedAt = DateTimeOffset.UtcNow;
        AdminPasswordIsInitial = false;
        Save(logger);
        logger.LogInformation("管理员口令已更新。");
    }

    public void Save(ILogger logger)
    {
        try
        {
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "保存服务器配置失败：{Path}", Path);
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
