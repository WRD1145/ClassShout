namespace ClassShout.Core.Remote;

/// <summary>
/// 外观设置（个性化）。
///
/// 单独一个文件，不和教室 / 教师的联网配置混在一起：外观是纯粹的本机偏好，
/// 与身份、凭据、服务器地址的生命周期完全不同 —— 换台机器、清空登录状态
/// 都不该把老师挑好的配色一起带走或一起弄丢。
/// </summary>
public sealed class AppearanceSettings
{
    /// <summary>
    /// 主题种子色，形如 #RRGGBB。
    /// 为空表示使用内置基线配色（等同于"没有自定义过"）。
    /// </summary>
    public string? SeedColor { get; set; }

    /// <summary>是否做过自定义。界面据此决定选中哪张色卡。</summary>
    public bool IsCustomized => !string.IsNullOrWhiteSpace(SeedColor);
}
