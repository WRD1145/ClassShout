using Avalonia;
using ClassShout.Classroom.Services;
using ClassShout.Core.Audio;

namespace ClassShout.Classroom;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 运维诊断：Linux 教室端上最常见的问题是"没装 alsa-utils"或"本机没有语音合成"，
        // 而那时的表现只有一个——不发声，从界面上完全看不出原因。
        // 这两个开关是为了让运维能一条命令问清楚"这台机器到底能用什么"，
        // 而不必去翻日志、猜依赖。
        if (Array.Exists(args, a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var line in ClassroomPlatform.DescribeBackends())
            {
                Console.WriteLine(line);
            }

            return 0;
        }

        if (Array.Exists(args, a => a.Equals("--audio-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunAudioTest();
        }

        // 开机自启也可以从命令行开关，而不是只能点界面。
        //
        // 两个理由：教室里那台机器往往不方便坐下来点设置（可能还没配键盘鼠标）；
        // 而且这一步"到底有没有真的写进启动文件夹"必须能被验证 ——
        // 有一个能回读状态的开关，就不必靠"界面显示已开启"来相信它。
        if (Array.Exists(args, a => a.Equals("--autostart-status", StringComparison.OrdinalIgnoreCase)))
        {
            return ReportAutoStart();
        }

        if (Array.Exists(args, a => a.Equals("--autostart-on", StringComparison.OrdinalIgnoreCase)))
        {
            return SetAutoStart(enable: true);
        }

        if (Array.Exists(args, a => a.Equals("--autostart-off", StringComparison.OrdinalIgnoreCase)))
        {
            return SetAutoStart(enable: false);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>汇报开机自启的实际状态：文件在不在、指向哪里。</summary>
    private static int ReportAutoStart()
    {
        Console.WriteLine($"支持自启     ：{(StartupShortcut.IsSupported ? "是" : "否")}");
        Console.WriteLine($"当前状态     ：{(StartupShortcut.IsEnabled ? "已开启" : "未开启")}");
        Console.WriteLine($"启动文件夹   ：{StartupShortcut.StartupFolder}");
        Console.WriteLine($"快捷方式     ：{StartupShortcut.ShortcutPath}");

        var target = StartupShortcut.Describe();
        Console.WriteLine($"快捷方式指向 ：{target ?? "（读不到）"}");

        var self = Environment.ProcessPath;
        Console.WriteLine($"当前程序路径 ：{self}");

        if (target is not null && self is not null &&
            !string.Equals(target, self, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("注意         ：快捷方式指向的不是当前这份程序，下次启动本程序时会自动改写。");
        }

        return 0;
    }

    private static int SetAutoStart(bool enable)
    {
        var error = enable ? StartupShortcut.Enable() : StartupShortcut.Disable();

        if (error is not null)
        {
            Console.WriteLine($"{(enable ? "开启" : "关闭")}开机自启失败：{error}");
            return 1;
        }

        Console.WriteLine($"已{(enable ? "开启" : "关闭")}开机自启。");
        return ReportAutoStart();
    }

    /// <summary>
    /// 真的走一遍播放链路：建播放器、按默认格式送 200 毫秒静音、等它正常收尾。
    ///
    /// 这不是单元测试，而是给现场用的自检 —— "装是装上了，能不能出声"
    /// 在有声卡的机器上几秒钟就有答案，不用等老师上课时才发现。
    /// </summary>
    private static int RunAudioTest()
    {
        var format = AudioFormat.Default;
        var player = ClassroomPlatform.CreatePlayer();

        Console.WriteLine($"播放自检：{format}");

        if (!ClassroomPlatform.TryStartPlayer(player, format, out var error))
        {
            Console.WriteLine($"  启动失败：{error}");
            return 1;
        }

        try
        {
            // 200 毫秒静音：足以验证参数拼得对、管道通、进程能正常收尾，
            // 又不会在教室里真的发出声音。
            player.Write(new byte[format.BytesForDuration(200)]);
            player.CompleteAsync().GetAwaiter().GetResult();

            Console.WriteLine("  完成：播放链路可用。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            player.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}