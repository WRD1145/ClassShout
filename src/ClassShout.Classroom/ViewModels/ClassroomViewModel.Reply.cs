using Avalonia.Threading;
using ClassShout.Classroom.Services;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Classroom.ViewModels;

/// <summary>
/// 教室端的「回复」这一块。
///
/// 单独一个 partial 文件：它要用的私有状态（_server / _relay / AddLog / Post）都在同一个
/// 类里，但把它塞进那两千多行的主文件之后，"回复"这件事就会被埋掉。
///
/// 谁在回：教室里的人（学生、课代表、下节课的老师）。所以界面上是四样东西 ——
/// 「知道了」一颗按钮、预置的几句快捷回复、一句现打的文字、一小段语音。
/// 怎么回由**教室端**决定（有没有麦克风、这个班习惯怎么答），见 <see cref="ClassroomReplySettings"/>。
///
/// 回到哪里去：局域网那条走当前连着的教师端会话（同一条 TCP，反向发），
/// 中继那条走服务器的 /reply 端点 —— 两条都由 <see cref="SendReplyAsync"/> 统一分派，
/// 界面不必知道现在是哪条链路。
/// </summary>
public partial class ClassroomViewModel
{
    /// <summary>教室端的回复设置（本机文件 classroom-reply.json）。</summary>
    private ClassroomReplySettings _replySettings = LocalSettings.LoadReply().Normalized();

    private ClassroomMicRecorder? _replyRecorder;
    private DateTimeOffset _replyRecordStartedAt;
    private DispatcherTimer? _replyTimer;

    // ======================== 设置项（设置页直接绑） ========================

    public bool ReplyEnabled
    {
        get => _replySettings.Enabled;
        set => SetReplySetting(() => _replySettings.Enabled = value);
    }

    public bool ReplyShowAcknowledge
    {
        get => _replySettings.ShowAcknowledge;
        set => SetReplySetting(() => _replySettings.ShowAcknowledge = value);
    }

    public bool ReplyAllowText
    {
        get => _replySettings.AllowText;
        set => SetReplySetting(() => _replySettings.AllowText = value);
    }

    public bool ReplyAllowVoice
    {
        get => _replySettings.AllowVoice;
        set => SetReplySetting(() => _replySettings.AllowVoice = value);
    }

    /// <summary>本机有没有麦克风。没有时"允许语音回复"这个开关应当关着并说明原因。</summary>
    public bool HasReplyMicrophone => ClassroomMicRecorder.IsAvailable;

    public string ReplyMicHint => HasReplyMicrophone
        ? "本机有可用的麦克风。"
        : "本机没有可用的麦克风：语音回复在这个教室用不了（Windows 需要录音设备，Linux 需要安装 alsa-utils 的 arecord）。";

    /// <summary>快捷回复按钮（设置里那几条）。</summary>
    public IReadOnlyList<QuickReply> ReplyQuickReplies => _replySettings.QuickReplies;

    public bool HasQuickReplies => _replySettings.QuickReplies.Count > 0;

    /// <summary>设置里那几条快捷回复的摘要（设置页的一行行文字）。</summary>
    public IReadOnlyList<QuickReplyRow> ReplyQuickReplyRows =>
        _replySettings.QuickReplies.Select(reply => new QuickReplyRow(reply)).ToList();

    private void SetReplySetting(Action apply)    {
        apply();

        _replySettings = _replySettings.Normalized();
        LocalSettings.SaveReply(_replySettings);

        OnPropertyChanged(nameof(ReplyEnabled));
        OnPropertyChanged(nameof(ReplyShowAcknowledge));
        OnPropertyChanged(nameof(ReplyAllowText));
        OnPropertyChanged(nameof(ReplyAllowVoice));
        OnPropertyChanged(nameof(ReplyQuickReplies));
        OnPropertyChanged(nameof(ReplyQuickReplyRows));
        OnPropertyChanged(nameof(HasQuickReplies));
        OnPropertyChanged(nameof(ReplyHint));

        RefreshReplyAvailability();
    }

    /// <summary>语音回复的时长上限（秒）。</summary>
    public int ReplyVoiceMaxSeconds
    {
        get => _replySettings.VoiceMaxSeconds;
        set
        {
            var clamped = Math.Clamp(value <= 0 ? 15 : value, 3, 60);

            if (clamped == _replySettings.VoiceMaxSeconds)
            {
                return;
            }

            _replySettings.VoiceMaxSeconds = clamped;
            LocalSettings.SaveReply(_replySettings);
            OnPropertyChanged();
        }
    }

    // ======================== 设置页：增删快捷回复 ========================

    private string _newQuickReplyLabel = string.Empty;
    private string _newQuickReplyText = string.Empty;

    /// <summary>新快捷回复的按钮文字（留空时用内容兜底）。</summary>
    public string NewQuickReplyLabel
    {
        get => _newQuickReplyLabel;
        set => SetProperty(ref _newQuickReplyLabel, value);
    }

    /// <summary>新快捷回复的内容。</summary>
    public string NewQuickReplyText
    {
        get => _newQuickReplyText;
        set => SetProperty(ref _newQuickReplyText, value);
    }

    [RelayCommand]
    private void AddQuickReply()
    {
        var text = NewQuickReplyText.Trim();

        if (text.Length == 0)
        {
            AddLog("回复", "快捷回复的内容不能为空。");
            return;
        }

        if (_replySettings.QuickReplies.Count >= ClassroomReplySettings.MaxQuickReplies)
        {
            AddLog("回复", $"快捷回复最多 {ClassroomReplySettings.MaxQuickReplies} 个：再多教室里那块屏就摆不下，也点不准。");
            return;
        }

        var label = NewQuickReplyLabel.Trim();
        _replySettings.QuickReplies.Add(QuickReply.Of(label.Length > 0 ? label : text, text));

        _replySettings.Normalized();
        LocalSettings.SaveReply(_replySettings);

        NewQuickReplyLabel = string.Empty;
        NewQuickReplyText = string.Empty;

        RefreshQuickReplies();
    }

    [RelayCommand]
    private void RemoveQuickReply(QuickReply? reply)
    {
        if (reply is null)
        {
            return;
        }

        _replySettings.QuickReplies.RemoveAll(item => item.Id == reply.Id);
        LocalSettings.SaveReply(_replySettings);

        RefreshQuickReplies();
    }

    /// <summary>恢复默认的几条（删光了之后想找回来时用）。</summary>
    [RelayCommand]
    private void ResetQuickReplies()
    {
        _replySettings.QuickReplies = ClassroomReplySettings.WithDefaults().QuickReplies;
        LocalSettings.SaveReply(_replySettings);

        RefreshQuickReplies();
    }

    private void RefreshQuickReplies()
    {
        OnPropertyChanged(nameof(ReplyQuickReplies));
        OnPropertyChanged(nameof(ReplyQuickReplyRows));
        OnPropertyChanged(nameof(HasQuickReplies));
        OnPropertyChanged(nameof(ShowQuickReplies));
        OnPropertyChanged(nameof(HasReplyPanel));
    }

    // ======================== 对哪一条回复 ========================

    private string _replyToId = string.Empty;
    private string _replySource = string.Empty;
    private string _replyOriginal = string.Empty;

    /// <summary>
    /// 屏上正显示的那条消息的 ID（= 它的发送时刻）。
    ///
    /// 与 <see cref="_replyToId"/> 的区别：这个由各路消息（文字/语音/图片）在呈现时写，
    /// 那个是"真正记下来用于回复的那一条"。语音喊话没有正文，所以两者不能混用。
    /// </summary>
    private string CurrentMessageId { get; set; } = string.Empty;

    /// <summary>最近一条可以回复的喊话（教室里那块屏上正显示的就是它）。</summary>
    [ObservableProperty]
    private string _replyTargetText = string.Empty;

    /// <summary>有没有"可以回复的那一条"。没有时整块回复区不显示。</summary>
    [ObservableProperty]
    private bool _hasReplyTarget;

    /// <summary>
    /// 记下"现在可以回哪一条"。
    ///
    /// 每条喊话都带着自己的 ID（= 发送时刻），回复时原样带回去 ——
    /// 老师那边才知道这条"知道了"是针对哪一句话的。
    /// </summary>
    private void RememberReplyTarget(string id, string source, string text)
    {
        _replyToId = id;
        _replySource = source;
        _replyOriginal = text;

        var time = MessageIds.TimeText(id);

        ReplyTargetText = time.Length > 0
            ? $"{source}（{time}）：{text}"
            : $"{source}：{text}";

        HasReplyTarget = _replySettings.Enabled;
        RefreshReplyAvailability();
    }

    /// <summary>
    /// 只给渲染校验用：把"现在可以回哪一条"直接摆好。
    ///
    /// 与真实路径共用同一段逻辑（<see cref="RememberReplyTarget"/>），只是把它公开出来 ——
    /// 预览工具在另一个程序集里，除此之外没有别的办法让回复条出现在导出的图上，
    /// 而"藏起来的新界面有没有排歪"恰恰只能靠出图看。
    /// </summary>
    public void RememberReplyForPreview(string id, string source, string text)
        => RememberReplyTarget(id, source, text);

    /// <summary>回复区的按钮该不该出现。静音时仍然可以回（静音只是不朗读）。</summary>
    public bool ShowAcknowledgeButton => _replySettings.Enabled && _replySettings.ShowAcknowledge && HasReplyTarget;

    public bool ShowTextReply => _replySettings.Enabled && _replySettings.AllowText && HasReplyTarget;

    public bool ShowVoiceReply => _replySettings.Enabled && _replySettings.AllowVoice
                                  && HasReplyTarget && HasReplyMicrophone;

    public bool ShowQuickReplies => _replySettings.Enabled && HasReplyTarget && HasQuickReplies;

    private void RefreshReplyAvailability()
    {
        OnPropertyChanged(nameof(ShowAcknowledgeButton));
        OnPropertyChanged(nameof(ShowTextReply));
        OnPropertyChanged(nameof(ShowVoiceReply));
        OnPropertyChanged(nameof(ShowQuickReplies));
        OnPropertyChanged(nameof(HasReplyPanel));
    }

    /// <summary>整块回复区要不要显示（一条可回复的都没有时整块藏掉）。</summary>
    public bool HasReplyPanel => _replySettings.Enabled && HasReplyTarget
                                 && (ShowAcknowledgeButton || ShowTextReply || ShowVoiceReply || ShowQuickReplies);

    /// <summary>一句话说明当前状态。</summary>
    public string ReplyHint
    {
        get
        {
            if (!_replySettings.Enabled)
            {
                return "回复功能已关闭。需要的话在「设置 → 回复」里打开。";
            }

            if (!HasReplyTarget)
            {
                return "还没有可回复的喊话：老师喊一条之后，这里会出现回复按钮。";
            }

            return LastReplyText.Length > 0
                ? LastReplyText
                : "回一句：点「知道了」，或者挑一句快捷回复。";
        }
    }

    // ======================== 文字回复 ========================

    private string _replyText = string.Empty;

    /// <summary>文字回复的输入框。</summary>
    public string ReplyText
    {
        get => _replyText;
        set
        {
            if (SetProperty(ref _replyText, value))
            {
                SendTextReplyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanSendTextReply => ShowTextReply && !string.IsNullOrWhiteSpace(ReplyText);

    [RelayCommand(CanExecute = nameof(CanSendTextReply))]
    private async Task SendTextReplyAsync()
    {
        var text = ReplyText.Trim();

        if (text.Length == 0)
        {
            return;
        }

        ReplyText = string.Empty;

        await SendReplyAsync(new ReplyMessage
        {
            Kind = ReplyKinds.Text,
            Text = text,
        }).ConfigureAwait(true);
    }

    // ======================== 知道了 / 快捷回复 ========================

    [RelayCommand]
    private Task AcknowledgeAsync()
        => SendReplyAsync(new ReplyMessage
        {
            Kind = ReplyKinds.Acknowledge,
            Text = ReplyComposer.ComposeAcknowledge(_replySettings, ReplyContextFor()),
        });

    [RelayCommand]
    private Task SendQuickReplyAsync(QuickReply? reply)
    {
        if (reply is null)
        {
            return Task.CompletedTask;
        }

        return SendReplyAsync(new ReplyMessage
        {
            Kind = ReplyKinds.Quick,
            Text = ReplyComposer.Compose(reply, ReplyContextFor()),
        });
    }

    // ======================== 语音回复 ========================

    /// <summary>正在录一段语音回复。</summary>
    [ObservableProperty]
    private bool _isRecordingReply;

    /// <summary>已经录了多久（秒，界面上显示 mm:ss）。</summary>
    [ObservableProperty]
    private double _replyRecordSeconds;

    public string ReplyRecordText => TimeSpan.FromSeconds(ReplyRecordSeconds).ToString(@"mm\:ss");

    partial void OnReplyRecordSecondsChanged(double value) => OnPropertyChanged(nameof(ReplyRecordText));

    public string ReplyVoiceButtonText => IsRecordingReply ? "说完了，发送" : "录一段语音回复";

    partial void OnIsRecordingReplyChanged(bool value) => OnPropertyChanged(nameof(ReplyVoiceButtonText));

    [RelayCommand]
    private async Task ToggleVoiceReplyAsync()
    {
        if (IsRecordingReply)
        {
            await FinishVoiceReplyAsync(send: true).ConfigureAwait(true);
            return;
        }

        StartVoiceReply();
    }

    [RelayCommand]
    private async Task CancelVoiceReplyAsync()
        => await FinishVoiceReplyAsync(send: false).ConfigureAwait(true);

    private void StartVoiceReply()
    {
        if (!HasReplyMicrophone)
        {
            AddLog("回复", ReplyMicHint);
            return;
        }

        try
        {
            _replyRecorder = new ClassroomMicRecorder();
            _replyRecorder.Start();

            _replyRecordStartedAt = DateTimeOffset.Now;
            ReplyRecordSeconds = 0;
            IsRecordingReply = true;

            // 到时自动发出去：教室里那个人很可能点完就开始说话、说完忘了点停，
            // 而"一直在录"会让整块回复区停在那儿。
            _replyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _replyTimer.Tick += (_, _) =>
            {
                ReplyRecordSeconds = (DateTimeOffset.Now - _replyRecordStartedAt).TotalSeconds;

                if (ReplyRecordSeconds >= _replySettings.VoiceMaxSeconds)
                {
                    _ = FinishVoiceReplyAsync(send: true);
                }
            };
            _replyTimer.Start();

            AddLog("回复", $"开始录语音回复（最多 {_replySettings.VoiceMaxSeconds} 秒）。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            StopReplyTimer();
            IsRecordingReply = false;
            _replyRecorder?.Dispose();
            _replyRecorder = null;
            AddLog("回复", $"开不了麦克风：{ex.Message}");
        }
    }

    private async Task FinishVoiceReplyAsync(bool send)
    {
        StopReplyTimer();
        IsRecordingReply = false;

        var recorder = _replyRecorder;
        _replyRecorder = null;

        if (recorder is null)
        {
            return;
        }

        var recorded = recorder.Stop();
        recorder.Dispose();

        if (!send)
        {
            AddLog("回复", "已放弃这段语音回复。");
            return;
        }

        if (recorded is not { } clip)
        {
            AddLog("回复", "没有录到声音：检查一下麦克风是否被静音。");
            return;
        }

        await SendReplyAsync(new ReplyMessage
        {
            Kind = ReplyKinds.Voice,
            Text = ReplyComposer.VoiceSummary(clip.DurationMs, ReplyContextFor()),
            AudioWav = Convert.ToBase64String(clip.Wav),
            AudioDurationMs = clip.DurationMs,
        }).ConfigureAwait(true);
    }

    private void StopReplyTimer()
    {
        _replyTimer?.Stop();
        _replyTimer = null;
        ReplyRecordSeconds = 0;
    }

    // ======================== 发出去 ========================

    private string _lastReplyText = string.Empty;

    /// <summary>上一条回复的结果（"已回复：收到" / "没发出去"）。</summary>
    public string LastReplyText
    {
        get => _lastReplyText;
        private set
        {
            if (SetProperty(ref _lastReplyText, value))
            {
                OnPropertyChanged(nameof(ReplyHint));
            }
        }
    }

    private ReplyContext ReplyContextFor() => new(
        ClassroomName,
        _replySource,
        _replyOriginal,
        DateTimeOffset.Now);

    /// <summary>
    /// 把一条回复发出去。
    ///
    /// 两条链路都试一遍：局域网那条（当前连着的教师端会话）与中继那条（服务器转发）。
    /// 至少成功一路才算发出去 —— 教室里同时挂着"局域网直连"与"服务器绑定"是常态，
    /// 而老师可能只用了其中一条。
    /// </summary>
    private async Task SendReplyAsync(ReplyMessage reply)
    {
        if (!reply.ReplyToId.Equals(_replyToId, StringComparison.Ordinal))
        {
            reply.ReplyToId = _replyToId;
        }

        if (string.IsNullOrEmpty(reply.Id))
        {
            reply.Id = MessageIds.New();
        }

        reply.From = ClassroomName;

        var delivered = false;

        // 局域网：同一条连接反向发。因为教室端是服务端、教师端是客户端，
        // 这条消息走的是"服务端往已连接的客户端写"，与 ack/status 完全一样。
        foreach (var session in _server.Sessions)
        {
            if (await session.SendAsync(reply).ConfigureAwait(true))
            {
                delivered = true;
            }
        }

        // 中继：服务器转发给这间教室的老师。
        if (_relay is { IsConnected: true })
        {
            if (await _relay.SendReplyAsync(reply).ConfigureAwait(true))
            {
                delivered = true;
            }
        }

        var what = reply.Kind == ReplyKinds.Voice ? "语音回复" : reply.Text;

        LastReplyText = delivered
            ? $"已回复（{MessageIds.TimeText(reply.Id)}）：{what}"
            : $"这条回复没发出去：{what} —— 检查一下教师端是否还连着。";

        AddLog(delivered ? AppLogLevel.Info : AppLogLevel.Warning, "回复",
            $"{(delivered ? "已回复" : "回复未送出")}：{what}");
    }

    /// <summary>弹窗上的「知道了」：教室那块屏角上的卡片里点一下就算回了。</summary>
    private void AcknowledgeFromPopup() => _ = AcknowledgeAsync();

    /// <summary>设置页里"快捷回复"那一行的显示对象。</summary>
    public sealed class QuickReplyRow
    {
        public QuickReplyRow(QuickReply reply)
        {
            Reply = reply;
        }

        public QuickReply Reply { get; }

        public string Label => Reply.Label;

        public string Summary => Reply.Summary;
    }
}
