using System.Diagnostics;
using System.Runtime.InteropServices;
using ClassShout.Core.Audio;

namespace ClassShout.Teacher.Desktop.Services;

/// <summary>
/// 桌面端的两件事：弹一条系统通知、播一段 WAV（语音回复）。
///
/// Windows 与 Linux 各一套：
///   · Windows —— 托盘气泡要 P/Invoke Shell_NotifyIcon（Avalonia 的 TrayIcon 没有气泡 API），
///     播放用 NAudio；
///   · Linux —— 通知走 notify-send，播放走 aplay/paplay，都是命令行工具，
///     而且要能被"没装"这件事优雅地挡住（Linux 上缺一个包是常态）。
/// </summary>
public static class DesktopNotifier
{
    /// <summary>把通知与播放注册到平台层。任一能力缺失只是少一个功能，不影响启动。</summary>
    public static void Register()
    {
        ClassShout.Teacher.Services.TeacherPlatform.RegisterNotifier(Notify);
        ClassShout.Teacher.Services.TeacherPlatform.RegisterWavPlayer(PlayWav);
    }

    private static void Notify(string title, string body)
    {
#if WINDOWS
        TrayBalloon.Show(title, body);
#else
        Run("notify-send", ["-a", "ClassShout", "-t", "6000", title, body]);
#endif
    }

    private static void PlayWav(byte[] wav)
    {
#if WINDOWS
        // NAudio 播放：拿到的是完整 WAV，直接当流放。播放器不释放 ——
        // 释放的时机很难挑（音频还在响），而一条回复只有几秒。
        var stream = new MemoryStream(wav, writable: false);
        var reader = new NAudio.Wave.WaveFileReader(stream);
        var output = new NAudio.Wave.WaveOutEvent();
        output.Init(reader);
        output.PlaybackStopped += (_, _) =>
        {
            try
            {
                output.Dispose();
                reader.Dispose();
                stream.Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // 已经关掉了
            }
        };
        output.Play();
#else
        // Linux：把 WAV 落到临时文件再交给播放器。
        // 用管道（aplay 从 stdin 读）也可以，但有些版本对 stdin 的 WAV 头处理不一致，
        // 而临时文件最稳 —— 一条回复几百 KB，用完即删。
        var path = Path.Combine(Path.GetTempPath(), $"classshout-reply-{Guid.NewGuid():N}.wav");

        try
        {
            File.WriteAllBytes(path, wav);

            if (!Run("aplay", ["-q", path]))
            {
                Run("paplay", [path]);
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 播放器可能还占着它，下次系统清理时会带走
            }
        }
#endif
    }

#if !WINDOWS
    /// <summary>跑一个命令行工具。找不到它时返回 false（不是异常）。</summary>
    private static bool Run(string command, IReadOnlyList<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
#endif
}

#if WINDOWS
/// <summary>
/// 托盘气泡。
///
/// 为什么不用 Avalonia 的 TrayIcon：它只有图标与菜单，没有气泡/通知那一路 ——
/// 而"教室里回了一句"这件事需要一个能被看见的提示。Shell_NotifyIcon 是这件事
/// 在 Windows 上唯一的标准做法（Win10 之后它会变成右下角的通知中心条目）。
/// </summary>
internal static class TrayBalloon
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    private static bool _added;
    private static IntPtr _owner;

    /// <summary>弹一条气泡。失败就静默返回 —— 一次通知弹出失败不该影响收到回复本身。</summary>
    public static void Show(string title, string body)
    {
        try
        {
            if (!_added)
            {
                // 用一个隐藏的 message-only 窗口当图标宿主
                _owner = CreateWindowEx(0, "STATIC", "ClassShout", 0, 0, 0, 0, 0,
                    new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_owner == IntPtr.Zero)
                {
                    return;
                }

                var data = Data(title, body, NIF_INFO);
                _added = Shell_NotifyIcon(NIM_ADD, ref data);
            }

            var modify = Data(title, body, NIF_INFO);
            Shell_NotifyIcon(NIM_MODIFY, ref modify);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // 老系统上缺 API：退化成不通知
        }
    }

    private static NotifyIconData Data(string title, string body, int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _owner,
        uID = 1,
        uFlags = flags | 0x00000004 /* NIF_TIP */,
        szTip = "ClassShout",
        szInfo = Truncate(body, 250),
        szInfoTitle = Truncate(title, 60),
        dwInfoFlags = NIIF_INFO,
    };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
}
#endif
