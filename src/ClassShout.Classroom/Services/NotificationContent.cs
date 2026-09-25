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
}
