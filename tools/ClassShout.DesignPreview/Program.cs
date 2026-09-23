using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassShout.Classroom.ViewModels;
using ClassShout.Teacher.ViewModels;
using FluentAvalonia.Styling;
// 两个应用各有一个 MainWindow，这里用别名区分，避免类型名冲突
using ClassroomWindow = ClassShout.Classroom.Views.MainWindow;
using ClassroomSettings = ClassShout.Classroom.Views.SettingsWindow;
using ClassroomPin = ClassShout.Classroom.Views.PinPromptWindow;
using TeacherView = ClassShout.Teacher.Views.MainView;

namespace ClassShout.DesignPreview;

/// <summary>预览用应用：铺 Fluent 底座 + MD3 主题，与真实应用的组合方式完全一致。</summary>
internal sealed class PreviewApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = false });

        // 用 StyleInclude 而不是生成的强类型类：不依赖 XAML 编译器对文件名的推断，更稳。
        Styles.Add(new StyleInclude(new Uri("avares://ClassShout.Design/"))
        {
            Source = new Uri("avares://ClassShout.Design/Md3Theme.axaml"),
        });
    }
}

/// <summary>一个待渲染的画面。返回 Window 时直接用它自己的尺寸，否则按给定尺寸包一层窗口。</summary>
/// <param name="Name">输出文件名前缀。</param>
/// <param name="Create">创建界面。</param>
/// <param name="Width">包裹窗口宽度（内容本身是 Window 时忽略）。</param>
/// <param name="Height">包裹窗口高度（内容本身是 Window 时忽略）。</param>
/// <param name="Expectations">
/// 该画面的亮/暗主题期望令牌；返回 <c>null</c> 表示跳过自动校验，只导出图片供人眼判断。
/// </param>
internal sealed record Scene(
    string Name,
    Func<Control> Create,
    int Width,
    int Height,
    Func<bool, IReadOnlyList<(string Name, uint Rgb)>?> Expectations);

internal static class Program
{
    /// <summary>
    /// 每个画面都必须出现的令牌。
    /// 只放"任何一页都必然用到"的几个：主色、页面底色、正文色。
    /// 像 PrimaryContainer、Error 这类只在特定控件上出现的颜色，
    /// 不能作为通用判据 —— 教师端的文字页本来就不含 Error 色，
    /// 强行要求它出现只会制造误报。
    /// </summary>
    private static readonly (string Name, uint Rgb)[] CommonLight =
    [
        ("Md3.Primary", 0x6750A4),
        ("Md3.Surface", 0xFEF7FF),
        ("Md3.OnSurface", 0x1D1B20),
        ("Md3.OnSurfaceVariant", 0x49454F),
    ];

    private static readonly (string Name, uint Rgb)[] CommonDark =
    [
        ("Md3.Primary", 0xD0BCFF),
        ("Md3.Surface", 0x141218),
        ("Md3.OnSurface", 0xE6E0E9),
        ("Md3.OnSurfaceVariant", 0xCAC4D0),
    ];

    /// <summary>设计系统画廊覆盖全部组件，可以要求更多令牌。</summary>
    private static readonly (string Name, uint Rgb)[] GalleryExtraLight =
    [
        ("Md3.PrimaryContainer", 0xEADDFF),
        ("Md3.SecondaryContainer", 0xE8DEF8),
        ("Md3.TertiaryContainer", 0xFFD8E4),
        ("Md3.Error", 0xB3261E),
        ("Md3.SurfaceContainerHighest", 0xE6E0E9),
        ("Md3.InverseSurface", 0x322F35),
    ];

    private static readonly (string Name, uint Rgb)[] GalleryExtraDark =
    [
        ("Md3.PrimaryContainer", 0x4F378B),
        ("Md3.SecondaryContainer", 0x4A4458),
        ("Md3.TertiaryContainer", 0x633B48),
        ("Md3.Error", 0xF2B8B5),
        ("Md3.SurfaceContainerHighest", 0x36343B),
        ("Md3.InverseSurface", 0xE6E0E9),
    ];

    /// <summary>教室端待机页的主色大字区用 PrimaryContainer，主色容器必然可见。</summary>
    private static readonly (string Name, uint Rgb)[] ClassroomExtraLight =
    [
        ("Md3.PrimaryContainer", 0xEADDFF),
        ("Md3.SecondaryContainer", 0xE8DEF8),
    ];

    private static readonly (string Name, uint Rgb)[] ClassroomExtraDark =
    [
        ("Md3.PrimaryContainer", 0x4F378B),
        ("Md3.SecondaryContainer", 0x4A4458),
    ];

    /// <summary>教师端文字页有顶栏图标底色和导航栏选中胶囊。</summary>
    private static readonly (string Name, uint Rgb)[] TeacherExtraLight =
    [
        ("Md3.SecondaryContainer", 0xE8DEF8),
        ("Md3.SurfaceContainer", 0xF3EDF7),
    ];

    private static readonly (string Name, uint Rgb)[] TeacherExtraDark =
    [
        ("Md3.SecondaryContainer", 0x4A4458),
        ("Md3.SurfaceContainer", 0x211F26),
    ];

    private static IReadOnlyList<(string Name, uint Rgb)> Combine(
        IReadOnlyList<(string Name, uint Rgb)> baseline,
        IReadOnlyList<(string Name, uint Rgb)> extra)
        => [.. baseline, .. extra];

    [STAThread]
    public static int Main(string[] args)
    {
        var outputDirectory = args.Length > 0 ? args[0] : "artifacts";
        Directory.CreateDirectory(outputDirectory);

        AppBuilder.Configure<PreviewApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // 关键：关掉"无头假绘制"，改用 Skia 真渲染，否则截不到任何像素
                UseHeadlessDrawing = false,
            })
            .UseSkia()
            .SetupWithoutStarting();

        var scenes = new Scene[]
        {
            new("design-system",
                () => new Showcase(), 880, 1560,
                isDark => isDark ? Combine(CommonDark, GalleryExtraDark) : Combine(CommonLight, GalleryExtraLight)),

            new("teacher",
                () => new TeacherView { DataContext = new TeacherShellViewModel() }, 430, 900,
                isDark => isDark ? Combine(CommonDark, TeacherExtraDark) : Combine(CommonLight, TeacherExtraLight)),

            // 教师端「设备」页：服务器绑定、账号、以及语音转文字的密钥自填都在这里。
            // 单独一个场景是因为它和首页内容完全不同，而配置类界面最容易排版走样。
            new("teacher-devices",
                () =>
                {
                    var vm = new TeacherShellViewModel();
                    vm.NavigateDevicesCommand.Execute(null);
                    return new TeacherView { DataContext = vm };
                }, 430, 1400,
                _ => null),

            // 教室端窗口自带尺寸，这里传 0 表示用窗口自己的
            new("classroom",
                () => new ClassroomWindow { DataContext = new ClassroomViewModel() }, 0, 0,
                isDark => isDark ? Combine(CommonDark, ClassroomExtraDark) : Combine(CommonLight, ClassroomExtraLight)),

            // 教室端设置窗口。设置项从主界面搬出来之后，主界面只留大字区，
            // 所以必须单独渲染一次确认它自己站得住 ——
            // 不做像素判据（内容以文字与控件为主，颜色占比判据不适用），
            // 但"能构造、能布局、能出帧"本身就是有效断言：
            // XAML 结构写坏、绑定指向不存在的属性、控件主题缺失都会在这里炸。
            // 高度特意调到整页可见：设置项会随着功能增加而变多，
            // 若按窗口默认高度导出，导出图里永远只看得到头两张卡片，
            // 审阅的人会以为后面没做。这里让它一屏看全。
            new("classroom-settings",
                () => new ClassroomSettings { DataContext = new ClassroomViewModel(), Height = 1500 }, 0, 0,
                _ => null),

            // 进入设置前的 PIN 提示窗。同样只验能不能渲染出来。
            new("classroom-pin-prompt",
                () => new ClassroomPin(), 0, 0,
                _ => null),

            // 字体回退诊断：用来排查真机上中文变方块的问题。
            // 这一页不做自动校验（Expectations 返回 null）——
            // 它整页近乎纯白，用"主色占比 < 85%"的判据会误报，
            // 判断方式是直接看图里有没有方块。
            new("font-diagnostics",
                () => new ClassShout.Teacher.Diagnostics.FontDiagnostics(), 1180, 460,
                _ => null),
        };

        var allPassed = true;
        var written = new List<string>();

        foreach (var scene in scenes)
        {
            foreach (var (variant, variantName) in new[]
                     {
                         (ThemeVariant.Light, "light"),
                         (ThemeVariant.Dark, "dark"),
                     })
            {
                var isDark = variant == ThemeVariant.Dark;
                var (window, owned) = CreateWindow(scene, variant);

                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? frame = null;
                for (var attempt = 0; attempt < 5 && frame is null; attempt++)
                {
                    frame = window.CaptureRenderedFrame();
                    if (frame is null)
                    {
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();
                    }
                }

                if (frame is null)
                {
                    Console.Error.WriteLine($"[失败] {scene.Name} / {variantName} 未能捕获渲染帧。");
                    allPassed = false;
                    window.Close();
                    continue;
                }

                var path = Path.Combine(outputDirectory, $"{scene.Name}-{variantName}.png");
                frame.Save(path);
                written.Add(path);
                Console.WriteLine($"[导出] {path}");

                // Expectations 返回 null 表示这一页只导出图片、不做自动校验
                if (scene.Expectations(isDark) is { } expectations)
                {
                    allPassed &= FrameInspector.Report($"{scene.Name} / {variantName}", frame, expectations);
                }
                else
                {
                    Console.WriteLine($"  （{scene.Name} 跳过自动校验，请直接看图）");
                }

                window.Close();
                Dispatcher.UIThread.RunJobs();

                if (owned)
                {
                    (window.Content as IDisposable)?.Dispose();
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(allPassed
            ? $"结论：渲染校验通过，共导出 {written.Count} 张 PNG。"
            : "结论：渲染校验未通过，请检查上面的 [缺失] / 疑似空白 项。");

        return allPassed ? 0 : 1;
    }

    /// <summary>把画面装进窗口。返回的 bool 表示内容是否由本方法创建、需要负责释放。</summary>
    private static (Window Window, bool OwnsContent) CreateWindow(Scene scene, ThemeVariant variant)
    {
        var content = scene.Create();

        if (content is Window asWindow)
        {
            asWindow.RequestedThemeVariant = variant;
            return (asWindow, false);
        }

        var window = new Window
        {
            Width = scene.Width,
            Height = scene.Height,
            RequestedThemeVariant = variant,
            Content = content,
        };

        return (window, true);
    }
}
