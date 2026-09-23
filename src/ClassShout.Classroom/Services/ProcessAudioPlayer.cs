using System.Diagnostics;
using ClassShout.Core.Audio;

namespace ClassShout.Classroom.Services;

/// <summary>
/// Linux 上的音频播放：把 PCM 管道喂给系统自带的命令行播放器。
///
/// 为什么不上音频库：.NET 在 Linux 上没有官方音频 API，而引一个原生绑定
/// （SDL2 / libvlc / PortAudio）会给"教室端要跑在便宜小主机上"这件事
/// 带来一堆安装依赖。aplay（alsa-utils）和 paplay（pulseaudio-utils）
/// 几乎每台带声卡的发行版都有，用它俩最省事，也最容易在出问题时现场排查 ——
/// 运维可以直接手敲同一条命令试。
///
/// 代价是"写入失败"只能靠进程退出码发现，不像库调用那样有明确异常；
/// 所以这里对写入做了容错，并且把实际用的后端记下来供诊断用。
/// </summary>
public sealed class ProcessAudioPlayer : IAudioPlayer
{
    private Process? _process;
    private Stream? _stdin;
    private readonly Lock _lock = new();

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>实际选中的后端命令名。诊断用。</summary>
    public string Backend { get; private set; } = "未选择";

    /// <summary>本机有没有可用的播放后端。</summary>
    public static bool IsBackendAvailable => FindBackend(out _, out _);

    private static bool FindBackend(out string command, out string arguments)
    {
        // 优先 aplay：它直接对 ALSA 说话，不依赖桌面会话里的 PulseAudio，
        // 而教室机上往往是无桌面登录的常驻进程 —— 那种环境下 paplay 会连不上会话。
        if (Exists("aplay"))
        {
            command = "aplay";
            arguments = "-q -t raw -f {0} -r {1} -c {2} -";
            return true;
        }

        if (Exists("paplay"))
        {
            command = "paplay";
            arguments = "--raw --format={0} --rate={1} --channels={2}";
            return true;
        }

        command = string.Empty;
        arguments = string.Empty;
        return false;

        static bool Exists(string name)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, name)))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 某个 PATH 项不可读不影响继续找
                }
            }

            return false;
        }
    }

    public void Start(AudioFormat format)
    {
        Stop();

        if (!format.IsSupported)
        {
            throw new NotSupportedException($"不支持的音频格式：{format}。");
        }

        if (!FindBackend(out var command, out var template))
        {
            throw new NotSupportedException(
                "本机找不到 aplay 或 paplay。请安装 alsa-utils（Debian/Ubuntu：apt install alsa-utils）。");
        }

        // 16 bit 是两端统一的位深，这里只按采样率与声道数拼参数
        var sampleFormat = command == "aplay" ? "S16_LE" : "s16le";
        var arguments = string.Format(template, sampleFormat, format.SampleRate, format.Channels);

        var startInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {command}。");

        lock (_lock)
        {
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            Backend = $"{command} ({arguments})";
        }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        Stream? stdin;
        Process? process;

        lock (_lock)
        {
            stdin = _stdin;
            process = _process;
        }

        // 先抓进局部变量再判断：Stop() 会在另一条线程把字段置空（这一课在 NAudio 那版上踩过）
        if (stdin is null || process is null || process.HasExited || pcm.IsEmpty)
        {
            return;
        }

        try
        {
            stdin.Write(pcm);
            stdin.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 播放器被关掉或声卡被占用：丢掉这一片即可，不该影响接收
        }
    }

    public async Task CompleteAsync()
    {
        Process? process;

        lock (_lock)
        {
            process = _process;
            _stdin = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            // 关掉 stdin 就是告诉 aplay "数据完了"，它会播完缓冲再退出
            process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            // 超时或进程已消失：继续走 Stop 收尾
        }
        finally
        {
            Stop();
        }
    }

    public void Stop()
    {
        Process? process;

        lock (_lock)
        {
            process = _process;
            _process = null;
            _stdin = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 已经退出
        }

        process.Dispose();
    }

    public void Dispose() => Stop();
}