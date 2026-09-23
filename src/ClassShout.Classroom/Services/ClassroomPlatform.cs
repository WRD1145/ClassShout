using ClassShout.Core.Audio;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 教室端的平台差异注入点。
///
/// 与教师端 <see cref="ClassShout.Teacher.Services.TeacherPlatform"/> 同样的思路：
/// 只有"播放"和"系统朗读"两处真正的平台差异，用编译期分支 + 一个工厂收住，
/// 上层（视图模型）只认 Core 里的 <see cref="IAudioPlayer"/> 与
/// <see cref="ISpeechSynthesizer"/>，不知道底下是 NAudio 还是 aplay。
///
/// 用编译期分支而不是运行期判断：Windows 那一支依赖 NAudio 与 System.Speech，
/// 它们在 net10.0（Linux）目标下根本没有被引用，运行期判断也编不过。
/// </summary>
public static class ClassroomPlatform
{
    /// <summary>当前目标平台的名字，用于诊断输出。</summary>
    public static string PlatformName =>
#if WINDOWS
        "Windows";
#else
        "Linux";
#endif

    /// <summary>创建一个音频播放器。</summary>
    public static IAudioPlayer CreatePlayer()
    {
#if WINDOWS
        return new NAudioLoopbackPlayer();
#else
        return new ProcessAudioPlayer();
#endif
    }

    /// <summary>本机有没有可用的系统朗读。Linux 上取决于 spd-say / espeak 在不在。</summary>
    public static bool HasSystemSpeech =>
#if WINDOWS
        true;
#else
        ProcessSpeechSynthesizer.IsBackendAvailable;
#endif

    /// <summary>
    /// 创建系统朗读引擎（保底用，实际朗读优先走 Edge 在线语音）。
    ///
    /// 本机没有时返回一个什么都不做的实现，而不是 null ——
    /// 上层就不必到处判空，只需要用 <see cref="HasSystemSpeech"/>
    /// 决定要不要在界面上提示"系统语音不可用"。
    /// </summary>
    public static ISpeechSynthesizer CreateSystemSpeech()
    {
#if WINDOWS
        // Windows 一定有 SAPI，不需要判断可用性
        return new WindowsSpeechSynthesizer();
#else
        return ProcessSpeechSynthesizer.IsBackendAvailable
            ? new ProcessSpeechSynthesizer()
            : new Core.Audio.SilentSpeechSynthesizer();
#endif
    }

    /// <summary>
    /// 启动播放，失败时把原因写进 <paramref name="error"/> 而不是抛出去。
    ///
    /// 声卡被占用、格式被驱动拒绝、Linux 上播放器命令不存在 ——
    /// 这些都是"这一路放不出来"，而不是"教室端该退出"。所以统一在这里
    /// 吃成返回值，让界面记一条日志就好。
    /// </summary>
    public static bool TryStartPlayer(IAudioPlayer player, AudioFormat format, out string? error)
    {
        try
        {
            player.Start(format);
            error = null;
            return true;
        }
        catch (NotSupportedException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
#if WINDOWS
        catch (NAudio.MmException ex)
        {
            error = $"声卡不可用：{ex.Message}";
            return false;
        }
#endif
    }

    /// <summary>
    /// 本机音频与朗读后端的可用情况，一行一条。
    ///
    /// 这不是给测试看的，是给运维看的：Linux 教室端上最常见的问题就是
    /// "没装 alsa-utils"或者"没有语音合成"，而那时的表现是"不发声"，
    /// 从界面上完全看不出原因。用 --diagnose 跑一次就能定位。
    /// </summary>
    public static IReadOnlyList<string> DescribeBackends()
    {
        var lines = new List<string> { $"平台：{PlatformName}" };

#if WINDOWS
        lines.Add("音频播放：NAudio（WaveOutEvent）");
        lines.Add("系统朗读：Windows SAPI（System.Speech）");
#else
        if (ProcessAudioPlayer.IsBackendAvailable)
        {
            var probe = new ProcessAudioPlayer();
            lines.Add("音频播放：可用（aplay 或 paplay）");
            _ = probe;
        }
        else
        {
            lines.Add("音频播放：不可用 —— 找不到 aplay 或 paplay。");
            lines.Add("           安装：apt install alsa-utils（或 pulseaudio-utils）");
        }

        lines.Add(ProcessSpeechSynthesizer.IsBackendAvailable
            ? "系统朗读：可用（spd-say / espeak-ng）"
            : "系统朗读：不可用 —— 找不到 spd-say、espeak-ng 或 espeak。");
#endif

        lines.Add("在线朗读：Edge 在线语音（需要能访问 speech.platform.bing.com）");
        lines.Add("托盘图标：由 Avalonia 提供；Linux 上需要桌面环境的通知区域支持。");

        return lines;
    }
}