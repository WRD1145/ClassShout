using System.Diagnostics;
using ClassShout.Core.Audio;

namespace ClassShout.Classroom.Services;

/// <summary>
/// Linux 上的系统朗读：调用 speech-dispatcher 的 spd-say 或 espeak-ng。
///
/// 定位是**保底**：Linux 上真正的朗读引擎是 Edge 在线语音（音质高一个档次），
/// 而这个只有在既连不上外网、机器上又恰好装了语音合成时才会用到。
/// 两个命令都没有就直接判定不可用 —— 由上层记一条明确的日志，
/// 而不是让老师喊了半天不知道为什么教室里没声音。
/// </summary>
public sealed class ProcessSpeechSynthesizer : ISpeechSynthesizer
{
    private Process? _current;
    private readonly Lock _lock = new();
    private volatile bool _speaking;

    public event EventHandler<bool>? SpeakingChanged;

    public bool IsSpeaking => _speaking;

    /// <summary>本机有没有可用的命令行朗读工具。</summary>
    public static bool IsBackendAvailable => FindBackend(out _);

    /// <summary>实际选中的后端命令名。诊断用。</summary>
    public string Backend { get; private set; } = "未选择";

    private static bool FindBackend(out string command)
    {
        foreach (var candidate in new[] { "spd-say", "espeak-ng", "espeak" })
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, candidate)))
                    {
                        command = candidate;
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 继续找
                }
            }
        }

        command = string.Empty;
        return false;
    }

    /// <summary>
    /// 命令行朗读工具的"音色"很少能枚举出来，这里如实返回空列表，
    /// 让界面显示"由系统决定"，而不是编几个名字让人以为能选。
    /// </summary>
    public IReadOnlyList<string> GetVoices() => [];

    public string? GetDefaultChineseVoice() => null;

    public async Task SpeakAsync(
        string text,
        SpeechRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !FindBackend(out var command))
        {
            return;
        }

        var arguments = command == "spd-say"
            ? $"-w -r {Math.Clamp(options.Rate * 10, -100, 100)} {Quote(text)}"
            : $"-s {Math.Clamp(175 + (options.Rate * 20), 80, 450)} {Quote(text)}";

        var startInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        SetSpeaking(true);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            lock (_lock)
            {
                _current = process;
                Backend = command;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            // 被停止或命令不可执行
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
            }

            SetSpeaking(false);
        }
    }

    /// <summary>参数里带空格的文本要整体引起来，否则会被拆成多个参数。</summary>
    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"") + "\"";

    public void Stop()
    {
        Process? process;
        lock (_lock)
        {
            process = _current;
            _current = null;
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
            // 已退出
        }

        SetSpeaking(false);
    }

    private void SetSpeaking(bool value)
    {
        if (_speaking == value)
        {
            return;
        }

        _speaking = value;
        SpeakingChanged?.Invoke(this, value);
    }

    public void Dispose() => Stop();
}