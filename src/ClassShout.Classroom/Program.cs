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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
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