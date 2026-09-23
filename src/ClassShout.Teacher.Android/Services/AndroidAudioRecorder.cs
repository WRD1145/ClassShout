using Android.App;
using Android.Media;
using ClassShout.Core.Audio;
using AndroidEncoding = Android.Media.Encoding;
// Android.Media 里也有一个同名类型，这里用别名明确指向我们自己的格式定义
using AudioFormat = ClassShout.Core.Audio.AudioFormat;

namespace ClassShout.Teacher.Android.Services;

/// <summary>
/// Android 端的麦克风采集，基于 <see cref="AudioRecord"/>。
///
/// 与桌面头共用同一个 <see cref="IAudioRecorder"/> 契约，
/// 所以共享 UI 层完全不知道底下是 NAudio 还是 AudioRecord。
///
/// 权限说明：清单里声明 RECORD_AUDIO 只是第一步，Android 6 起还必须在运行时动态申请
/// （见 MainActivity），否则这里会直接抛异常。缺权限时给出可操作的提示，而不是 NullReference。
/// </summary>
public sealed class AndroidAudioRecorder : IAudioRecorder
{
    /// <summary>每片 20 毫秒，与协议分片粒度一致，端到端延迟最低。</summary>
    private const int ChunkMilliseconds = 20;

    private readonly Activity _activity;

    private AudioRecord? _record;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public AndroidAudioRecorder(Activity activity)
    {
        _activity = activity;
    }

    /// <summary>16 kHz 单声道：人声足够清晰，32 KB/s 在局域网上几乎不占带宽。</summary>
    public AudioFormat Format { get; } = new(16000, 1, 16);

    public bool IsRecording => _record?.RecordingState == RecordState.Recording;

    public event EventHandler<float>? LevelChanged;

    /// <inheritdoc />
    public event EventHandler<string>? Failed;

    public Task StartAsync(Action<ReadOnlyMemory<byte>> onData, CancellationToken cancellationToken = default)
    {
        if (_record is not null)
        {
            return Task.CompletedTask;
        }

        EnsurePermission();

        var minBuffer = AudioRecord.GetMinBufferSize(Format.SampleRate, ChannelIn.Mono, AndroidEncoding.Pcm16bit);
        if (minBuffer <= 0)
        {
            throw new InvalidOperationException($"本机声卡不支持 {Format} 的录音格式。");
        }

        var chunkBytes = Format.BytesForDuration(ChunkMilliseconds);

        // 底层缓冲给到 8 片，避免系统调度抖动导致丢帧
        var bufferSize = Math.Max(minBuffer, chunkBytes * 8);

        var record = new AudioRecord(
            AudioSource.Mic,
            Format.SampleRate,
            ChannelIn.Mono,
            AndroidEncoding.Pcm16bit,
            bufferSize);

        if (record.State != State.Initialized)
        {
            record.Release();
            throw new InvalidOperationException("麦克风初始化失败，可能已被其他应用占用。");
        }

        _record = record;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        record.StartRecording();
        _readLoop = Task.Run(() => ReadLoopAsync(record, chunkBytes, onData, _cts.Token));

        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(
        AudioRecord record,
        int chunkBytes,
        Action<ReadOnlyMemory<byte>> onData,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 每次读都换一块新缓冲，不复用同一块。
                //
                // 复用会把同一段内存反复交给 onData：当前那个消费方刚好立刻
                // ToArray() 复制走了，所以没出过事；但这属于"契约上允许持有、
                // 实际上一持有就被改写"，桌面实现也不是这么做的。
                // 20 毫秒一片、每秒 50 次、每次 640 字节，这点分配可以忽略。
                var buffer = new byte[chunkBytes];

                // 阻塞式读取：读满一片或超时返回，跑在后台线程上不影响界面
                var read = record.Read(buffer, 0, buffer.Length);

                if (read > 0)
                {
                    onData(buffer.AsMemory(0, read));

                    var level = (float)PcmLevel.Compute(buffer.AsSpan(0, read));
                    LevelChanged?.Invoke(this, level);
                }
                else if (read < 0)
                {
                    // 负数表示出错（如设备被抢占）。
                    //
                    // 原来这里直接 break 就走人了，界面那边毫不知情：
                    // 计时器继续走、按钮还显示"正在录音"，而音频早就断了。
                    // 老师对着手机喊半天，教室里一点声音都没有。
                    Failed?.Invoke(this, "麦克风读取失败，录音已中断（可能被其他应用占用）。");
                    break;
                }
                else
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private void EnsurePermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return;
        }

        var granted = _activity.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio)
                      == global::Android.Content.PM.Permission.Granted;

        if (!granted)
        {
            throw new InvalidOperationException(
                "尚未授予麦克风权限。请在弹出的系统提示中允许录音，或到「设置 → 应用 → ClassShout 教师端 → 权限」里手动开启。");
        }
    }

    public async Task StopAsync()
    {
        var record = _record;
        if (record is null)
        {
            return;
        }

        _record = null;
        _cts?.Cancel();

        try
        {
            if (record.RecordingState == RecordState.Recording)
            {
                record.Stop();
            }
        }
        catch (global::Java.Lang.IllegalStateException)
        {
            // 已在停止过程中，忽略
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 读线程没能在 1 秒内退出也不能卡住界面
            }

            _readLoop = null;
        }

        record.Release();
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        LevelChanged = null;
    }
}
