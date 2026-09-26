using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using ClassShout.Core.Remote;

namespace ClassShout.Design.Controls;

/// <summary>
/// 「关于」区块：版本号、运行环境，以及藏在版本号后面的开发者模式。
///
/// 解锁方式是**在版本号上连点 10 次** —— 这个约定借自 ClassIsland
/// （它在「关于」里连点应用图标 10 次开启调试界面）。用同一个手势是有意的：
/// 会去翻开发者选项的人，多半也用过 ClassIsland，不必再学一套。
///
/// 做成设计系统里的共享控件，是因为两端都要有这一块，而"版本号怎么显示、
/// 点几次算解锁、解锁后显示什么"这类规则一旦各写一份，迟早会长歪。
/// 各端专属的开发者内容由调用方通过 <see cref="DeveloperContent"/> 塞进来。
/// </summary>
public sealed class AboutPanel : UserControl
{
    private readonly TextBlock _versionText = new();
    private readonly TextBlock _buildText = new();
    private readonly TextBlock _tapHint = new() { IsVisible = false };
    private readonly StackPanel _developerSection = new() { Spacing = 10, IsVisible = false };

    /// <summary>连点手势。规则本身在设计层里共用（见 <see cref="DeveloperTapGesture"/>）。</summary>
    private readonly DeveloperTapGesture _gesture;

    /// <summary>是否已经订阅了 <see cref="DeveloperMode.Changed"/>。</summary>
    private bool _subscribed;

    /// <summary>应用显示名，例如「ClassShout 教室端」。</summary>
    public string AppName { get; set; } = "ClassShout";

    /// <summary>本端专属的开发者内容（开发者模式开启后才显示）。</summary>
    public Control? DeveloperContent { get; set; }

    /// <summary>
    /// 「复制诊断信息」时取用的文本。由各端提供自己知道的那部分
    /// （链路状态、服务器地址、最近日志…），控件负责把它和版本信息拼在一起。
    /// </summary>
    public Func<string>? DiagnosticsProvider { get; set; }

    /// <summary>开发者模式状态发生变化（仅在实际切换时触发）。</summary>
    public event EventHandler<bool>? DeveloperModeChanged;

    public AboutPanel()
    {
        _gesture = new DeveloperTapGesture(ShowHint, () => ApplyDeveloperState(true, raise: true));

        Build();
        ApplyDeveloperState(DeveloperMode.IsEnabled, raise: false);
    }

    /// <summary>
    /// 解锁的入口不止这一处（「版本与更新」卡片上那行版本号也能连点），
    /// 所以这里必须跟着全局状态走：不然从别处解锁之后，这块内容要等
    /// 重新打开界面才出现 —— 看起来就像没解锁成功。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (!_subscribed)
        {
            DeveloperMode.Changed += OnDeveloperModeChanged;
            _subscribed = true;
        }

        ApplyDeveloperState(DeveloperMode.IsEnabled, raise: false);
    }

    /// <summary>
    /// 退订是必须的：事件是静态的，不退订的话每打开一次设置窗口
    /// 就留下一个再也不会被回收的 AboutPanel。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_subscribed)
        {
            DeveloperMode.Changed -= OnDeveloperModeChanged;
            _subscribed = false;
        }
    }

    private void OnDeveloperModeChanged() => Dispatcher.UIThread.Post(() => ApplyDeveloperState(true, raise: false));

    private void Build()
    {
        _versionText.FontSize = 15;
        _versionText.Text = $"{AppName} {DeveloperMode.Version}";
        _versionText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurface"));

        // 手势挂在版本号上，而版本号看起来就是一行普通的文字 ——
        // 做成按钮的话，它就变成了"一个功能"，而不是藏在关于里的入口。
        _gesture.Attach(_versionText);

        _buildText.FontSize = 11;
        _buildText.TextWrapping = TextWrapping.Wrap;
        _buildText.Text = DeveloperMode.BuildDescription;
        _buildText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurfaceVariant"));

        _tapHint.FontSize = 11;
        _tapHint.IsVisible = false;
        _tapHint.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.Primary"));

        var root = new StackPanel
        {
            Spacing = 6,
            Children = { _versionText, _buildText, _tapHint, _developerSection },
        };

        Content = root;
    }

    private void ShowHint(string text)
    {
        _tapHint.Text = text;
        _tapHint.IsVisible = true;

        // 提示是临时的：留在界面上会让人以为它是一个常驻状态
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (s, _) =>
        {
            _tapHint.IsVisible = false;
            ((DispatcherTimer)s!).Stop();
        };
        timer.Start();
    }

    private void ApplyDeveloperState(bool enabled, bool raise)
    {
        _developerSection.IsVisible = enabled;

        if (enabled && _developerSection.Children.Count == 0)
        {
            BuildDeveloperSection();
        }

        if (raise)
        {
            DeveloperModeChanged?.Invoke(this, enabled);
        }
    }

    private void BuildDeveloperSection()
    {
        var title = new TextBlock { Text = "开发者模式", FontSize = 13 };
        title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.Primary"));

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Text = "这些是为排障准备的，平时不必打开。关闭方式：删掉本机配置目录里的 developer.json。",
        };
        note.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurfaceVariant"));

        _developerSection.Children.Add(title);
        _developerSection.Children.Add(note);

        if (DeveloperContent is not null)
        {
            _developerSection.Children.Add(DeveloperContent);
        }

        _developerSection.Children.Add(BuildFontDiagnosticsButton());
        _developerSection.Children.Add(BuildConfigFolderRow());
        _developerSection.Children.Add(BuildCopyDiagnosticsButton());
    }

    /// <summary>
    /// 字体回退诊断。
    ///
    /// 这一页原先只能靠"改一个编译期常量、重新打包"才能进去看到 ——
    /// 而它要排查的问题（中文变方块）恰恰只在真机上出现，现场根本没有重打包的条件。
    /// 挪到开发者模式里之后，真机上连点版本号就能打开。
    /// </summary>
    private Control BuildFontDiagnosticsButton()
    {
        var button = new Button { Content = "字体回退诊断", FontSize = 12 };

        button.Click += (_, _) =>
        {
            var window = new DiagnosticsWindow("字体回退诊断", new Diagnostics.FontDiagnostics());

            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        };

        return button;
    }

    private Control BuildConfigFolderRow()
    {
        var path = new TextBlock
        {
            Text = LocalSettings.Directory,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        path.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurfaceVariant"));

        var open = new Button { Content = "打开配置目录", FontSize = 12 };
        open.Click += (_, _) => OpenConfigFolder();

        var row = new StackPanel { Spacing = 6, Children = { path, open } };
        return row;
    }

    private Control BuildCopyDiagnosticsButton()
    {
        var button = new Button { Content = "复制诊断信息", FontSize = 12 };

        button.Click += async (_, _) =>
        {
            var text = DiagnosticsProvider?.Invoke() ?? string.Empty;
            var payload = $"{AppName} {DeveloperMode.Version}{Environment.NewLine}"
                          + $"{DeveloperMode.BuildDescription}{Environment.NewLine}"
                          + $"{Environment.NewLine}{text}";

            // 剪贴板挂在 TopLevel 上，控件自己拿不到 —— 用 GetTopLevel 往上找。
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(payload).ConfigureAwait(true);
            }
        };

        return button;
    }

    /// <summary>
    /// 打开配置目录。排障时最常见的一步就是"去看看那个 json 里到底写了什么"，
    /// 让人自己去 %LOCALAPPDATA% 一层层翻是很烦的。
    /// </summary>
    private static void OpenConfigFolder()
    {
        try
        {
            var path = LocalSettings.Directory;

            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", path);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 打不开就算了：路径已经显示在界面上，手动复制过去也一样
        }
    }
}
