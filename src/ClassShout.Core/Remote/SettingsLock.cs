using System.Security.Cryptography;
using System.Text;

namespace ClassShout.Core.Remote;

/// <summary>设置锁的本地配置。</summary>
public sealed class SettingsLockSettings
{
    /// <summary>是否启用。关闭时下面这些字段即使有值也一律忽略。</summary>
    public bool Enabled { get; set; }

    /// <summary>PIN 的 PBKDF2 派生值（Base64）。服务器与本机都不保存明文。</summary>
    public string? PinHash { get; set; }

    /// <summary>PIN 的随机盐（Base64）。</summary>
    public string? PinSalt { get; set; }

    /// <summary>
    /// 进入设置界面时先验一次 PIN（整体锁）。
    ///
    /// 和 <see cref="ProtectedAreas"/> 是两种用法，可以同时开：
    /// 整体锁管"别让人翻设置"，分项锁管"别的都能改，但密钥和退出不许动"。
    /// </summary>
    public bool LockSettingsEntry { get; set; } = true;

    /// <summary>
    /// 需要单独解锁才能改动的那几项，取值见 <see cref="ProtectedAreas"/>。
    ///
    /// 用字符串而不是位标志：这份文件是给人看、也可能被手改的，
    /// 存 "stt" 比存 4 一眼就能看懂，将来加项也不会让旧文件里的数字改变含义。
    /// </summary>
    public List<string> ProtectedAreas { get; set; } = [];

    /// <summary>退出程序时需要 PIN。</summary>
    public bool ProtectExit { get; set; }
}

/// <summary>可以被 PIN 单独保护的设置项。</summary>
public static class ProtectedAreas
{
    /// <summary>朗读设置（音量、语速、引擎、音色）。</summary>
    public const string Speech = "speech";

    /// <summary>语音转文字（含接口地址与密钥）。</summary>
    public const string Stt = "stt";

    /// <summary>跨局域网喊话与教室身份。</summary>
    public const string Relay = "relay";

    /// <summary>喊话弹窗与默认展示参数。</summary>
    public const string Display = "display";

    /// <summary>教室信息（教室名）。</summary>
    public const string Classroom = "classroom";

    /// <summary>后台运行与开机自启。</summary>
    public const string Background = "background";

    public static readonly string[] All =
        [Speech, Stt, Relay, Display, Classroom, Background];

    /// <summary>界面上显示的名字。</summary>
    public static string Label(string area) => area switch
    {
        Speech => "朗读设置",
        Stt => "语音转文字与密钥",
        Relay => "跨局域网与教室身份",
        Display => "喊话展示与弹窗",
        Classroom => "教室信息",
        Background => "后台运行与开机自启",
        _ => area,
    };
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

        // 在**已有配置**上改，而不是新建一份：改个 PIN 不该顺手把
        // "哪些项目受保护""退出要不要 PIN"这些选择清空 —— 那样用户会以为
        // 自己只是换了个口令，实际保护范围被悄悄重置了。
        var settings = Load();
        settings.Enabled = true;
        settings.PinHash = Convert.ToBase64String(hash);
        settings.PinSalt = Convert.ToBase64String(salt);

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

    /// <summary>
    /// 某一项是不是受保护的。
    ///
    /// 没启用锁时一律返回 false：这是可选功能，关着的时候不该拦人 ——
    /// 哪怕配置文件里还留着上次勾的那几项。
    /// </summary>
    public static bool IsAreaProtected(string area)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var settings = Load();

        return settings.ProtectedAreas.Any(
            item => string.Equals(item, area, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>退出程序需不需要 PIN。</summary>
    public static bool IsExitProtected => IsEnabled && Load().ProtectExit;

    /// <summary>进入设置界面需不需要先验 PIN。</summary>
    public static bool IsEntryLocked => IsEnabled && Load().LockSettingsEntry;

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