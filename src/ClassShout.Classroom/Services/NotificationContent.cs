namespace ClassShout.Classroom.Services;

/// <summary>弹窗要显示的内容。</summary>
/// <param name="SourceName">发起者，通常是老师姓名。</param>
/// <param name="Text">喊话正文；语音喊话时给一句说明而不是转写文本（我们没有做语音识别）。</param>
/// <param name="IsVoice">是否为语音喊话，决定用哪个图标。</param>
public sealed record NotificationContent(string SourceName, string Text, bool IsVoice)
{
    public string TimeText { get; } = DateTime.Now.ToString("HH:mm");

    /// <summary>底部提示语。语音喊话时说明声音正在播，文字喊话时说明正在朗读。</summary>
    public string Hint => IsVoice ? "语音喊话正在教室播放" : "教室端正在朗读这条内容";
}
