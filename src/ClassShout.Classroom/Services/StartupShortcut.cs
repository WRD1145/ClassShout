using System.Runtime.InteropServices;
using System.Text;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 开机自启：在「启动」文件夹里放一个指向本程序的快捷方式。
///
/// 为什么用启动文件夹而不是注册表 Run 键：
///   · 运维看得见、改得动 —— 直接打开 `shell:startup` 就是一个文件，删掉即关闭自启；
///     而 Run 键藏在注册表里，出了问题很难被发现。
///   · 老师换机器、重装系统时，把快捷方式拷过去就完事。
///
/// 创建 .lnk 没有托管 API，只能走 COM 的 IShellLink。这里手工声明接口，
/// 不引入任何第三方依赖 —— 需要的只是"建一个指向 exe 的快捷方式"这一件事。
///
/// 不做"记住一个开关状态、然后据此判断"这件事：状态**以文件是否存在为准**。
/// 老师手动把快捷方式删掉是很正常的操作，若程序还记着"已开启"，
/// 界面就会显示一个和实际不符的状态 —— 这种不一致比功能本身更容易误导人。
/// </summary>
public static class StartupShortcut
{
    /// <summary>快捷方式文件名。用中文名，因为它就摆在启动文件夹里给人看。</summary>
    private const string ShortcutName = "ClassShout 教室端.lnk";

    /// <summary>只有 Windows 走启动文件夹这条路。Linux 部署用 systemd，见 README。</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>启动文件夹路径（<c>shell:startup</c>）。</summary>
    public static string StartupFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    /// <summary>快捷方式的完整路径。</summary>
    public static string ShortcutPath => Path.Combine(StartupFolder, ShortcutName);

    /// <summary>当前是否已开启（以文件是否存在为准）。</summary>
    public static bool IsEnabled => IsSupported && File.Exists(ShortcutPath);

    /// <summary>
    /// 本程序可执行文件的路径。
    ///
    /// 单文件发布下 <c>Assembly.Location</c> 是空串，只有 <c>ProcessPath</c> 才是真的 exe 路径 ——
    /// 而快捷方式必须指向那个 exe，不是解包出来的临时目录。
    /// </summary>
    private static string ExecutablePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "ClassShout.Classroom.exe");

    /// <summary>
    /// 开启自启（已存在则覆盖）。返回 null 表示成功，否则返回给用户看的失败原因。
    ///
    /// 覆盖而不是"已存在就跳过"：程序升级或换目录之后，旧的快捷方式会指向一个不存在的路径，
    /// 开机时静默失败 —— 那种问题在教室里几个月都不会有人发现。
    /// </summary>
    public static string? Enable()
    {
        if (!IsSupported)
        {
            return "当前系统不支持通过启动文件夹设置自启。";
        }

        try
        {
            Directory.CreateDirectory(StartupFolder);

            var link = (IShellLinkW)new ShellLinkComObject();
            link.SetPath(ExecutablePath);
            link.SetWorkingDirectory(AppContext.BaseDirectory);
            link.SetDescription("ClassShout 教室端（开机自动启动）");

            // 图标取自身，省得依赖系统默认的 exe 图标
            link.SetIconLocation(ExecutablePath, 0);

            ((IPersistFile)link).Save(ShortcutPath, true);

            // 不调用 Marshal.FinalReleaseComObject：.NET Core 起它一律抛 PlatformNotSupportedException。
            // 这几个对象很小，交给 GC 即可。
            return null;
        }
        catch (Exception ex) when (ex is COMException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or InvalidCastException
                                       or PlatformNotSupportedException)
        {
            return $"{ex.GetType().Name}：{ex.Message}";
        }
    }

    /// <summary>关闭自启。返回 null 表示成功（含"本来就没开"），否则返回失败原因。</summary>
    public static string? Disable()
    {
        if (!IsSupported)
        {
            return null;
        }

        try
        {
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{ex.GetType().Name}：{ex.Message}";
        }
    }

    /// <summary>
    /// 开机自启实际指向的路径；读不到返回 null。仅用于自检与排错。
    /// </summary>
    public static string? Describe()
    {
        if (!IsEnabled)
        {
            return null;
        }

        try
        {
            var link = (IShellLinkW)new ShellLinkComObject();
            ((IPersistFile)link).Load(ShortcutPath, 0);

            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.ToString();
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidCastException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 已开启时把快捷方式重写一遍，修正程序被移动或升级后的路径。
    /// 启动时调用一次，代价可以忽略。
    /// </summary>
    public static string? RefreshIfEnabled() => IsEnabled ? Enable() : null;

    // ======================== COM 互操作 ========================
    //
    // 手工声明而不是引用 Interop.Shell32：那类包大多年久失修，
    // 而我们只需要下面这三个接口里的几个方法。
    // 顺序即 vtable 顺序，**不能**调整或省略中间的方法。

    // 不能声明为 sealed：C# 不允许把密封类转换成与它无关的接口，
    // 而这里正是靠"把 COM 对象当成接口用"来调用它的。
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkComObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);

        void GetIDList(out IntPtr itemIdList);

        void SetIDList(IntPtr itemIdList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        void IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
