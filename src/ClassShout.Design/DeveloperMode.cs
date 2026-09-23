using System.Reflection;
using System.Runtime.InteropServices;
using ClassShout.Core.Remote;

namespace ClassShout.Design;

/// <summary>
/// 客户端版本信息与开发者模式的开关。
///
/// 版本号取自入口程序集的 <see cref="AssemblyInformationalVersionAttribute"/> ——
/// 与 Directory.Build.props 里的 &lt;Version&gt; 同源。开启源码链接后它的形式是
/// "1.2.3+提交哈希"，所以这里顺手把提交哈希也解出来：报问题时"是哪个提交"
/// 往往比"哪个版本号"更有用。
///
/// 为什么不在各端各写一份：这个项目已经吃过一次亏 —— 服务端的版本号曾经是硬编码的
/// "1.0.0"，发了 1.1.0 之后控制台还在显示 1.0.0。版本信息只应有一个来源。
/// </summary>
public static class DeveloperMode
{
    private static bool _isEnabled;
    private static bool _loaded;

    /// <summary>是否已解锁开发者模式（持久化，重启后仍有效）。</summary>
    public static bool IsEnabled
    {
        get
        {
            if (!_loaded)
            {
                _isEnabled = LocalSettings.LoadDeveloper().Enabled;
                _loaded = true;
            }

            return _isEnabled;
        }
    }

    /// <summary>解锁并落盘。</summary>
    public static void Enable()
    {
        _isEnabled = true;
        _loaded = true;
        LocalSettings.SaveDeveloper(new DeveloperSettings { Enabled = true });
    }

    /// <summary>语义版本号，例如 1.2.3。</summary>
    public static string Version
    {
        get
        {
            var informational = InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational.Split('+')[0];
            }

            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "未知";
        }
    }

    /// <summary>提交哈希（短），取不到时为空串。</summary>
    public static string Commit
    {
        get
        {
            var informational = InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
            {
                return string.Empty;
            }

            var parts = informational.Split('+');
            return parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : string.Empty;
        }
    }

    /// <summary>
    /// 一行式的构建与环境描述。报问题时把这一行贴上，比来回问"你装的是哪版"快得多。
    /// </summary>
    public static string BuildDescription
    {
        get
        {
            var commit = Commit;
            var commitPart = commit.Length > 0 ? $" · 提交 {commit}" : string.Empty;
            return $"{Version}{commitPart} · {RuntimeInformation.FrameworkDescription} · "
                   + $"{RuntimeInformation.OSDescription.Trim()}";
        }
    }

    private static string? InformationalVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
