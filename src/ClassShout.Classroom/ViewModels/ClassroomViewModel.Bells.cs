using System.Buffers.Binary;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClassShout.Classroom.Services;
using ClassShout.Core.Audio;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Classroom.ViewModels;

public partial class ClassroomViewModel
{
    public ObservableCollection<BellSchedule> Bells { get; } = new(LocalSettings.LoadBells().Bells);
    private readonly Dictionary<string, DateOnly> _bellFired = [];
    private DispatcherTimer? _bellTimer;
    // 铃声独立占用输出，避免教师端停止喊话顺手把上课铃清掉。
    private readonly IAudioPlayer _bellPlayer = ClassroomPlatform.CreatePlayer();
    private bool _bellPlaying;
    [ObservableProperty] private string _bellStatus = "铃声保存在本机；应用运行时断网也能响。";

    private void StartBells()
    {
        if (_bellTimer is not null) return;
        _bellTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _bellTimer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            foreach (var bell in Bells.ToArray())
            {
                if (!bell.IsDue(now) || _bellFired.GetValueOrDefault(bell.Id) == today) continue;
                _bellFired[bell.Id] = today;
                AddLog("铃声", $"本地触发：{bell.Label}（{bell.Time}），不依赖网络。");
                _ = PlayBellAsync(bell);
            }
        };
        _bellTimer.Start();
    }

    [RelayCommand] private void AddBell()
        => Bells.Add(new BellSchedule { Time = DateTime.Now.AddMinutes(1).ToString("HH:mm") });

    [RelayCommand] private void RemoveBell(BellSchedule? bell)
    {
        if (bell is not null) Bells.Remove(bell);
        SaveBells();
    }

    [RelayCommand] private void SaveBells()
    {
        var settings = new BellSettings { Bells = Bells.ToList() }.Normalized();
        BellStatus = LocalSettings.SaveBells(settings)
            ? "铃声已保存到本机。无效时刻会被禁用；周几为 1–127 位掩码。"
            : "铃声保存失败，重启后会丢失，请检查配置目录权限。";
        Bells.Clear();
        foreach (var bell in settings.Bells) Bells.Add(bell);
        AddLog("铃声", BellStatus);
    }

    [RelayCommand] private Task PreviewBellAsync(BellSchedule? bell)
        => bell is null ? Task.CompletedTask : PlayBellAsync(bell);

    private async Task PlayBellAsync(BellSchedule bell)
    {
        if (_bellPlaying)
        {
            AddLog("铃声", $"{bell.Label} 未播放：上一条铃声仍在播放。");
            return;
        }
        _bellPlaying = true;
        try
        {
            var format = AudioFormat.Default;
            byte[] pcm;
            if (!string.IsNullOrWhiteSpace(bell.AudioWav))
            {
                if (!WavCodec.TryDecode(Convert.FromBase64String(bell.AudioWav), out format, out pcm)
                    || !format.IsSupported || format.BitsPerSample != 16)
                    throw new InvalidDataException("铃声需要受支持的 16 位 PCM WAV。");
                for (var i = 0; i + 1 < pcm.Length; i += 2)
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i),
                        (short)(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i)) * Math.Clamp(bell.Volume, 0, 100) / 100));
            }
            else
            {
                var count = format.SampleRate * Math.Clamp(bell.DurationMs, 100, 30000) / 1000;
                pcm = new byte[count * 2];
                for (var i = 0; i < count; i++)
                {
                    // 首尾淡入淡出，避免硬切造成爆音。
                    var fade = Math.Min(1d, Math.Min(i, count - 1 - i) / 160d);
                    var value = (short)(Math.Sin(2 * Math.PI * Math.Clamp(bell.FrequencyHz, 100, 4000) * i / format.SampleRate)
                        * 16000 * Math.Clamp(bell.Volume, 0, 100) / 100 * fade);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
                }
            }
            if (bell.ShowLabel)
                NotifyOnScreen("本地铃声", bell.Label, ShoutNoticeKind.Text);
            _bellPlayer.Start(format);
            _bellPlayer.Write(pcm);
            await _bellPlayer.CompleteAsync();
            AddLog("铃声", $"播放完成：{bell.Label}");
        }
        catch (Exception ex)
        {
            AddLog("铃声", $"播放失败：{bell.Label}，{ex.Message}");
        }
        finally { _bellPlaying = false; }
    }
}
