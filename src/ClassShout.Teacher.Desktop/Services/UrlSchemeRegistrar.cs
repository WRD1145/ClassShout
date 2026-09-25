using ClassShout.Core.Remote;
using Microsoft.Win32;

namespace ClassShout.Teacher.Desktop.Services;

/// <summary>
/// 把 <c>classshout://</c> 注册到 Windows，让网页上的"用教师端打开"按钮能真的叫起这个程序。
///
/// 为什么写 <c>HKCU\Software\Classes</c> 而不是 HKLM：写 HKCU 不需要管理员权限，
/// 而教师端是个便携程序 —— 为了注册一个协议去要提权，用户会直接放弃。
/// HKCU 的注册只对当前用户生效，这正好够用：协议要服务的就是这个用户点链接的那一下。
///
/// 注册是幂等的，每次启动都写一遍：程序可能被挪到别处（便携版很常见），
/// 那时旧的注册指向已经不存在的 exe，点链接只会得到一个"找不到程序"的系统提示。
/// </summary>
public static class UrlSchemeRegistrar
{
    private const string ProtocolKey = $@"Software\Classes\{ShareLink.Scheme}";

    /// <summary>
    /// 注册（或修正）协议处理程序。失败不抛异常，只返回 false ——
    /// 注册不上最多是"点链接不能自动打开软件"，而粘贴链接那条路仍然可用，
    /// 不该因为这个让应用起不来。
    /// </summary>
    public static bool TryRegister(out string? error)
    {
        error = null;

        // 显式判平台，而不是只靠项目的目标框架：注册表 API 在别的系统上不存在，
        // 而这个判断也顺便让"以后把桌面头也发到 Linux"时的行为是可预期的
        // （不注册、不抛异常，粘贴链接那条路照常可用）。
        if (!OperatingSystem.IsWindows())
        {
            error = "当前平台不支持注册自定义协议。";
            return false;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            error = "取不到当前程序路径。";
            return false;
        }

        try
        {
            using var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey);
            if (protocol is null)
            {
                error = "无法创建注册表项。";
                return false;
            }

            protocol.SetValue(string.Empty, "URL:ClassShout 班级分享");
            protocol.SetValue("URL Protocol", string.Empty);

            // 带 %1 的命令行：系统会把完整链接作为第一个参数交给我们
            using var command = protocol.CreateSubKey(@"shell\open\command");
            if (command is null)
            {
                error = "无法创建命令项。";
                return false;
            }

            command.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
