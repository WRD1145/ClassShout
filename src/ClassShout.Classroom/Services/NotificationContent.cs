namespace ClassShout.Classroom.Services;

using ClassShout.Core.Protocol;

/// <summary>弹窗要显示的内容。</summary>
/// <param name="SourceName">发起者，通常是老师姓名。</param>
/// <param name="Text">喊话正文；语音喊话时给一句说明而不是转写文本（我们没有做语音识别）。</param>
/// <param name="IsVoice">是否为语音喊话，决定用哪个图标。</param>
public sealed record NotificationContent(string SourceName, string Text, bool IsVoice)
{
    public string TimeText { get; } = DateTime.Now.ToString("HH:mm");

    /// <summary>正文的字号档位，取值见 <see cref="ShoutFontSizes"/>。</summary>
    public string FontSize { get; init; } = ShoutFontSizes.Default;

    /// <summary>
    /// 停留时长（毫秒）。<see cref="ShoutHoldDurations.Unspecified"/> 表示用教室端设置里的秒数，
    /// <see cref="ShoutHoldDurations.Forever"/> 表示不自动消失、要点掉才算。
    /// </summary>
    public int HoldMs { get; init; } = ShoutHoldDurations.Unspecified;

    /// <summary>
    /// 随这条提示一起显示的图片。为空表示纯文字提示。
    ///
    /// 用 Avalonia 的 Bitmap 而不是原始字节：字节在到达这里之前就已经解码过了
    /// （教室端收到图片时就解了一次），再解一遍等于白花一次 CPU。
    /// </summary>
    public Avalonia.Media.Imaging.Bitmap? Image { get; init; }

    /// <summary>这条提示带没带图片。</summary>
    public bool HasImage => Image is not null;

    /// <summary>正文的字号（像素）。
    ///
    /// 档位到像素的映射放在这里而不是视图里：弹窗与教室端大字区共用同一套档位，
    /// 各写各的迟早会出现"同一档在两处看着不一样大"。
    /// </summary>
    public double TextFontSize => ShoutFontSizes.ToPixels(FontSize, popup: true);

    /// <summary>底部提示语。语音喊话时说明声音正在播，文字喊话时说明正在朗读。</summary>
    public string Hint => IsVoice
        ? "语音喊话正在教室播放"
        : HasImage ? "图片喊话正在教室显示" : "教室端正在朗读这条内容";

    /// <summary>
    /// 弹窗上那颗「知道了」。
    ///
    /// 做成一个命令而不是回调：弹窗的 DataContext 就是这个对象，绑一个命令最省事，
    /// 界面里不必写"找父级视图模型"那类容易写错的绑定。
    /// 教室端没开回复、或者这条消息不该被回复时它是 null —— 按钮跟着不显示。
    /// </summary>
    public CommunityToolkit.Mvvm.Input.IRelayCommand? AcknowledgeCommand { get; init; }

    /// <summary>这条提示上要不要出现「知道了」。</summary>
    public bool CanAcknowledge => AcknowledgeCommand is not null;

    /// <summary>
    /// 回复区的主人（教室端视图模型）。
    ///
    /// 弹窗里的快捷回复与语音回复直接绑它的命令与状态，而不是在这里再抄一份：
    /// 录音状态、已经录了多久、上一条回复的结果都是**活的** —— 抄一份就等于两份状态，
    /// 而两份状态迟早会对不上（弹窗写着"正在录"，主窗口却停在"录一段"）。
    ///
    /// 为什么弹窗也要有回复区：发送方选了「只弹窗」时，教室里那块屏上只有这张卡片，
    /// 主窗口的回复栏等于被藏起来了 —— 那时教室里的人看得到喊话，却回不了一句。
    /// </summary>
    public ClassShout.Classroom.ViewModels.ClassroomViewModel? ReplyHost { get; init; }

    /// <summary>这张卡片上要不要出现回复区（知道了 / 快捷 / 语音）。</summary>
    public bool HasReplyControls => ReplyHost?.HasReplyPanel == true;
}
