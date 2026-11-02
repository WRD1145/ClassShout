using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassShout.Setup;

/// <summary>
/// 这个安装包里装的是哪个应用。
///
/// 由 <c>scripts\pack.ps1</c> 在构建时写成 payload-info.json 内嵌进来，
/// 而不是用 MSBuild 生成 C# 常量：那些值（应用名、exe 名、目录、版本）本来就来自
/// 打包脚本，让它们只有一个来源，比在 csproj 里转义一堆引号可靠得多。
/// </summary>
public sealed class PayloadInfo
{
    public string AppId { get; set; } = "classroom";

    public string Title { get; set; } = "ClassShout 教室端";

    public string Exe { get; set; } = "ClassShout.Classroom.exe";

    public string Folder { get; set; } = "Classroom";

    public string Version { get; set; } = "0.0.0";
}

/// <summary>
/// ClassShout 的安装 / 更新 / 卸载程序。
///
/// 一次编译出的是**一个** exe，装的是同一次发布里的那份程序（内嵌 payload.zip）。
///
/// 为什么安装版与便携版是两种目录布局：
///   · 便携版把运行时收进 `runtime\`，靠启动脚本设 `DOTNET_ROOT` —— 干净，便于随手解压；
///   · 安装版是**平铺**的（hostfxr / coreclr 就在 exe 旁边），双击快捷方式就能跑，
///     不需要任何环境变量、也不用有人记得"要先双击启动脚本"。
///
/// 装到 %LOCALAPPDATA% 下**不需要管理员**：教室里那台电脑常常不给管理员权限，
/// 而"装不上"是这类工具最容易被放弃的地方。卸载走「应用和功能」里那一栏。
/// </summary>
public static partial class Program
{
    private static string UninstallKeyRoot => TestRegistryRoot ?? @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static PayloadInfo Info = LoadInfo();
    private static bool SuppressLaunch;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向到不支持的句柄时，不值得因此退出
        }

        if (Has(args, "selftest")) return SetupSelfTest.Run();
        if (Has(args, "help", "h", "?"))
        {
            PrintHelp();
            return 0;
        }

        // 命令要在**所有**参数里找，而不是"不以 / 或 - 开头的第一个"：
        // `/update /silent` 两个都以 / 开头，于是被当成"没有命令"→ 走成安装；
        // 而安装不会先关掉正在运行的实例，覆盖文件时就撞在"文件被占用"上。
        var command = ResolveCommand(args);
        SuppressLaunch = Has(args, "norestart");
        if (!Has(args, "silent", "quiet")) return RunGui(command);

        try
        {
            return ExecuteAction(command, quiet: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Fail($"出错了：{ex.GetType().Name}：{ex.Message}");

            if (!Has(args, "silent", "quiet"))
            {
                Console.WriteLine("按任意键退出…");
                Console.ReadKey(intercept: true);
            }

            return 1;
        }
    }

    /// <summary>
    /// 从参数里挑出命令：跳过已知开关（/silent 等），第一个剩下的就是命令。
    /// 这样 `/update /silent` 与 `/silent /update` 两种写法都对 —— 用户不必记顺序。
    /// </summary>
    private static string ResolveCommand(string[] args)
    {
        string[] switches = ["silent", "quiet", "help", "h", "?", "norestart"];

        foreach (var arg in args)
        {
            if (!arg.StartsWith("/", StringComparison.Ordinal) && !arg.StartsWith("-", StringComparison.Ordinal))
            {
                return arg.ToLowerInvariant();
            }

            var name = arg.TrimStart('/', '-').ToLowerInvariant();

            if (!switches.Contains(name))
            {
                return name;
            }
        }

        return "install";
    }


    private static int Unsupported(string command)
    {
        Fail($"不认识的命令：{command}（可用：install / update / uninstall，加 /silent 可静默）");
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            {Info.Title} 安装 / 更新程序

              {Path.GetFileName(Environment.ProcessPath)}              安装（会问装到哪）
              {Path.GetFileName(Environment.ProcessPath)} /silent      静默安装，用默认目录
              {Path.GetFileName(Environment.ProcessPath)} /update      升级已装的版本（保留设置与日志）
              {Path.GetFileName(Environment.ProcessPath)} /uninstall   卸载

            默认安装目录：{DefaultInstallDir()}
            装在 %LOCALAPPDATA% 下，不需要管理员权限。
            """);
    }

    // ======================== 安装 ========================

    private static int Install(bool quiet)
    {
        var target = RequestedTarget ?? (quiet ? DefaultInstallDir() : AskInstallDir(DefaultInstallDir()));

        if (target is null)
        {
            Console.WriteLine("已取消，什么都没做。");
            return 0;
        }

        var existing = ReadInstalledVersion(target);
        RejectDowngrade(existing);

        if (existing is not null)
        {
            Console.WriteLine($"这个目录里已经装了 {Info.Title} {existing} —— 按覆盖安装处理。");

            // 覆盖前先关掉这个目录里的实例；否则第一个被占用的 dll 就会让安装中途停下，
            // 而那句"写不进 Avalonia.Base.dll"对用户没有任何可操作性。
            CloseRunningInstances(target);
        }

        return ExtractPayload(target, quiet) ? Finish(target, quiet, isUpdate: existing is not null) : 1;
    }

    // ======================== 升级 ========================

    /// <summary>
    /// 升级：先关掉正在跑的实例，再覆盖。
    ///
    /// 只覆盖程序文件，**不删目录**：logs\ 留着，用户放在那里的东西也留着。
    /// 旧版本多出来的 dll 会留在原地 —— 它们不会再被加载（deps.json 已经换新），
    /// 代价是几十 KB 磁盘；换来的是一条铁律：升级永远不会弄丢日志。
    /// </summary>
    private static int Update(bool quiet)
    {
        var target = RequestedTarget ?? ExistingInstallDir();

        if (target is null)
        {
            Console.WriteLine("没有找到已安装的版本，按全新安装处理。");
            return Install(quiet);
        }

        RejectDowngrade(ReadInstalledVersion(target));
        CloseRunningInstances(target);

        return ExtractPayload(target, quiet) ? Finish(target, quiet, isUpdate: true) : 1;
    }

    // ======================== 卸载 ========================

    private static int Uninstall(bool quiet)
    {
        var target = RequestedTarget ?? ExistingInstallDir() ?? DefaultInstallDir();
        return RemoveManagedInstallation(target);
    }

    // ======================== 解包 ========================

    private static bool ExtractPayload(string target, bool quiet) => ExtractManagedPayload(target);

    private static Stream? OpenResource(string name)
        => name == "payload.zip" && TestPayload is not null ? TestPayload() : Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    private static PayloadInfo LoadInfo()
    {
        using var stream = OpenResource("payload-info.json");

        if (stream is null)
        {
            return new PayloadInfo();
        }

        try
        {
            return JsonSerializer.Deserialize<PayloadInfo>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new PayloadInfo();
        }
        catch (JsonException)
        {
            return new PayloadInfo();
        }
    }

    // ======================== 收尾：快捷方式 + 卸载登记 ========================

    private static int Finish(string target, bool quiet, bool isUpdate)
    {
        var exePath = Path.Combine(target, Info.Exe);

        if (!File.Exists(exePath))
        {
            Fail($"装完了却找不到 {Info.Exe} —— 这个安装包和程序对不上。");
            return 1;
        }

        if (!SkipOsIntegration)
        {
            CacheMaintenanceProgram();
            CreateShortcuts(exePath, target);
            Register(target, exePath);
        }

        Console.WriteLine();
        Console.WriteLine(CurrentAction == "repair" ? $"{Info.Title} 已修复，版本仍为 {Info.Version}。" : isUpdate
            ? $"{Info.Title} 已更新到 {Info.Version}。"
            : $"{Info.Title} {Info.Version} 已装好。");
        Console.WriteLine($"  安装位置：{target}");
        Console.WriteLine(@"  开始菜单：ClassShout");
        Console.WriteLine(@"  卸载：设置 → 应用 → 已安装的应用 里找它");

        if (IntegrationFailed)
        {
            Console.WriteLine("程序文件已写入，但快捷方式或安装登记未完成，请再次运行修复。");
            return 1;
        }

        if (GuiMode)
        {
            if (LaunchRequested) TryStart(exePath, target);
        }
        else if (isUpdate && !SuppressLaunch)
        {
            // 点的是"更新"，更新完直接拉起新版本；不该再让用户点一次启动
            TryStart(exePath, target);
        }
        else if (!quiet)
        {
            Console.Write("现在就启动它？（Y/n） ");

            if (!string.Equals(Console.ReadLine()?.Trim(), "n", StringComparison.OrdinalIgnoreCase))
            {
                TryStart(exePath, target);
            }
        }

        WaitForExit(quiet);
        return IntegrationFailed ? 1 : 0;
    }

    private static void TryStart(string exePath, string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = target,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"没启动起来（{ex.Message}），你可以自己双击 {exePath}。");
        }
    }

    private static string StartMenuDir
        => TestIntegrationRoot is { } root ? Path.Combine(root, "menu") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ClassShout");

    private static string DesktopShortcut
        => Path.Combine(TestIntegrationRoot is { } root ? root : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Info.Title}.lnk");

    private static void CreateShortcuts(string exePath, string target)
    {
        try
        {
            Directory.CreateDirectory(StartMenuDir);

            CreateShortcut(Path.Combine(StartMenuDir, $"{Info.Title}.lnk"), exePath, target);
            CreateShortcut(
                Path.Combine(StartMenuDir, $"卸载 {Info.Title}.lnk"),
                MaintenanceProgram,
                target,
                arguments: "/uninstall");

            if (!File.Exists(DesktopShortcut))
            {
                CreateShortcut(DesktopShortcut, exePath, target);
            }

            Console.WriteLine("已创建开始菜单与桌面快捷方式。");
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            IntegrationFailed = true;
            Console.WriteLine($"快捷方式没建成功（{ex.Message}），可以直接运行 {exePath}。");
        }
    }

    /// <summary>
    /// 建一个 .lnk。
    ///
    /// 用 WScript.Shell 这个 COM 组件而不是 P/Invoke IShellLink：它从 Windows 2000 起就在，
    /// 而且是"创建快捷方式"这件事最不容易写错的一条路（IShellLink 还要配 IPersistFile，
    /// 一次写错的表现是"双击弹出找不到项目"）。
    /// </summary>
    private static void CreateShortcut(string linkPath, string target, string workingDirectory, string? arguments = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统里没有 WScript.Shell，建不了快捷方式");

        dynamic shell = Activator.CreateInstance(shellType)!;

        try
        {
            dynamic shortcut = shell.CreateShortcut(linkPath);

            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = target;
            shortcut.Description = Info.Title;
            shortcut.Arguments = arguments ?? string.Empty;
            shortcut.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private static void DeleteShortcuts()
    {
        // 只删除当前端的快捷方式，另一端的开始菜单项必须保留。
        foreach (var name in new[] { $"{Info.Title}.lnk", $"卸载 {Info.Title}.lnk" })
            if (File.Exists(Path.Combine(StartMenuDir, name))) File.Delete(Path.Combine(StartMenuDir, name));

        try
        {
            if (File.Exists(DesktopShortcut))
            {
                File.Delete(DesktopShortcut);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"桌面快捷方式没删掉（{ex.Message}）。");
        }
    }

    // ======================== 登记（"应用和功能"里那一栏） ========================

    private static void Register(string target, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{UninstallKeyRoot}\ClassShout-{Info.AppId}");

            key.SetValue("DisplayName", Info.Title);
            key.SetValue("DisplayVersion", Info.Version);
            key.SetValue("Publisher", "ClassShout");
            key.SetValue("InstallLocation", target);
            key.SetValue("DisplayIcon", exePath);
            key.SetValue("UninstallString", $"\"{MaintenanceProgram}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{MaintenanceProgram}\" /uninstall /silent");
            key.SetValue("ModifyPath", $"\"{MaintenanceProgram}\"");
            key.SetValue("NoModify", 0, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, DirectorySize(target) / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            IntegrationFailed = true;
            // 登记不上只影响"应用和功能"里那一栏，程序照样能跑
            Console.WriteLine($"没写进「应用和功能」（{ex.Message}）—— 卸载时请用开始菜单里的卸载项。");
        }
    }

    private static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"{UninstallKeyRoot}\ClassShout-{Info.AppId}", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            IntegrationFailed = true;
            Console.WriteLine($"卸载登记没删干净（{ex.Message}），「应用和功能」里可能还会看到它。");
        }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return ReadRecord(path, false)?.Files.Sum(relative =>
            {
                var file = new FileInfo(OwnedFile(path, relative));
                return file.Exists ? file.Length : 0;
            }) ?? 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ======================== 已装版本 ========================

    private static string? ReadInstalledVersion(string target) => ReadManagedVersion(target);

    /// <summary>
    /// 已安装的目录：先看登记，再退回默认目录（有人手动拷过去、或者登记被清掉了）。
    /// </summary>
    private static string? ExistingInstallDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallKeyRoot}\ClassShout-{Info.AppId}");

            if (key?.GetValue("InstallLocation") is string location
                && Directory.Exists(location) && (ReadManagedVersion(location) is not null || ReadRecord(location, false) is not null))
            {
                return location;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 下面还有默认目录这一条路
        }

        var fallback = DefaultInstallDir();

        return ReadManagedVersion(fallback) is not null ? fallback : null;
    }

    private static string DefaultInstallDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassShout",
            Info.Folder);

    private static string? AskInstallDir(string suggestion)
    {
        Console.WriteLine();
        Console.WriteLine($"  {Info.Title} {Info.Version}");
        Console.WriteLine();
        Console.Write($"装到哪个目录？（回车 = {suggestion}） ");
        var input = Console.ReadLine()?.Trim();

        return string.IsNullOrEmpty(input) ? suggestion : input.Trim('"');
    }

    // ======================== 小工具 ========================

    private static void CloseRunningInstances(string target)
    {
        var running = AppProcesses(target);

        if (running.Count == 0)
        {
            return;
        }

        Console.WriteLine($"正在关闭 {running.Count} 个正在运行的实例…");

        foreach (var process in running)
        {
            try
            {
                if (!process.CloseMainWindow() || !process.WaitForExit(4000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(4000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                Console.WriteLine($"  关不掉这个进程（{ex.Message}），继续尝试覆盖。");
            }
        }
    }

    /// <summary>只认安装目录里的实例：同一台机器上教师端、教室端可能同时在跑。</summary>
    private static List<Process> AppProcesses(string target)
    {
        var result = new List<Process>();

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Info.Exe)))
        {
            try
            {
                var path = process.MainModule?.FileName;

                if (path is not null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 拿不到路径（进程刚好退出，或权限不足）：当成不是我们的
            }
        }

        return result;
    }

    private static bool Has(string[] args, params string[] names)
        => args.Any(arg => names.Any(name =>
            arg.Equals($"/{name}", StringComparison.OrdinalIgnoreCase)
            || arg.Equals($"--{name}", StringComparison.OrdinalIgnoreCase)));

    private static void WaitForExit(bool quiet)
    {
        if (quiet)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("按任意键关闭这个窗口…");
        Console.ReadKey(intercept: true);
    }

    private static void Fail(string message) => Console.WriteLine(message);
}
