using ClassShout.Core.Audio;
using ClassShout.Core.Remote;

namespace ClassShout.Teacher.Services;

/// <summary>录好的一段语音。</summary>
/// <param name="Format">采集格式。</param>
/// <param name="Pcm">裸 PCM 字节。</param>
public readonly record struct VoiceClip(AudioFormat Format, byte[] Pcm)
{
    /// <summary>时长（秒）。</summary>
    public double Seconds => Format.DurationMsOf(Pcm.Length) / 1000.0;

    public bool IsEmpty => Pcm.Length == 0;

    /// <summary>包成 WAV，便于落盘与上传。</summary>
    public byte[] ToWav() => WavCodec.Encode(Format, Pcm);
}

/// <summary>
/// 为定时喊话录一段语音。
///
/// 与语音喊话页的区别只有一个，但很关键：**这里不边录边发**。
/// 定时喊话要的是"先把这段声音存下来，到点再放"，所以 PCM 全部攒在内存里，
/// 松手之后交给 <see cref="VoiceClipStore"/> 落盘。
///
/// 时长上限沿用服务器那条（<see cref="ServerScheduledShout.MaxVoiceSeconds"/>）：
/// 两边不一样的话，会出现"录的时候没拦住、排的时候服务器拒收"这种最气人的顺序。
/// </summary>
public sealed class VoiceClipRecorder : IAsyncDisposable
{
    /// <summary>攒着的 PCM 上限（字节）。超过就自动停 —— 手机内存不是无限的。</summary>
    private static readonly int MaxBytes =
        AudioFormat.Default.BytesForDuration(ServerScheduledShout.MaxVoiceSeconds * 1000);

    private readonly List<byte[]> _chunks = [];
    private IAudioRecorder? _recorder;
    private int _bytes;

    /// <summary>正在录。</summary>
    public bool IsRecording => _recorder?.IsRecording == true;

    /// <summary>采集格式；开始录之前是默认格式。</summary>
    public AudioFormat Format { get; private set; } = AudioFormat.Default;

    /// <summary>已录时长（秒）。</summary>
    public double Seconds => Format.DurationMsOf(_bytes) / 1000.0;

    /// <summary>音量电平，界面画电平条用。</summary>
    public event Action<float>? LevelChanged;

    /// <summary>到了时长上限自动停下，界面据此提示"最长就是这么长"。</summary>
    public event Action? ReachedLimit;

    /// <summary>采集中途失败（设备被抢占等）。</summary>
    public event Action<string>? Failed;

    /// <summary>开始录。返回是否真的开始了。</summary>
    public async Task<bool> StartAsync()
    {
        if (IsRecording)
        {
            return true;
        }

        if (!TeacherPlatform.HasRecorder)
        {
            Failed?.Invoke("本平台尚未注册麦克风采集实现。");
            return false;
        }

        _chunks.Clear();
        _bytes = 0;

        var recorder = TeacherPlatform.CreateAudioRecorder();
        Format = recorder.Format;

        recorder.LevelChanged += OnLevel;
        recorder.Failed += OnFailed;

        try
        {
            await recorder.StartAsync(OnData).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            recorder.LevelChanged -= OnLevel;
            recorder.Failed -= OnFailed;
            await recorder.DisposeAsync().ConfigureAwait(true);
            Failed?.Invoke($"打不开麦克风：{ex.Message}");
            return false;
        }

        _recorder = recorder;
        return true;
    }

    /// <summary>停止并交出这段语音。没录到东西时返回 null。</summary>
    public async Task<VoiceClip?> StopAsync()
    {
        var recorder = _recorder;
        _recorder = null;

        if (recorder is not null)
        {
            recorder.LevelChanged -= OnLevel;
            recorder.Failed -= OnFailed;

            try
            {
                await recorder.StopAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 停不下来也得往下走：先把已经采到的交出去，总比整段丢掉强
            }

            await recorder.DisposeAsync().ConfigureAwait(true);
        }

        if (_bytes == 0)
        {
            return null;
        }

        var pcm = new byte[_bytes];
        var offset = 0;

        foreach (var chunk in _chunks)
        {
            chunk.CopyTo(pcm, offset);
            offset += chunk.Length;
        }

        _chunks.Clear();
        _bytes = 0;
        LevelChanged?.Invoke(0);

        return new VoiceClip(Format, pcm);
    }

    private void OnData(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        // 回调在采集线程上，这里只做加法与入列，不碰界面
        _chunks.Add(data.ToArray());
        _bytes += data.Length;

        if (_bytes >= MaxBytes)
        {
            ReachedLimit?.Invoke();
        }
    }

    private void OnLevel(object? sender, float level) => LevelChanged?.Invoke(level);

    private void OnFailed(object? sender, string message) => Failed?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        if (_recorder is not null)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 定时语音的落盘。
///
/// 存在用户数据目录下的 <c>schedule-audio</c> 子目录：定时任务是本机资料，
/// 和教室身份、名单一样属于"这位老师自己的东西"。
/// </summary>
public static class VoiceClipStore
{
    /// <summary>子目录名。</summary>
    public const string FolderName = "schedule-audio";

    private static string Directory
    {
        get
        {
            var path = Path.Combine(LocalSettings.Directory, FolderName);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>把一段语音写成 WAV，返回文件名（不含目录）。失败返回 null。</summary>
    public static string? Save(string id, VoiceClip clip)
    {
        try
        {
            var fileName = id + ".wav";
            File.WriteAllBytes(Path.Combine(Directory, fileName), clip.ToWav());
            return fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读回一段语音。读不出来（文件丢了、被手改坏了）返回 null。</summary>
    public static VoiceClip? Load(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(Directory, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return WavCodec.TryDecode(File.ReadAllBytes(path), out var format, out var pcm)
                ? new VoiceClip(format, pcm)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>删掉一段语音。删不掉也不报错 —— 几百 KB 的残留不值得打断流程。</summary>
    public static void Delete(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        try
        {
            var path = Path.Combine(Directory, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略
        }
    }
}
