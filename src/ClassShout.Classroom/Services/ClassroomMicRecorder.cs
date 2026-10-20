using System.Diagnostics;
using ClassShout.Core.Audio;

namespace ClassShout.Classroom.Services;

/// <summary>
/// 教室端的麦克风采集（只为"语音回复"用）。
///
/// 采集出来的是一段**完整的 WAV**，而不是像教师端那样边录边流：
/// 语音回复是十几秒的小片段，一次发完比分片少一整类"中途断开"的状态
/// （教室端那句"没发出去"就足够让人知道该再点一次）。
///
/// 两端各一套后端，和播放那边一样用编译期分支：
///   · Windows：NAudio 的 WaveIn（16 kHz / 单声道 / 16 bit，与喊话同一种格式，
///     教师端播放时才不用为"回复是另一种格式"多写一条分支）；
///   · Linux：arecord（alsa-utils）。教室里那台 Linux 机器通常没有 .NET 之外的
///     音频库，而 arecord 几乎必然随声卡驱动一起装上。
/// </summary>
public sealed class ClassroomMicRecorder : IDisposable
{
    /// <summary>采集格式。与喊话一致：16 kHz、单声道、16 bit。</summary>
    public static AudioFormat Format => AudioFormat.Default;

    private readonly MemoryStream _pcm = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _pump;

#if WINDOWS
    private NAudio.Wave.WaveInEvent? _waveIn;
#else
    private Process? _arecord;
#endif

    /// <summary>本机有没有可用的采集后端（没有时界面上就不该出现"语音回复"）。</summary>
    public static bool IsAvailable
    {
        get
        {
#if WINDOWS
            try
            {
                // 只探测"有没有输入设备"，不真的开录：开录会占用麦克风，
                // 而在这个属性被读的时候（界面渲染设置页）很可能正录着别的东西。
                return NAudio.Wave.WaveInEvent.DeviceCount > 0;
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or PlatformNotSupportedException)
            {
                return false;
            }
#else
            return FindOnPath("arecord") is not null;
#endif
        }
    }

    /// <summary>开始采集。已经在录时不做任何事。</summary>
    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        lock (_gate)
        {
            _pcm.SetLength(0);
        }

        _cts = new CancellationTokenSource();

#if WINDOWS
        var device = new NAudio.Wave.WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new NAudio.Wave.WaveFormat(Format.SampleRate, Format.BitsPerSample, Format.Channels),
            BufferMilliseconds = 100,
        };

        device.DataAvailable += (_, e) =>
        {
            lock (_gate)
            {
                _pcm.Write(e.Buffer, 0, e.BytesRecorded);
            }
        };

        device.StartRecording();
        _waveIn = device;
#else
        var arecord = FindOnPath("arecord")
                      ?? throw new InvalidOperationException("找不到 arecord：请先安装 alsa-utils。");

        var start = new ProcessStartInfo(arecord)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 原始 PCM 直接走 stdout：省掉"先写一个临时文件再读回来"，
        // 也避免录到一半被别的进程抢走文件。
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("S16_LE");
        start.ArgumentList.Add("-r");
        start.ArgumentList.Add(Format.SampleRate.ToString());
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Format.Channels.ToString());
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("raw");

        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("arecord 启动失败。");

        _arecord = process;
        _pump = Task.Run(() => PumpAsync(process, _cts!.Token));
#endif
    }

    /// <summary>
    /// 停止采集并返回 WAV。
    /// </summary>
    /// <returns>WAV 字节与时长；没有采到任何音频时返回 null（界面据此提示"没录到声音"）。</returns>
    public (byte[] Wav, int DurationMs)? Stop()
    {
        var cts = _cts;
        _cts = null;

        cts?.Cancel();

#if WINDOWS
        if (_waveIn is { } device)
        {
            try
            {
                device.StopRecording();
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                // 停止失败不影响已经收到的数据
            }

            device.Dispose();
            _waveIn = null;
        }
#else
        if (_arecord is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.WaitForExit(2000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 进程已经没了
            }

            process.Dispose();
            _arecord = null;
        }

        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 采集泵的异常不影响已经拿到的数据
        }

        _pump = null;
#endif

        cts?.Dispose();

        byte[] pcm;

        lock (_gate)
        {
            pcm = _pcm.ToArray();
        }

        if (pcm.Length == 0)
        {
            return null;
        }

        // 16 kHz / 单声道 / 16 bit：每秒 32000 字节
        var bytesPerSecond = Format.SampleRate * Format.Channels * (Format.BitsPerSample / 8);
        var durationMs = (int)Math.Round(pcm.Length * 1000.0 / Math.Max(1, bytesPerSecond));

        return (WavCodec.Encode(Format, pcm), durationMs);
    }

    /// <summary>放弃本次采集。</summary>
    public void Cancel()
    {
        _ = Stop();
    }

    public void Dispose()
    {
        _ = Stop();
        _pcm.Dispose();
    }

#if !WINDOWS
    private async Task PumpAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[8192];
            var stream = process.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                lock (_gate)
                {
                    _pcm.Write(buffer, 0, read);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 停止时本来就会被取消
        }
    }

    private static string? FindOnPath(string name)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        return paths
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
#endif
}
