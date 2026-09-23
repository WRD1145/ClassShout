using System.Security.Cryptography;
using System.Text;

namespace ClassShout.Core.Remote;

/// <summary>设置锁的本地配置。</summary>
public sealed class SettingsLockSettings
{
    /// <summary>是否启用。关闭时下面两个字段即使有值也一律忽略。</summary>
    public bool Enabled { get; set; }

    /// <summary>PIN 的 PBKDF2 派生值（Base64）。服务器与本机都不保存明文。</summary>
    public string? PinHash { get; set; }

    /// <summary>PIN 的随机盐（Base64）。</summary>
    public string? PinSalt { get; set; }
}

/// <summary>
/// 设置锁：进入设置界面之前先验一个本机 PIN。
///
/// 为什么是"本机 PIN"而不是服务器的管理员口令：
/// 教室端与教师端都可能在没有网络的教室里用，拿服务器口令会把"看设置"这件事
/// 变成"先联网"；而且服务器口令属于学校运维，不该被每位老师拿在手上。
/// 这个 PIN 由本机的人自己设，只管"别让路过的人乱改配置"这一件事。
///
/// 它是**可选的**：默认关闭。开着的时候也只在"进入设置"那一刻验一次 ——
/// 进去之后每一项都再问一遍，只会让人把 PIN 写在便签上贴在电脑边。
/// 所以"已解锁"这个状态记在内存里，由界面持有，绝不落盘：
/// 落盘就等于永久解锁，那这道锁也就没意义了。
///
/// 注意它拦的是"随手改配置"，不是"防得住拿到这台机器的人" ——
/// 文件就在本机，能读到文件的人可以删掉它直接绕过。这一点在界面上要说明白，
/// 免得学校以为它等同于账号口令。
/// </summary>
public static class SettingsLock
{
    /// <summary>PIN 最短长度。四位足够挡住"路过顺手点两下"，再长没人愿意每次输。</summary>
    public const int MinPinLength = 4;

    private const int MaxPinLength = 32;
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string FileName = "settings-lock.json";

    public static SettingsLockSettings Load() => LocalSettings.Load(FileName, static () => new SettingsLockSettings());

    /// <summary>当前是否启用了设置锁。</summary>
    public static bool IsEnabled
    {
        get
        {
            var settings = Load();
            return settings.Enabled
                   && !string.IsNullOrWhiteSpace(settings.PinHash)
                   && !string.IsNullOrWhiteSpace(settings.PinSalt);
        }
    }

    /// <summary>
    /// 设置或更换 PIN，并启用设置锁。
    /// 返回错误文案而不是简单的 bool：长度不合规和"写盘失败"是两回事，
    /// 都回一句"设置失败"会让人不知道下一步该做什么。
    /// </summary>
    public static (bool Ok, string? Error) SetPin(string pin)
    {
        if (!IsWellFormed(pin, out var error))
        {
            return (false, error);
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            pin, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);

        var settings = new SettingsLockSettings
        {
            Enabled = true,
            PinHash = Convert.ToBase64String(hash),
            PinSalt = Convert.ToBase64String(salt),
        };

        if (!LocalSettings.Save(FileName, settings))
        {
            // 写不进去就必须报失败。否则界面显示"已开启"，重启后又变成没开 ——
            // 用户以为配置被保护着，实际完全没有。
            return (false, "无法写入本机配置，PIN 未生效。请检查磁盘空间与文件权限。");
        }

        return (true, null);
    }

    /// <summary>
    /// 校验 PIN。
    ///
    /// 用固定时间比较，且先把两边各自哈希再比 —— 直接对原始字节调用
    /// CryptographicOperations.FixedTimeEquals 会在长度不同时立刻返回，
    /// 把"PIN 有几位"从响应耗时里泄漏出去。
    /// </summary>
    public static bool Verify(string pin)
    {
        var settings = Load();

        if (!settings.Enabled
            || string.IsNullOrWhiteSpace(settings.PinHash)
            || string.IsNullOrWhiteSpace(settings.PinSalt))
        {
            // 没启用就一律放行：这是可选功能，关着的时候不该拦人
            return true;
        }

        try
        {
            var salt = Convert.FromBase64String(settings.PinSalt);
            var expected = Convert.FromBase64String(settings.PinHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                pin ?? string.Empty, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            // 文件被改坏：当作验不过，而不是放行
            return false;
        }
    }

    /// <summary>关闭设置锁，并清掉已保存的 PIN。</summary>
    public static bool Disable()
        => LocalSettings.Save(FileName, new SettingsLockSettings());

    /// <summary>PIN 是否合规。</summary>
    public static bool IsWellFormed(string? pin, out string? error)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            error = "请输入 PIN。";
            return false;
        }

        if (pin.Length < MinPinLength)
        {
            error = $"PIN 至少 {MinPinLength} 位。";
            return false;
        }

        if (pin.Length > MaxPinLength)
        {
            error = $"PIN 最多 {MaxPinLength} 位。";
            return false;
        }

        // 只允许数字：教室电脑上多半是触屏或数字键盘，让人切输入法打字母很别扭
        if (!pin.All(char.IsAsciiDigit))
        {
            error = "PIN 只能包含数字。";
            return false;
        }

        error = null;
        return true;
    }
}