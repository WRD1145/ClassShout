using ClassShout.Core.Remote;
using System.Collections.ObjectModel;
using ClassShout.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

/// <summary>
/// 一条常用语。
///
/// 之所以把它做成自带命令的对象，而不是纯字符串：
/// 字符串列表在 XAML 模板里想调用父级视图模型的命令，得写
/// <c>$parent[ItemsControl].((vm:TextShoutViewModel)DataContext).UsePresetCommand</c>
/// 这类又长又容易写错的绑定。让每一项自带命令，模板里一行就够。
/// </summary>
public sealed class TextPreset
{
    public TextPreset(string text, Action<string> apply)
    {
        Text = text;
        ApplyCommand = new RelayCommand(() => apply(text));
    }

    /// <summary>常用语内容。</summary>
    public string Text { get; }

    /// <summary>点击后把内容填入输入框。</summary>
    public IRelayCommand ApplyCommand { get; }
}

/// <summary>
/// 文字喊话页。
/// 输入的文字会发到教室端，由教室端的系统 TTS 朗读 —— 手机端不发声，
/// 这样声音一定从教室的音响/投影喇叭出来，符合"喊话"的实际场景。
/// </summary>
public partial class TextShoutViewModel : ObservableObject
{
    private readonly IShoutTransport _channel;

    public TextShoutViewModel(IShoutTransport channel)
    {
        _channel = channel;

        foreach (var phrase in DefaultPresets)
        {
            Presets.Add(new TextPreset(phrase, ApplyPreset));
        }

        LoadHistory();
    }

    /// <summary>最近喊话（本机保存，最多二十条，最新在前）。</summary>
    public ObservableCollection<ShoutRecordItem> RecentShouts { get; } = [];

    private void LoadHistory()
    {
        RecentShouts.Clear();

        foreach (var record in ShoutHistoryStore.Load().Recent)
        {
            RecentShouts.Add(new ShoutRecordItem(record));
        }

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryHintText));
    }

    public bool HasHistory => RecentShouts.Count > 0;

    public string HistoryHintText => RecentShouts.Count == 0
        ? $"喊过的内容会留在这里（最多 {ShoutHistoryStore.MaxCount} 条）"
        : $"最近 {RecentShouts.Count} 条 · 最多保留 {ShoutHistoryStore.MaxCount} 条";

    private static readonly string[] DefaultPresets =
    [
        "同学们请安静",
        "请注意看黑板",
        "这道题我再讲一遍",
        "请翻到课本第 __ 页",
        "课代表把作业收上来",
        "下课后请到办公室找我",
        "距离下课还有十分钟",
        "请把手机收起来",
    ];

    private void ApplyPreset(string preset) => Text = preset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(CharacterCount))]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateText))]
    private int _rate = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private int _volume = 100;

    /// <summary>新喊话是否打断教室端当前正在朗读的内容。</summary>
    [ObservableProperty]
    private bool _interrupt = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    /// <summary>常用语：课堂上反复要说的话，点一下直接填入，避免每次手打。</summary>
    public ObservableCollection<TextPreset> Presets { get; } = [];

    public int CharacterCount => Text.Length;

    public string CharacterCountText => $"{CharacterCount} 字 / 建议 60 字以内";

    public string RateText => Rate switch
    {
        < 0 => $"慢 {Math.Abs(Rate)}",
        > 0 => $"快 {Rate}",
        _ => "正常",
    };

    public string VolumeText => $"{Volume}%";

    public bool CanSend => IsConnected && !IsSending && !string.IsNullOrWhiteSpace(Text);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasText));

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        IsSending = true;

        try
        {
            var sent = Text;
            var ok = await _channel.SendTextAsync(sent, Rate, Volume, voiceName: null, Interrupt);
            if (ok)
            {
                // 记一条本机历史。放在"发送成功"之后而不是点击时：
                // 没发出去的喊话不该出现在"我喊过什么"里。
                var updated = ShoutHistoryStore.Record(sent, isVoice: false);

                RecentShouts.Clear();
                foreach (var record in updated)
                {
                    RecentShouts.Add(new ShoutRecordItem(record));
                }

                OnPropertyChanged(nameof(HasHistory));
                OnPropertyChanged(nameof(HistoryHintText));

                // 发送成功就清空输入框，方便连续喊话
                Text = string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // 连接已断，交给外壳视图模型的 ConnectionChanged 去更新界面
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void UsePreset(string? preset)
    {
        if (!string.IsNullOrWhiteSpace(preset))
        {
            Text = preset;
        }
    }

    [RelayCommand]
    private void Clear() => Text = string.Empty;

    /// <summary>把某条历史重新填回输入框 —— 课堂上"再说一遍"是最常见的需求。</summary>
    [RelayCommand]
    private void UseHistory(ShoutRecordItem? item)
    {
        if (item is not null)
        {
            Text = item.Text;
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        ShoutHistoryStore.Clear();
        LoadHistory();
    }

    /// <summary>让教室端立刻停止朗读。</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            await _channel.RequestStopAsync("教师端中止朗读");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // 忽略：连接已断时无需再发停止指令
        }
    }
}
