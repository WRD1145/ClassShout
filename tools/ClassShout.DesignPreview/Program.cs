using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassShout.Classroom.Services;
using ClassShout.Classroom.ViewModels;
using ClassShout.Design.Controls;
using ClassShout.Design.Theming;
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

    /// <summary>
    /// 内置字体的解析断言。
    ///
    /// 为什么必须断言、而不能"渲染一张图看一眼"：字体没命中时不会有任何报错，
    /// 只会静默回退到系统字体 —— 中文照样显示，只是不是我们想要的那个字体。
    ///
    /// 而 HarmonyOS Sans SC 这里有个具体的坑：Medium 字重的字体文件内部，
    /// legacy 家族名（nameID 1）写的是 "HarmonyOS Sans SC Medium"，自成一家；
    /// 真正的家族名只写在排印家族（nameID 16）= "HarmonyOS Sans SC" 里。
    /// 若字体管理器按 legacy 家族名分组，那么请求 "HarmonyOS Sans SC" + Medium
    /// 只能拿到同一家族里最接近的 Regular —— 强调字重就这么无声无息地没了。
    ///
    /// 所以这里直接问字体管理器：请求这一档字重，你实际给的是哪个家族的哪个字重。
    /// </summary>
    private static bool VerifyBundledFonts()
    {
        var manager = FontManager.Current;
        var passed = true;

        // 取值来源刻意是"一个真实窗口上的 FontFamily"，而不是自己拼一个字体链字符串：
        // Md3Theme.axaml 里 TopLevel 的 FontFamily 绑的就是 Md3.FontFamily 令牌，
        // 所以窗口上的值正是界面实际在用的那一个。
        // 这样 Typography.axaml 里 avares 路径打错、或有人把内置字体从链首挪走，
        // 都会在这里失败，而不是等打完包在真机上发现"中文好像有点不一样"。
        var probe = new Window { Width = 120, Height = 80 };

        // 判定对象是"子控件实际继承到的字体"，而不是窗口自己的属性值：
        // 界面里真正参与渲染的是子控件，而它的值来自继承链的终点。
        var sample = new TextBlock { Text = "同期", FontSize = 20 };
        probe.Content = sample;

        probe.Show();

        // 必须走完整的一轮布局再读：样式与 DynamicResource 是在布局阶段套上去的，
        // Show() 之后立刻读会拿到默认值（Segoe UI Variable Text），
        // 于是一条本来正常的字体链会被误判成"没生效"。
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var family = sample.FontFamily;

        Console.WriteLine("内置字体解析：");
        Console.WriteLine($"  窗口自身字体   = {probe.FontFamily}");
        Console.WriteLine($"  子控件实际字体 = {family}");
        Console.WriteLine($"    解析后的键   = {family.Key}（Source={family.Key?.Source}）");
        Console.WriteLine($"    家族名列表   = [{string.Join(" | ", family.FamilyNames)}]");
        Console.WriteLine($"  窗口底色       = {probe.Background}（同一条样式里的 Background，用来判断样式到底有没有套上）");

        // 定位用的旁证：这两个键在同一个 Styles.Resources 里，
        // 谁能查到、谁查不到，一眼就能看出是"资源整体不可达"还是"只有这一个键有问题"。
        foreach (var key in new[] { "Md3.FontFamily", "Md3.Weight.Emphasis" })
        {
            var found = probe.TryFindResource(key, out var value);
            Console.WriteLine($"    probe 查 {key,-22} → {(found ? $"'{value}'" : "（未找到）")}");
        }

        // 光看字符串不够：链首写错时 Avalonia 会静默落到后面某个系统字体上，
        // 中文照样显示，只是不是内置的那个。所以两个断言缺一不可。
        //
        // 还有一层保险：如果谁又把"逗号分隔的系统中文字体链"接在 avares 家族后面，
        // Avalonia 会把整串当成字体源（Key.Source 变成 compositefont:…），
        // 内置字体因此完全不生效 —— 这里对 Source 的比对正好能把那种写法挡下来。
        var source = family.Key?.Source?.ToString() ?? string.Empty;
        var leadsWithBundled =
            source.StartsWith(BundledFontSource, StringComparison.Ordinal) &&
            family.FamilyNames.Any(name => name.Contains("HarmonyOS", StringComparison.Ordinal));

        if (!leadsWithBundled)
        {
            Console.WriteLine($"  [失败] 界面实际用的不是内置字体：Source 应以 {BundledFontSource} 开头，家族名应含 HarmonyOS");
            passed = false;
        }

        foreach (var (weight, expected) in new[]
                 {
                     (FontWeight.Normal, FontWeight.Normal),
                     (FontWeight.Medium, FontWeight.Medium),
                 })
        {
            var typeface = new Typeface(family, FontStyle.Normal, weight);
            if (!manager.TryGetGlyphTypeface(typeface, out var glyphTypeface))
            {
                Console.WriteLine($"  [失败] {weight} —— 没有解析出任何字形");
                passed = false;
                continue;
            }

            var hasCjk = glyphTypeface.TryGetGlyph('同', out _);

            // 关键的一条：家族名里必须出现 HarmonyOS。
            // 内置字体没命中时，这里会变成 Microsoft YaHei UI 之类的系统字体 ——
            // 中文一样能显示，所以只有断言家族名才抓得住这种静默回退。
            var fromBundled = glyphTypeface.FamilyName.Contains("HarmonyOS", StringComparison.Ordinal);
            var weightOk = glyphTypeface.Weight == expected;
            var ok = hasCjk && fromBundled && weightOk;
            passed &= ok;

            Console.WriteLine(
                $"  [{(ok ? "通过" : "失败")}] 请求 {weight,-8} → 家族 '{glyphTypeface.FamilyName}' / " +
                $"实际字重 {glyphTypeface.Weight} / 中文字形={hasCjk} / 来自内置字体={fromBundled}");
        }

        probe.Close();
        Dispatcher.UIThread.RunJobs();

        Console.WriteLine();
        return passed;
    }

    /// <summary>内置字体所在的 avares 地址（Key 的前缀，不含家族名后缀）。</summary>
    private const string BundledFontSource = "avares://ClassShout.Design/Assets/Fonts";

    /// <summary>预览自定义配色时用的种子色（靛蓝）。与基线紫差别明显，一眼能看出换没换。</summary>
    private const string CustomSeed = "#3F51B5";

    /// <summary>取颜色里的 RGB，用于与渲染帧的像素值比对。</summary>
    private static uint ToRgb(Color color) => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    /// <summary>
    /// 置顶档位里那项"暂不可用"必须真的选不动。
    ///
    /// 为什么不靠看图：把文字调暗只是**看起来**灰，照样能点中 —— 而截图根本分辨不出
    /// "灰"和"禁用"这两种状态。所以直接查容器上的 IsEnabled，逐项与选项自身的
    /// IsAvailable 对齐：不可用的必须禁用，可用的必须不禁用（全禁用同样是 bug）。
    /// </summary>
    private static bool VerifyTopmostPicker()
    {
        Console.WriteLine("置顶档位可选项断言：");

        var window = new ClassroomSettings { DataContext = new ClassroomViewModel(), Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var picker = window.FindControl<ComboBox>("TopmostPicker");
        if (picker is null)
        {
            Console.WriteLine("  [失败] 设置窗口里没有名为 TopmostPicker 的下拉框");
            window.Close();
            Console.WriteLine();
            return false;
        }

        // 容器要等下拉展开、布局跑过一轮才被创建出来
        picker.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var passed = true;

        for (var i = 0; i < picker.ItemCount; i++)
        {
            if (picker.Items[i] is not TopmostOption option)
            {
                Console.WriteLine($"  [失败] 第 {i} 项不是 TopmostOption");
                passed = false;
                continue;
            }

            var container = picker.ContainerFromIndex(i) as ComboBoxItem;
            var actual = container?.IsEnabled;
            var ok = actual == option.IsAvailable;
            passed &= ok;

            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {option.Label,-24} " +
                              $"期望可选={option.IsAvailable} 容器实际={(actual is null ? "未创建" : actual.ToString())}");
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Console.WriteLine();
        return passed;
    }

    /// <summary>
    /// 配色断言。
    ///
    /// 检查的是"用户随便挑一个颜色都还行"这件事，而不是某一个颜色好不好看：
    /// 色角色是从种子的色相算出来的，最怕的就是某些色相下算出来的
    /// "深色底上的浅色字"对比度不够 —— 那种问题只在用户恰好选到那个颜色时才出现，
    /// 靠手工试是试不出来的。
    ///
    /// 对比度按 WCAG 的相对亮度比计算：MD3 规范保证 On* 与对应底色之间至少 4.5:1
    /// （音调差在 40 以上）。这里就照这个门槛验，任何一组掉下去都算失败。
    /// </summary>
    private static bool VerifyPalette()
    {
        // 覆盖色相环的各个方向，外加纯黑、纯白、纯红这些极端种子 ——
        // 极端值的彩度接近 0 或极高，最容易暴露映射写错。
        var seeds = new[]
        {
            Md3Palette.DefaultSeedHex,
            "#3F51B5", "#00696D", "#2E6B3E", "#8A5200", "#A03A32", "#9C3F6E", "#4A4A52",
            "#FFEB3B", "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF",
        };

        // 必须成对的角色：前景 vs 背景
        var pairs = new (string Foreground, string Background)[]
        {
            ("Md3.OnPrimary", "Md3.Primary"),
            ("Md3.OnPrimaryContainer", "Md3.PrimaryContainer"),
            ("Md3.OnSecondary", "Md3.Secondary"),
            ("Md3.OnSecondaryContainer", "Md3.SecondaryContainer"),
            ("Md3.OnTertiary", "Md3.Tertiary"),
            ("Md3.OnTertiaryContainer", "Md3.TertiaryContainer"),
            ("Md3.OnError", "Md3.Error"),
            ("Md3.OnErrorContainer", "Md3.ErrorContainer"),
            ("Md3.OnSurface", "Md3.Surface"),
            ("Md3.OnSurfaceVariant", "Md3.Surface"),
        };

        Console.WriteLine("配色断言（任意种子色下，成对色角色的对比度都必须达标）：");

        var passed = true;
        var worst = double.MaxValue;
        var worstAt = string.Empty;

        foreach (var hex in seeds)
        {
            Md3Palette.TryParseSeed(hex, out var seed);

            foreach (var dark in new[] { false, true })
            {
                var roles = Md3Palette.Build(seed, dark);

                foreach (var (fg, bg) in pairs)
                {
                    var ratio = Contrast(roles[fg], roles[bg]);
                    if (ratio < worst)
                    {
                        worst = ratio;
                        worstAt = $"{hex}/{(dark ? "暗" : "亮")}/{fg}";
                    }

                    if (ratio < 4.5)
                    {
                        Console.WriteLine($"  [失败] {hex} {(dark ? "暗" : "亮")} {fg} on {bg} = {ratio:F2}:1（应 ≥ 4.5）");
                        passed = false;
                    }
                }
            }
        }

        // 另外验一次"真的会覆盖到应用资源上"：光算出来正确而没接上，等于没做。
        var app = Application.Current!;
        Md3Appearance.Apply(app, "#3F51B5");
        var applied = app.TryFindResource("Md3.Primary", ThemeVariant.Light, out var value);
        var replaced = applied && value is ISolidColorBrush brush && brush.Color != Color.Parse("#6750A4");
        if (!replaced)
        {
            Console.WriteLine("  [失败] 自定义配色没有覆盖到应用资源（Md3.Primary 仍是基线值或取不到）");
            passed = false;
        }

        Md3Appearance.Apply(app, null);

        Console.WriteLine($"  [{(passed ? "通过" : "失败")}] {seeds.Length} 个种子 × 亮暗两套 × {pairs.Length} 对角色，"
                          + $"最低对比度 {worst:F2}:1（出现在 {worstAt}）");
        Console.WriteLine();

        return passed;
    }

    /// <summary>WCAG 相对亮度对比度。</summary>
    private static double Contrast(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

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

        // 字体先验：解析不到就该立刻失败，不必等渲染完再看图猜
        var fontsPassed = VerifyBundledFonts();

        // 配色先验：同样不必等到看图
        var palettePassed = VerifyPalette();

        // 交互状态的先验：截图看不出"置灰"与"禁用"的区别
        var pickerPassed = VerifyTopmostPicker();

        var scenes = new Scene[]
        {
            new("design-system",
                () => new Showcase(), 880, 1560,
                isDark => isDark ? Combine(CommonDark, GalleryExtraDark) : Combine(CommonLight, GalleryExtraLight)),

            new("teacher",
                () => new TeacherView { DataContext = new TeacherShellViewModel() }, 430, 900,
                isDark => isDark ? Combine(CommonDark, TeacherExtraDark) : Combine(CommonLight, TeacherExtraLight)),

            // 教师端文字页 + 排队提示。刻意真的往队列里塞两条卡住的喊话，
            // 好让"排队中"那块显示出来 —— {x:Static StringConverters.IsNotNullOrEmpty}
            // 这类转换器是**运行时**解析的，编译通过不代表能用，必须真渲染一次。
            new("teacher-text-queue",
                () =>
                {
                    var vm = new TeacherShellViewModel();
                    var blocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    vm.Text.Queue?.Enqueue("第一条（占位，用于让队列非空）", _ => blocker.Task);
                    vm.Text.Queue?.Enqueue("第二条（占位，用于让队列非空）", _ => blocker.Task);
                    vm.Text.RefreshQueueStatus();

                    return new TeacherView { DataContext = vm };
                }, 430, 900,
                _ => null),

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
                () => new ClassroomSettings { DataContext = new ClassroomViewModel(), Height = 2600 }, 0, 0,
                _ => null),

            // 进入设置前的 PIN 提示窗。同样只验能不能渲染出来。
            new("classroom-pin-prompt",
                () => new ClassroomPin(), 0, 0,
                _ => null),

            // 置顶档位的下拉展开状态。
            //
            // 必须单独来一张：折叠时只会渲染"当前选中项"，而那两项置灰的内容
            // 根本不在画面里 —— 也就是说"置灰"这件事没法从 classroom-settings 那张图看出来。
            // 这里把下拉打开，让「UIA 置顶（暂不可用）」那一项真的被画出来。
            new("classroom-topmost",
                () =>
                {
                    var window = new ClassroomSettings
                    {
                        DataContext = new ClassroomViewModel(),
                        Height = 820,
                    };

                    // 用 Post 而不是直接设：下拉需要窗口已经显示、Popup 宿主已就位才能展开。
                    // 主循环在 Show 之后会跑一轮 RunJobs，正好执行这里排队的工作。
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (window.FindControl<ComboBox>("TopmostPicker") is { } picker)
                        {
                            picker.IsDropDownOpen = true;
                        }
                    }, DispatcherPriority.Background);

                    return window;
                }, 0, 0,
                _ => null),

            // 字体回退诊断：用来排查真机上中文变方块的问题。
            // 这一页不做自动校验（Expectations 返回 null）——
            // 它整页近乎纯白，用"主色占比 < 85%"的判据会误报，
            // 判断方式是直接看图里有没有方块。
            new("font-diagnostics",
                () => new ClassShout.Teacher.Diagnostics.FontDiagnostics(), 1180, 460,
                _ => null),

            // 个性化（换主题色）的实际观感。
            //
            // 放在最后是有意的：这一场景会给应用换上一套自定义配色，
            // 上面那些场景的令牌判据用的是基线配色数值，先跑才不会被污染。
            //
            // 断言只能证明"资源确实被换掉了"，换完之后整页协不协调、有没有哪一块
            // 没跟上新配色，仍然只能靠看图 —— 所以这一页导出后要真的打开看。
            new("teacher-appearance",
                () =>
                {
                    Md3Appearance.Apply(Application.Current!, CustomSeed);

                    var vm = new TeacherShellViewModel();
                    vm.NavigateDevicesCommand.Execute(null);

                    var view = new TeacherView { DataContext = vm };

                    // 选中状态也要设上，导出的图才和真实使用一致：
                    // 真实应用里这一步由 MainView 用已保存的设置完成；
                    // 少了它，图上会是"已经换过色、却没有任何色卡被选中"的矛盾状态。
                    view.FindControl<ThemePicker>("Appearance")?.SelectedSeedHex = CustomSeed;

                    return view;
                }, 430, 1500,
                isDark =>
                {
                    // 判据直接由配色算法算出来，而不是抄一份写死：
                    // 这样"算出来的颜色"与"真正画到屏幕上的颜色"必须一致，
                    // 中间任何一环断了（资源没装上、DynamicResource 没重算）都会暴露。
                    Md3Palette.TryParseSeed(CustomSeed, out var seed);
                    var roles = Md3Palette.Build(seed, isDark);

                    return
                    [
                        ("Md3.Primary（自定义）", ToRgb(roles["Md3.Primary"])),
                        ("Md3.Surface（自定义）", ToRgb(roles["Md3.Surface"])),
                    ];
                }),
        };

        var allPassed = fontsPassed && palettePassed && pickerPassed;
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
