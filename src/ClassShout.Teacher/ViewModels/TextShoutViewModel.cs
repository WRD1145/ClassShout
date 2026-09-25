using ClassShout.Core.Protocol;
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
/// 「这一条发给谁」里的一个班级。
///
/// 做成可观察对象而不是直接用 BoundClassroom：选中状态是界面状态，
/// 列表每次重建都会重来，所以它得挂在条目自己身上。
/// </summary>
public sealed partial class ShoutTargetItem : ObservableObject
{
    public ShoutTargetItem(BoundClassroom record, bool isSelected)
    {
        Record = record;
        _isSelected = isSelected;
    }

    public BoundClassroom Record { get; }

    public string Uuid => Record.Uuid;

    public string Name => Record.Name;

    /// <summary>
    /// 是不是当前正绑着的那一间。标出来，免得老师不知道自己正在对谁说话。
    ///
    /// 可写是为了让渲染校验把它摆出来 —— 否则那个徽标只有真的连上服务器绑一次才会被画到，
    /// 而"排版有没有走样"恰恰要在图上才看得出来。
    /// </summary>
    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isSelected;
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

        // 读进来就顺手规范化：这个文件是纯本机数据，可能被手改过或被旧版本写过，
        // 而一个读不懂的档位会让界面上"什么都没有被选中"。
        _displaySettings = LocalSettings.LoadTeacherDisplay().Normalized();

        foreach (var phrase in DefaultPresets)
        {
            Presets.Add(new TextPreset(phrase, ApplyPreset));
        }

        LoadHistory();
    }

    // ======================== 一次发给哪几个班 ========================

    /// <summary>可选的班级。和"已保存的教室"是同一批数据，只是带上勾选状态。</summary>
    public ObservableCollection<ShoutTargetItem> Targets { get; } = [];

    /// <summary>多班发送器。由外壳注入；为 null 时退化成"只发当前绑定那间"。</summary>
    public ClassroomBroadcaster? Broadcaster { get; set; }

    /// <summary>喊话时用的老师姓名（服务器以账号里的姓名为准，这里只是兜底）。</summary>
    public string TeacherName { get; set; } = string.Empty;

    /// <summary>多班发送结束后抛一句结果给外壳去提示。</summary>
    public event Action<string>? BroadcastFinished;

    /// <summary>列表里超过一间教室时才需要"选择发给谁"——只有一间的话，选它没有意义。</summary>
    public bool HasMultipleTargets => Targets.Count > 1;

    /// <summary>当前这一条会发给哪几间。</summary>
    public string TargetSummaryText
    {
        get
        {
            var selected = Targets.Where(t => t.IsSelected).ToList();

            return selected.Count switch
            {
                0 => "当前绑定的教室",
                1 => selected[0].Name,
                _ => $"{selected.Count} 个班级：{string.Join("、", selected.Select(t => t.Name))}",
            };
        }
    }

    /// <summary>
    /// 用最新的"已保存的教室"刷新勾选列表。
    ///
    /// 保留原来的勾选：老师勾了两个班、又去设备页切了个教室回来，
    /// 不该发现自己的勾选被清空了。第一次填充时默认只勾当前绑定的那一间。
    /// </summary>
    public void SyncTargets(IEnumerable<BoundClassroom> records, string? currentUuid)
    {
        var previous = Targets
            .Where(t => t.IsSelected)
            .Select(t => t.Uuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isFirstFill = Targets.Count == 0;

        Targets.Clear();

        foreach (var record in records)
        {
            var isCurrent = currentUuid is not null &&
                            string.Equals(record.Uuid, currentUuid, StringComparison.OrdinalIgnoreCase);

            var selected = isFirstFill ? isCurrent : previous.Contains(record.Uuid);

            var item = new ShoutTargetItem(record, selected) { IsCurrent = isCurrent };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShoutTargetItem.IsSelected))
                {
                    OnPropertyChanged(nameof(TargetSummaryText));
                }
            };

            Targets.Add(item);
        }

        // 一个都没勾上时兜个底：发送按钮不该因为"没勾任何班"而变成什么都不做。
        if (Targets.Count > 0 && Targets.All(t => !t.IsSelected))
        {
            var fallback = Targets.FirstOrDefault(t => t.IsCurrent) ?? Targets[0];
            fallback.IsSelected = true;
        }

        OnPropertyChanged(nameof(HasMultipleTargets));
        OnPropertyChanged(nameof(TargetSummaryText));
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

    // ======================== 展示参数 ========================
    //
    // 四项都是"这一次怎么显示"。上次用的那套存在本机，下次打开还是它 ——
    // 讲评试卷时习惯用"特大字 + 常驻"的老师，不该每节课都重选一遍。

    private readonly TeacherDisplaySettings _displaySettings;

    public IReadOnlyList<ShoutDisplayOption> DisplayOptions => ShoutDisplayChoices.Displays;

    public IReadOnlyList<ShoutFontSizeOption> FontSizeOptions => ShoutDisplayChoices.FontSizes;

    public IReadOnlyList<ShoutHoldOption> HoldOptions => ShoutDisplayChoices.Holds;

    public ShoutDisplayOption SelectedDisplay
    {
        get => DisplayOptions.FirstOrDefault(o => o.Value == _displaySettings.Display) ?? DisplayOptions[0];
        set
        {
            if (value is null || _displaySettings.Display == value.Value)
            {
                return;
            }

            _displaySettings.Display = value.Value;
            LocalSettings.SaveTeacherDisplay(_displaySettings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplaySummaryText));
        }
    }

    public ShoutFontSizeOption SelectedFontSize
    {
        get => FontSizeOptions.FirstOrDefault(o => o.Value == _displaySettings.FontSize) ?? FontSizeOptions[1];
        set
        {
            if (value is null || _displaySettings.FontSize == value.Value)
            {
                return;
            }

            _displaySettings.FontSize = value.Value;
            LocalSettings.SaveTeacherDisplay(_displaySettings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplaySummaryText));
        }
    }

    public ShoutHoldOption SelectedHold
    {
        get => HoldOptions.FirstOrDefault(o => o.Value == _displaySettings.HoldMs) ?? HoldOptions[1];
        set
        {
            if (value is null || _displaySettings.HoldMs == value.Value)
            {
                return;
            }

            _displaySettings.HoldMs = value.Value;
            LocalSettings.SaveTeacherDisplay(_displaySettings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplaySummaryText));
        }
    }

    /// <summary>这次要不要让教室端朗读。关掉就是"只把字摆出来"，适合上课时不想打断讲解的场合。</summary>
    public bool Speak
    {
        get => _displaySettings.Speak;
        set
        {
            if (_displaySettings.Speak == value)
            {
                return;
            }

            _displaySettings.Speak = value;
            LocalSettings.SaveTeacherDisplay(_displaySettings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplaySummaryText));
        }
    }

    /// <summary>一句话说清这次会怎么显示，放在选项旁边给人核对。</summary>
    public string DisplaySummaryText =>
        $"{SelectedDisplay.Label} · {SelectedFontSize.Label}字 · {SelectedHold.Label}"
        + (Speak ? " · 朗读" : " · 不朗读");

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

    // ======================== 发送队列 ========================

    /// <summary>发送队列。由外壳注入 —— 队列要在页面之间共享，不能每个页面各排各的。</summary>
    public ShoutQueue? Queue { get; set; }

    private long _myTicket;

    [ObservableProperty]
    private string _queueStatus = string.Empty;

    /// <summary>自己这条是不是还在排队（前面有人）。界面据此把提示显示得醒目一点。</summary>
    [ObservableProperty]
    private bool _isQueued;

    /// <summary>
    /// 刷新"前面还有几个"。
    ///
    /// 每次都重新问队列，而不是在入队时把数字记下来自己减 ——
    /// 队列是共享的，别的页面也会往里放东西，自己维护计数迟早对不上。
    /// </summary>
    public void RefreshQueueStatus()
    {
        if (Queue is null)
        {
            QueueStatus = string.Empty;
            IsQueued = false;
            return;
        }

        var ahead = Queue.PositionOf(_myTicket);
        var pending = Queue.PendingCount;

        IsQueued = ahead > 0;

        QueueStatus = ahead > 0
            ? $"排队中 · 前面还有 {ahead} 条"
            : pending > 0
                ? $"队列中还有 {pending} 条（本页已发出）"
                : string.Empty;
    }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasText));

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Text;

        // 参数在入队时就固定下来：这条喊话用的是"点发送那一刻"的语速、音量与展示参数，
        // 而不是轮到它发送时界面上的值 —— 老师排了三条又去调了字号，
        // 前面那两条不该跟着变。
        var message = new TextShoutMessage
        {
            Text = text.Trim(),
            Rate = Rate,
            Volume = Volume,
            Interrupt = Interrupt,
            Display = SelectedDisplay?.Value,
            FontSize = SelectedFontSize?.Value,
            HoldMs = SelectedHold?.Value ?? ShoutHoldDurations.Unspecified,
            Speak = Speak,
        };

        var sent = text.Trim();

        if (Queue is null)
        {
            // 没有队列（理论上不该发生）时退回直接发，至少不至于点了没反应
            _ = SendDirectAsync(message);
            Text = string.Empty;
            return;
        }

        _myTicket = Queue.Enqueue(sent, ct => SendToTargetsAsync(message, ct));

        // 立刻清空输入框：排队的意义就是让老师可以连着喊好几条
        Text = string.Empty;
        RefreshQueueStatus();
    }

    /// <summary>
    /// 把这一条发出去。勾了一间就走原来的链路（局域网直连仍然优先）；
    /// 勾了多间则逐个经中继发送，并把"哪几间没送到"如实报出来。
    /// </summary>
    private async Task<bool> SendToTargetsAsync(TextShoutMessage message, CancellationToken cancellationToken)
    {
        var selected = Targets.Where(t => t.IsSelected).Select(t => t.Record).ToList();

        // 单目标（或没有多班发送器）时保持原样：这条路上有局域网直连优先的逻辑，
        // 而多班喊话必然跨网络 —— 老师不可能同时待在两个班的局域网里。
        if (selected.Count <= 1 || Broadcaster is null)
        {
            return await _channel.SendTextAsync(message, cancellationToken).ConfigureAwait(true);
        }

        try
        {
            var results = await Broadcaster
                .SendTextAsync(selected, message, TeacherName, cancellationToken)
                .ConfigureAwait(true);

            var sentCount = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();

            var summary = failed.Count == 0
                ? $"已发给 {sentCount} 个班级。"
                : $"已发给 {sentCount} 个班级，{failed.Count} 个没送到：{string.Join("、", failed.Select(r => r.Classroom.Name))}。";

            BroadcastFinished?.Invoke(summary);

            // 全都失败了才算这条没发出去（历史记录只记真的送到的）
            return sentCount > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            BroadcastFinished?.Invoke($"多班喊话失败：{ex.Message}");
            return false;
        }
    }

    private async Task SendDirectAsync(TextShoutMessage message)
    {
        IsSending = true;

        try
        {
            if (await _channel.SendTextAsync(message))
            {
                RecordHistory(message.Text);
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

    /// <summary>队列报"这条发出去了"时调用。历史只记真的发出去的。</summary>
    internal void OnQueueSent(string text, bool ok)
    {
        RefreshQueueStatus();

        if (ok)
        {
            RecordHistory(text);
        }
    }

    private void RecordHistory(string text)
    {
        // 记一条本机历史。放在"发送成功"之后而不是点击时：
        // 没发出去的喊话不该出现在"我喊过什么"里。
        var updated = ShoutHistoryStore.Record(text, isVoice: false);

        RecentShouts.Clear();
        foreach (var record in updated)
        {
            RecentShouts.Add(new ShoutRecordItem(record));
        }

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryHintText));
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
