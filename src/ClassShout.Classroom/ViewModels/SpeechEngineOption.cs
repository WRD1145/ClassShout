using ClassShout.Core.Audio;

namespace ClassShout.Classroom.ViewModels;

/// <summary>
/// 引擎下拉里的一项。
///
/// 用一个带 Label 的记录而不是直接绑枚举：枚举名（System / Edge）不是给用户看的，
/// 而在 XAML 里写"枚举到显示串"的转换器又多一层间接。直接把"值 + 显示串"绑上去最简单。
/// </summary>
/// <param name="Value">引擎。</param>
/// <param name="Label">界面上的显示串。</param>
public sealed record SpeechEngineOption(SpeechEngine Value, string Label);