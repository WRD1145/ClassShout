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
///
/// 内容是可观察的：编辑模式下每一行就是一个输入框，改的正是这里的 Text。
/// </summary>
public sealed partial class TextPreset : ObservableObject
{
    public TextPreset(string text, Action<string> apply, Action<TextPreset> remove)
    {
        _text = text;
        ApplyCommand = new RelayCommand(() => apply(Text));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>常用语内容。</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>点击后把内容填入输入框。</summary>
    public IRelayCommand ApplyCommand { get; }

    /// <summary>编辑模式下删掉这一条。</summary>
    public IRelayCommand RemoveCommand { get; }
}

/// <summary>
/// 一条喊话要发到哪儿。两种来源合成同一张列表，就是「可发送班级」。
///
///   · <see cref="Lan"/> —— 屏幕上正连着的这间（局域网直连，不经过服务器）；
///   · <see cref="Server"/> —— 服务器上授权给我的班级（经中继服务器）。
///
/// 为什么把两条路并成一张表：老师关心的是"这条会发到哪个班"，而不是
/// "它走的是哪条网络"。以前这两类各占一块地方（一张勾选列表 + 一行"当前走局域网"的
/// 说明文字），于是同时连着局域网又绑着服务器时，屏幕上出现两处"发给谁"，
/// 谁也说不清点发送到底会发给哪一个。
/// </summary>
public static class ShoutTargetSources
{
    public const string Lan = "lan";

    public const string Server = "server";

    public static bool IsLan(string? source) => string.Equals(source, Lan, StringComparison.Ordinal);

    public static string Label(string? source) => IsLan(source) ? "局域网直连" : "经服务器";
}

/// <summary>「可发送班级」里的一项：外壳算好的、各页共用的一份描述。</summary>
/// <param name="Uuid">教室 UUID。</param>
/// <param name="Name">教室名。</param>
/// <param name="Source">走哪条路，取值见 <see cref="ShoutTargetSources"/>。</param>
/// <param name="IsOnline">这间教室现在是否在线。</param>
/// <param name="IsCurrent">是不是当前正连（绑）着的那一间。</param>
/// <param name="Record">
/// 服务器那条路要用它（含口令与服务器地址）；局域网直连那条没有绑定记录，
/// 为 null —— 因为"我们本来就连着它"这件事本身就是凭据。
/// </param>
public sealed record ShoutTargetInfo(
    string Uuid,
    string Name,
    string Source,
    bool IsOnline,
    bool IsCurrent,
    BoundClassroom? Record);

/// <summary>
/// 「可发送班级」列表里的一项。
///
/// 做成可观察对象而不是直接用 <see cref="ShoutTargetInfo"/>：选中状态是界面状态，
/// 列表每次重建都会重来，所以它得挂在条目自己身上。
/// </summary>
public sealed partial class ShoutTargetItem : ObservableObject
{
    public ShoutTargetItem(ShoutTargetInfo info, bool isSelected)
    {
        Info = info;
        _isSelected = isSelected;
    }

    public ShoutTargetInfo Info { get; }

    public string Uuid => Info.Uuid;

    public string Name => Info.Name;

    /// <summary>走哪条路（"局域网直连" / "经服务器"）。</summary>
    public string SourceLabel => ShoutTargetSources.Label(Info.Source);

    public bool IsLan => ShoutTargetSources.IsLan(Info.Source);

    /// <summary>服务器上绑定来的那一项才带绑定记录；局域网直连的那项没有。</summary>
    public BoundClassroom? Record => Info.Record;

    public bool IsOnline => Info.IsOnline;

    /// <summary>在线状态那一小行字。局域网直连必然是通的 —— 不通就发不出去。</summary>
    public string OnlineText => IsLan ? "已连接" : IsOnline ? "在线" : "当前离线";

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

    /// <summary>这位老师的常用语（本机保存，见 <see cref="TeacherPhraseSettings"/>）。</summary>
    private TeacherPhraseSettings _phraseSettings;

    public TextShoutViewModel(IShoutTransport channel)
    {
        _channel = channel;

        // 读进来就顺手规范化：这个文件是纯本机数据，可能被手改过或被旧版本写过，
        // 而一个读不懂的档位会让界面上"什么都没有被选中"。
        _displaySettings = LocalSettings.LoadTeacherDisplay().Normalized();

        _phraseSettings = LocalSettings.LoadPhrases().Normalized();
        BuildPresets();

        LoadHistory();
    }

    // ======================== 一次发给哪几个班 ========================

    /// <summary>可选的班级。和"已保存的教室"是同一批数据，只是带上勾选状态。</summary>
    public ObservableCollection<ShoutTargetItem> Targets { get; } = [];

    /// <summary>多班发送器。由外壳注入；为 null 时退化成"只发当前绑定那间"。</summary>
    public ClassroomBroadcaster? Broadcaster { get; set; }

    /// <summary>喊话时用的老师姓名（服务器以账号里的姓名为准，这里只是兜底）。</summary>
    public string TeacherName { get; set; } = string.Empty;

    /// <summary>
    /// 「这位老师在某个班叫什么」。
    ///
    /// 按班级取，是因为一位老师在不同班可能教不同科目 —— 教室里看到的来源
    /// （"数学张老师"／"信息技术张老师"）是跟着那个班走的。
    /// 为 null 时一律用 <see cref="TeacherName"/>（自检与预览里就是这样）。
    /// </summary>
    public Func<string?, string>? ShoutNameFor { get; set; }

    private string NameFor(string? uuid) => ShoutNameFor?.Invoke(uuid) ?? TeacherName;

    /// <summary>多班发送结束后抛一句结果给外壳去提示。</summary>
    public event Action<string>? BroadcastFinished;

    /// <summary>列表里超过一间教室时才需要"选择发给谁"——只有一间的话，选它没有意义。</summary>
    public bool HasMultipleTargets => Targets.Count > 1;

    /// <summary>
    /// 这张卡要不要显示。**有一间就显示** ——
    /// 只有一间时"选谁"确实没得选，但"这条会发到哪"仍然值得看一眼。
    /// </summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>卡片里那句说明：这一条到底会发到哪。</summary>
    public string TargetHintText
    {
        get
        {
            if (Targets.Count == 0)
            {
                return string.Empty;
            }

            var selected = Targets.Where(t => t.IsSelected).ToList();

            if (Targets.Count == 1)
            {
                var only = Targets[0];
                var where = only.IsCurrent ? "（当前连接）" : string.Empty;

                return $"这一条会发到「{only.Name}」{where}，走{only.SourceLabel}。";
            }

            if (selected.Count == 0)
            {
                return "还没勾任何班级 —— 勾一个才会发出去。";
            }

            var offline = selected.Where(t => !t.IsOnline).ToList();

            var baseText = selected.Count == 1
                ? $"这一条会发到「{selected[0].Name}」（{selected[0].SourceLabel}）。"
                : "勾选多个班级时，这一条会同时发到每一间："
                  + string.Join("、", selected.Select(t => $"{t.Name}（{t.SourceLabel}）")) + "。";

            // 离线的那些要单独说一句：不说的话，老师会以为"发出去了 = 教室里响了"，
            // 而实际上那间教室的电脑可能根本没开。
            return offline.Count == 0
                ? baseText
                : baseText + $"其中 {string.Join("、", offline.Select(t => t.Name))} 当前离线，多半收不到。";
        }
    }

    /// <summary>当前这一条会发给哪几间。</summary>
    public string TargetSummaryText
    {
        get
        {
            var selected = Targets.Where(t => t.IsSelected).ToList();

            if (Targets.Count == 0)
            {
                return "未连接教室";
            }

            return selected.Count switch
            {
                0 => "还没勾选班级",
                1 => selected[0].Name,
                _ => $"{selected.Count} 个班级：{string.Join("、", selected.Select(t => t.Name))}",
            };
        }
    }

    /// <summary>
    /// 用最新的「可发送班级」刷新勾选列表（局域网连着的那间与服务器绑定的班级在这里合成一张表）。
    ///
    /// 保留原来的勾选：老师勾了两个班、又去设备页切了个教室回来，
    /// 不该发现自己的勾选被清空了。第一次填充时默认勾**局域网直连**的那间（最直接），
    /// 没有就勾当前绑定的那间。
    /// </summary>
    public void SyncTargets(IReadOnlyList<ShoutTargetInfo> infos)
    {
        var previous = Targets
            .Where(t => t.IsSelected)
            .Select(t => t.Uuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isFirstFill = Targets.Count == 0;

        Targets.Clear();

        foreach (var info in infos)
        {
            var selected = isFirstFill
                ? info.Source == ShoutTargetSources.Lan || info.IsCurrent
                : previous.Contains(info.Uuid);

            var item = new ShoutTargetItem(info, selected) { IsCurrent = info.IsCurrent };

            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShoutTargetItem.IsSelected))
                {
                    OnPropertyChanged(nameof(TargetSummaryText));
                    OnPropertyChanged(nameof(TargetHintText));
                }
            };

            Targets.Add(item);
        }

        // 一个都没勾上时兜个底：发送按钮不该因为"没勾任何班"而变成什么都不做。
        if (Targets.Count > 0 && Targets.All(t => !t.IsSelected))
        {
            var fallback = Targets.FirstOrDefault(t => t.IsLan)
                           ?? Targets.FirstOrDefault(t => t.IsCurrent)
                           ?? Targets[0];

            fallback.IsSelected = true;
        }

        OnPropertyChanged(nameof(HasMultipleTargets));
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(TargetSummaryText));
        OnPropertyChanged(nameof(TargetHintText));
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

    private void ApplyPreset(string preset) => Text = preset;

    // ======================== 常用语（可自定义） ========================

    /// <summary>常用语：课堂上反复要说的话，点一下直接填入，避免每次手打。</summary>
    public ObservableCollection<TextPreset> Presets { get; } = [];

    /// <summary>是不是正在编辑常用语。编辑模式下每一行变成输入框，并出现增删按钮。</summary>
    [ObservableProperty]
    private bool _isEditingPresets;

    /// <summary>编辑过程中的一句回执（存好了 / 存不下 / 到上限了）。</summary>
    [ObservableProperty]
    private string _presetStatus = string.Empty;

    /// <summary>一条常用语都没有时，界面上得说一句，否则那一块就是空白。</summary>
    public bool HasNoPresets => Presets.Count == 0;

    /// <summary>
    /// 编辑区的说明。
    ///
    /// 例子放在这里而不是每一行的水位提示里：输入框的浮动标签会一直挂在内容上方，
    /// 一句长例子挂在每一行上会变成一片紫色小字，反而看不清自己写了什么。
    /// </summary>
    public string PresetLimitText =>
        $"一句话一条，例如「请翻到课本第 __ 页」。最多 {TeacherPhraseSettings.MaxCount} 条，"
        + $"每条 {TeacherPhraseSettings.MaxLength} 字以内。改完点「完成」保存。";

    /// <summary>按当前设置重建列表。第一次进页面、恢复默认、保存后对齐都用它。</summary>
    private void BuildPresets()
    {
        Presets.Clear();

        foreach (var phrase in _phraseSettings.Phrases)
        {
            Presets.Add(new TextPreset(phrase, ApplyPreset, RemovePreset));
        }

        OnPropertyChanged(nameof(HasNoPresets));
    }

    [RelayCommand]
    private void BeginEditPresets()
    {
        PresetStatus = string.Empty;
        IsEditingPresets = true;
    }

    [RelayCommand]
    private void FinishEditPresets()
    {
        // 存下去的是"规范化之后"的那一份，然后把界面也换成同一份 ——
        // 界面显示的和真正落盘的不该是两个样子（去重、截断都发生在这里）。
        var saved = PersistPresets(out var failure);
        IsEditingPresets = false;

        PresetStatus = saved
            ? Presets.Count == 0
                ? "已保存：常用语清空了。"
                : $"已保存 {Presets.Count} 条常用语。"
            : failure ?? "常用语没能存到本机。";
    }

    [RelayCommand]
    private void AddPreset()
    {
        if (Presets.Count >= TeacherPhraseSettings.MaxCount)
        {
            PresetStatus = $"最多 {TeacherPhraseSettings.MaxCount} 条 —— 先删掉几条再加。";
            return;
        }

        // 空行是"我还没填"，不是一条空常用语：保存时会被规范化丢掉，
        // 所以这里不拦，但也不假装它已经是一条。
        Presets.Insert(0, new TextPreset(string.Empty, ApplyPreset, RemovePreset));
        PresetStatus = "填好内容，点「完成」保存。";
        OnPropertyChanged(nameof(HasNoPresets));
    }

    [RelayCommand]
    private void RemovePreset(TextPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        Presets.Remove(preset);
        PresetStatus = "删掉了这一条，点「完成」保存。";
        OnPropertyChanged(nameof(HasNoPresets));
    }

    [RelayCommand]
    private void ResetPresets()
    {
        _phraseSettings = TeacherPhraseSettings.WithDefaults();
        BuildPresets();
        PresetStatus = $"已放回出厂的那 {Presets.Count} 条，点「完成」保存。";
    }

    /// <summary>
    /// 把界面上的内容写回设置并落盘。
    /// 返回是否真的存进了本机 —— 存不下要让老师知道，而不是下次打开发现白改了。
    /// </summary>
    private bool PersistPresets(out string? failure)
    {
        var normalized = new TeacherPhraseSettings
        {
            Phrases = [.. Presets.Select(p => p.Text)],
        }.Normalized();

        _phraseSettings = normalized;
        BuildPresets();

        if (LocalSettings.SavePhrases(normalized))
        {
            failure = null;
            return true;
        }

        failure = "常用语没能存到本机 —— 这一次的改动只在本次运行里有效。";
        return false;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(CharacterCount))]
    [NotifyPropertyChangedFor(nameof(CharacterCountText))]
    [NotifyPropertyChangedFor(nameof(SendBlockedHint))]
    [NotifyPropertyChangedFor(nameof(HasSendBlockedHint))]
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

    // ======================== 随图一起喊 ========================
    //
    // 图片和文字走的是同一条喊话、同一套展示参数：老师发一张"实验步骤"的照片
    // 配一句"照着这个做"，两样东西要一起出现在教室里，而不是先后两条。

    private PreparedImage? _preparedImage;

    /// <summary>这次要随图发出去的图片；为空表示纯文字喊话。</summary>
    public PreparedImage? PreparedImage
    {
        get => _preparedImage;
        private set
        {
            _preparedImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ImageSizeText));
            OnPropertyChanged(nameof(SendBlockedHint));
            OnPropertyChanged(nameof(HasSendBlockedHint));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>预览用的位图。界面直接绑它显示缩略图。</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _imagePreview;

    public bool HasImage => PreparedImage is not null;

    public string ImageSizeText => PreparedImage?.SizeText ?? string.Empty;

    /// <summary>
    /// 选好图片之后由界面调用（界面负责弹文件选择器，视图模型不碰 UI API）。
    /// 返回失败原因；成功返回 null。
    /// </summary>
    public async Task<string?> AttachImageAsync(Stream source)
    {
        PreparedImage? prepared;
        try
        {
            prepared = await ShoutImage.PrepareAsync(source).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"读不出这张图片：{ex.Message}";
        }

        if (prepared is null)
        {
            return "这个文件不是能识别的图片（支持 JPEG / PNG / WebP / GIF）。";
        }

        PreparedImage = prepared;

        // 预览用压缩后的字节重建：老师看到的就是教室里会看到的那一张，
        // 而不是"我选的图"和"发出去的图"长得不一样。
        var previous = ImagePreview;
        using (var stream = new MemoryStream(prepared.Bytes))
        {
            ImagePreview = new Avalonia.Media.Imaging.Bitmap(stream);
        }

        previous?.Dispose();
        return null;
    }

    [RelayCommand]
    private void ClearImage()
    {
        PreparedImage = null;
        ImagePreview?.Dispose();
        ImagePreview = null;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SendBlockedHint))]
    [NotifyPropertyChangedFor(nameof(HasSendBlockedHint))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SendBlockedHint))]
    [NotifyPropertyChangedFor(nameof(HasSendBlockedHint))]
    private bool _isSending;

    /// <summary>输入框里有多少字。界面上顺手提示"建议 60 字以内"。</summary>
    public int CharacterCount => Text.Length;

    public string CharacterCountText => $"{CharacterCount} 字 / 建议 60 字以内";

    public string RateText => Rate switch
    {
        < 0 => $"慢 {Math.Abs(Rate)}",
        > 0 => $"快 {Rate}",
        _ => "正常",
    };

    public string VolumeText => $"{Volume}%";

    /// <summary>
    /// 能不能发。带图时允许"没有文字"——一张照片本身就是内容，
    /// 说明文字是可选的那部分。
    /// </summary>
    public bool CanSend => IsConnected && !IsSending && (!string.IsNullOrWhiteSpace(Text) || HasImage);

    /// <summary>
    /// 发不出去的原因。
    ///
    /// 为什么要有这一行字：按钮灰着却不说话，是最容易被当成"坏了"的一种界面 ——
    /// 而这几个条件（没连教室 / 还没写内容 / 上一条还在发）本来都能一句话说清。
    /// </summary>
    public string SendBlockedHint
    {
        get
        {
            if (IsSending)
            {
                return "正在发送上一条…";
            }

            if (!IsConnected)
            {
                return "还没连上教室，发不出去：去「设备」页连一间；用局域网的话，确认教室那台电脑开着、和这台设备在同一个网络，"
                       + "并且第一次启动时在防火墙提示里勾了「专用网络」。";
            }

            if (string.IsNullOrWhiteSpace(Text) && !HasImage)
            {
                return "写一句要喊的话，或者选一张图片。";
            }

            return string.Empty;
        }
    }

    public bool HasSendBlockedHint => SendBlockedHint.Length > 0;

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
        var image = PreparedImage;

        // 带图的这一条：图片和文字是同一次喊话（同一条 display/hold 参数）。
        // 多班发送这一版先只支持纯文字 —— 图片多发意味着同一张图对着每个班各传一遍，
        // 流量是班级数的倍数，等真有老师这么用再加。
        if (image is not null)
        {
            var ok = await _channel.SendImageAsync(
                new ImageStartMessage
                {
                    Text = message.Text,
                    ContentType = image.ContentType,
                    Width = image.Width,
                    Height = image.Height,
                    Display = message.Display,
                    FontSize = message.FontSize,
                    HoldMs = message.HoldMs,
                    Speak = message.Speak,
                },
                image.Bytes,
                cancellationToken).ConfigureAwait(true);

            if (ok)
            {
                // 发出去了就把图从输入区撤掉：不然老师接着打下一句时，
                // 上一条的照片还挂在输入框上，一按发送又发了一遍。
                Avalonia.Threading.Dispatcher.UIThread.Post(ClearImage);
            }

            return ok;
        }

        var selected = Targets.Where(t => t.IsSelected).ToList();

        // 勾中的里面可能有"局域网直连的那间"（屏幕上正连着的那间）：它走的是手边这条
        // 直连链路，而其余几间要经服务器。两条路必须分开走 —— 局域网那间在服务器上
        // 未必有绑定记录，硬拿它去中继发送只会得到一句"无法绑定教室"。
        var overLan = selected.Where(t => t.IsLan).ToList();
        var overServer = selected.Where(t => !t.IsLan && t.Record is not null).ToList();

        // 只有一件要发、而且就是局域网那间（或没有多班发送器）时，保持原来的单条链路：
        // 这条路上有局域网直连优先的逻辑。
        if (Broadcaster is null || (selected.Count <= 1 && overServer.Count == 0))
        {
            return await _channel.SendTextAsync(message, cancellationToken).ConfigureAwait(true);
        }

        try
        {
            var sent = 0;
            var failed = new List<string>();

            if (overLan.Count > 0)
            {
                if (await _channel.SendTextAsync(message, cancellationToken).ConfigureAwait(true))
                {
                    sent++;
                }
                else
                {
                    failed.Add(overLan[0].Name);
                }
            }

            if (overServer.Count > 0)
            {
                var results = await Broadcaster
                    .SendTextAsync(
                        overServer.Select(t => t.Record!).ToList(),
                        message,
                        target => NameFor(target.Uuid),
                        cancellationToken)
                    .ConfigureAwait(true);

                sent += results.Count(r => r.Ok);
                failed.AddRange(results.Where(r => !r.Ok).Select(r => r.Classroom.Name));
            }

            var summary = failed.Count == 0
                ? $"已发给 {sent} 个班级。"
                : $"已发给 {sent} 个班级，{failed.Count} 个没送到：{string.Join("、", failed)}。";

            BroadcastFinished?.Invoke(summary);

            // 全都失败了才算这条没发出去（历史记录只记真的送到的）
            return sent > 0;
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
