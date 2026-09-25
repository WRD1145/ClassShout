using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassShout.Core.Remote;
using ClassShout.Design.Controls;
using ClassShout.Design.Theming;
using ClassShout.Teacher.ViewModels;

namespace ClassShout.Teacher.Views;

/// <summary>
/// 教师端主视图。
/// 桌面头把它装进手机比例的窗口，Android 头把它作为单视图根节点，
/// 两边跑的是同一份界面。
/// </summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        // 先对齐界面状态，再接回调 —— 反过来的话，初始化赋值会被当成"用户改了颜色"，
        // 白白触发一次保存。
        var theme = this.FindControl<ThemePicker>("Appearance");
        if (theme is not null)
        {
            theme.SelectedSeedHex = LocalSettings.LoadAppearance().SeedColor;
            theme.SelectionChanged += OnAppearanceChanged;
        }

        var about = this.FindControl<AboutPanel>("About");
        if (about is not null)
        {
            about.AppName = "ClassShout 教师端";
            about.DiagnosticsProvider = BuildDiagnostics;
        }
    }

    /// <summary>
    /// 「复制诊断信息」的文本。
    ///
    /// 只放排障时一定会被问到的几项，且**不放任何凭据**：
    /// 服务器地址与绑定教室是排障必需的，但登录令牌一概不进这里 ——
    /// 这份文本的用途是贴到别处给人看，写进去就等于泄露。
    /// </summary>
    private string BuildDiagnostics()
    {
        if (DataContext is not TeacherShellViewModel vm)
        {
            return "（界面尚未挂上视图模型）";
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine($"服务器地址 ：{(vm.IsServerConfigured ? vm.SavedServerUrl : "（未配置）")}");
        text.AppendLine($"账号       ：{(vm.IsSignedIn ? vm.SignedInName : "未登录")}");
        text.AppendLine($"绑定教室   ：{(vm.IsServerBound ? vm.ClassroomName : "未绑定")}");
        text.AppendLine($"服务器状态 ：{vm.ServerStatusText}");
        text.AppendLine($"链路       ：{vm.ActiveLinkText}");
        text.AppendLine($"配置目录   ：{LocalSettings.Directory}");
        return text.ToString();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 选一张图片随喊话发出去。
    ///
    /// 文件选择必须由视图来做：它要 TopLevel（也就是当前窗口），
    /// 而视图模型在 Android 与桌面两种宿主下都不该知道窗口从哪来。
    /// 视图只负责"让用户挑一个文件、把流交给视图模型"，压缩与发送都在下面那层。
    /// </summary>
    private async void OnPickImageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not TeacherShellViewModel vm)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选一张要发到教室的图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("图片")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif"],
                    MimeTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"],
                },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            var error = await vm.Text.AttachImageAsync(stream);

            if (error is not null)
            {
                vm.ShowSnackbar(error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 选中的文件读不了（权限、已被移走、云盘占位符）都只是"这次没选成"，
            // 不该把异常抛到 UI 线程上变成一次崩溃。
            vm.ShowSnackbar($"打开这张图片失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 换主题色：先存、再当场生效。
    ///
    /// 先存后应用：万一应用出问题，磁盘上的设置仍与界面选中的一致，
    /// 重启后不会出现"界面显示新颜色、实际用旧颜色"的错位。
    /// 这套顺序与教室端的设置窗口一致，两端的表现应当完全一样。
    /// </summary>
    private void OnAppearanceChanged(object? sender, string? seedHex)
    {
        var settings = LocalSettings.LoadAppearance();
        settings.SeedColor = seedHex;

        if (!LocalSettings.SaveAppearance(settings))
        {
            (sender as ThemePicker)?.ShowPersistenceFailure("外观设置没能写入本机，重启后会恢复原样。");
        }

        if (Application.Current is { } app && !Md3Appearance.Apply(app, seedHex))
        {
            // 控件已校验过一次，能走到这里说明两处判断不一致，属于代码缺陷，
            // 明确抛出来而不是伪装成"用户输入有误"。
            throw new InvalidOperationException(
                $"外观控件认定合法、但配色应用器拒绝了「{seedHex}」，两处校验逻辑不一致。");
        }
    }
}
