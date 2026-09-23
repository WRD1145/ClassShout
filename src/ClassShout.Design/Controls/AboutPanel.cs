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
    /// <summary>解锁需要连点多少次。</summary>
    private const int UnlockTapCount = 10;

    /// <summary>两次点击间隔超过它就重新计数。手滑点两下不该被算进那 10 次里。</summary>
    private static readonly TimeSpan TapWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>从第几次开始给出"还差几次"的提示：太早提示会让人误以为按错了。</summary>
    private const int HintAfterTaps = 5;

    private readonly TextBlock _versionText = new();
    private readonly TextBlock _buildText = new();
    private readonly TextBlock _tapHint = new() { IsVisible = false };
    private readonly StackPanel _developerSection = new() { Spacing = 10, IsVisible = false };

    private int _tapCount;
    private DateTimeOffset _lastTapAt = DateTimeOffset.MinValue;

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
        Build();
        ApplyDeveloperState(DeveloperMode.IsEnabled, raise: false);
    }

    private void Build()
    {
        _versionText.FontSize = 15;
        _versionText.Cursor = new Cursor(StandardCursorType.Hand);
        _versionText.Text = $"{AppName} {DeveloperMode.Version}";
        _versionText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Md3.OnSurface"));
        ToolTip.SetTip(_versionText, "连点 10 次可开启开发者模式");

        // 用 PointerPressed 而不是 Button：版本号必须看起来就是一行普通的文字，
        // 一旦做成按钮，它就变成了"一个功能"，而不是藏在关于里的入口。
        _versionText.PointerPressed += OnVersionPressed;

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

    private void OnVersionPressed(object? sender, PointerPressedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;

        // 间隔太久就重新计数：连点必须是"连"着点，隔天再点一下不该续上
        _tapCount = now - _lastTapAt > TapWindow ? 1 : _tapCount + 1;
        _lastTapAt = now;

        if (DeveloperMode.IsEnabled)
        {
            // 已解锁时再点，给一句明确的提示 —— 否则用户会以为"怎么点都没反应"，
            // 而这正是我们最不希望开发者模式给人的印象。
            ShowHint("开发者模式已开启。");
            return;
        }

        if (_tapCount >= UnlockTapCount)
        {
            _tapCount = 0;
            DeveloperMode.Enable();
            ApplyDeveloperState(true, raise: true);
            return;
        }

        if (_tapCount >= HintAfterTaps)
        {
            ShowHint($"再点 {UnlockTapCount - _tapCount} 次可开启开发者模式…");
        }
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
