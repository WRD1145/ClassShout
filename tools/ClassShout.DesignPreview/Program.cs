using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassShout.Classroom.Services;
using ClassShout.Core.Remote;
using ClassShout.Classroom.ViewModels;
using ClassShout.Design.Controls;
using ClassShout.Design.Theming;
using ClassShout.Teacher.Services;
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

    /// <summary>
    /// 教室端顶栏那个连接状态 chip。
    ///
    /// 锁的是"界面在说谎"这件事：它原本只数局域网 TCP 会话，
    /// 于是教室端正通过中继收着喊话、chip 上却写着"未连接"。
    /// 局域网那一路要有真实会话才能构造，这里验的是能构造出来的几种组合 ——
    /// 也就是被报出来的那一种（已连服务器却显示未连接）。
    /// </summary>
    private static bool VerifyClassroomLinkChip()
    {
        Console.WriteLine("教室端顶栏连接状态 chip：");

        var passed = true;

        void Check(string label, bool ok, string detail)
        {
            passed &= ok;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {label} —— {detail}");
        }

        var vm = new ClassroomViewModel();

        Check("什么都没有时显示未连接", vm.TeacherCountText == "未连接", vm.TeacherCountText);
        Check("什么都没有时引导语同时提到两条路",
            vm.IdleHint.Contains("中继服务器", StringComparison.Ordinal),
            vm.IdleHint);

        var notified = false;
        var hintNotified = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClassroomViewModel.TeacherCountText))
            {
                notified = true;
            }

            if (e.PropertyName == nameof(ClassroomViewModel.IdleHint))
            {
                hintNotified = true;
            }
        };

        vm.IsRelayConnected = true;

        Check("连上服务器后不再说未连接",
            vm.TeacherCountText == "已连服务器",
            vm.TeacherCountText);
        Check("状态变化会通知界面重画（否则要等下一次别的刷新才更新）", notified, $"收到通知={notified}");
        Check("引导语换成服务器口径，不再教同一局域网",
            !vm.IdleHint.Contains("同一局域网时", StringComparison.Ordinal) && hintNotified,
            $"{vm.IdleHint}（引导语收到通知={hintNotified}）");

        vm.IsRelayConnected = false;
        Check("断开后回到未连接", vm.TeacherCountText == "未连接", vm.TeacherCountText);

        Console.WriteLine();
        return passed;
    }

    /// <summary>
    /// 发图片前的处理：大图压到最长边 1600，小图原样放过。
    ///
    /// 这段逻辑错了的表现是"教室里的图糊了"或者"一张照片传了半分钟"，
    /// 两种情况都不会报错，只能靠断言与真图对照。
    /// </summary>
    private static bool VerifyImagePreparation()
    {
        Console.WriteLine("发送图片前的处理：");

        var passed = true;

        void Check(string label, bool ok, string detail)
        {
            passed &= ok;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {label} —— {detail}");
        }

        // 造一张"手机照片"级别的图：3000×2000，带花纹（纯色会被 JPEG 压得极小，
        // 那样"压缩后体积变小"这条断言就失去意义了）
        var big = RenderTestImage(3000, 2000);
        var bigResult = ShoutImage.PrepareAsync(new MemoryStream(big)).GetAwaiter().GetResult();

        Check("大图能被处理", bigResult is not null, bigResult?.SizeText ?? "处理失败");

        if (bigResult is { } prepared)
        {
            Check("大图按最长边等比缩到 1600",
                Math.Max(prepared.Width, prepared.Height) == ShoutImage.MaxEdge,
                $"{prepared.Width}×{prepared.Height}");

            // 断言的是"体积落在可发送的量级"，不是"一定比原图小"：
            // 原图可能是压得很好的 PNG，而 1600 宽的同一张图重新编码后未必更小。
            // 真正要保证的是别把一张几兆的图原样发出去。
            Check("处理后落在可直接发送的量级（≤ 1 MB）",
                prepared.Bytes.Length <= 1024 * 1024,
                $"原图 {big.Length / 1024} KB（3000×2000）→ 处理后 {prepared.Bytes.Length / 1024} KB（{prepared.Width}×{prepared.Height}）");

            Check("标记为已压缩", prepared.Compressed, $"Compressed={prepared.Compressed}");

            Check("按体积挑了更小的编码格式（照片用 JPEG、截图用 PNG）",
                prepared.ContentType is "image/jpeg" or "image/png",
                prepared.ContentType);

            // 压缩结果必须还能解码 —— 否则教室里什么都显示不出来，
            // 而发送端这边一路都显示"成功"
            var decodable = true;
            string decodeDetail = "解码成功";

            try
            {
                using var stream = new MemoryStream(prepared.Bytes);
                using var bitmap = new Bitmap(stream);
                decodable = bitmap.PixelSize.Width == prepared.Width;
                decodeDetail = $"{bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}";
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                decodable = false;
                decodeDetail = ex.Message;
            }

            Check("压缩结果能重新解码", decodable, decodeDetail);
        }

        // 小图：既不超尺寸也不超体积，应当原样放过（避免把截图重新编码一遍，文字会发糊）
        var small = RenderTestImage(120, 90);
        var smallResult = ShoutImage.PrepareAsync(new MemoryStream(small)).GetAwaiter().GetResult();

        Check("小图原样放过，不重新编码",
            smallResult is { Compressed: false } && smallResult.Bytes.AsSpan().SequenceEqual(small),
            smallResult is null ? "处理失败" : $"{smallResult.Width}×{smallResult.Height}，{smallResult.SizeText}");

        // 不是图片的文件要能被识别出来，而不是抛异常或当成一张空图发出去
        var bogus = ShoutImage.PrepareAsync(new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]))
            .GetAwaiter().GetResult();

        Check("认不出的文件返回失败而不是抛异常", bogus is null, bogus is null ? "返回了 null" : "居然处理成功了");

        Console.WriteLine();
        return passed;
    }

    /// <summary>画一张带花纹的测试图，返回 PNG 字节。</summary>
    private static byte[] RenderTestImage(int width, int height)
    {
        var target = new RenderTargetBitmap(new PixelSize(width, height));

        using (var context = target.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, width, height));

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x55, 0x99)), Math.Max(1, width / 150.0));
            var step = Math.Max(8, width / 30);

            for (var i = -height; i < width; i += step)
            {
                context.DrawLine(pen, new Point(i, 0), new Point(i + height, height));
            }
        }

        using var stream = new MemoryStream();
        target.Save(stream);
        target.Dispose();
        return stream.ToArray();
    }

    /// <summary>
    /// 「已保存的教室」列表：读进来、标出当前那间、移除时真的落盘。
    ///
    /// 这几件事都发生在视图模型里，跑不在场测不到 —— 而它们错了的表现还很隐蔽：
    /// 列表少一条、或者删掉之后重启又回来了，都要等老师下一次换班才发现。
    /// </summary>
    private static bool VerifySavedClassrooms()
    {
        Console.WriteLine("教师端「已保存的教室」：");

        var passed = true;

        void Check(string label, bool ok, string detail)
        {
            passed &= ok;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {label} —— {detail}");
        }

        var dataPath = Path.Combine(LocalSettings.Directory, "teacher.json");
        var hadFile = File.Exists(dataPath);
        var backup = hadFile ? File.ReadAllText(dataPath) : null;

        try
        {
            SeedSavedClassrooms();

            var vm = new TeacherShellViewModel();

            Check("已保存的教室会读进界面",
                vm.SavedClassrooms.Count == 3,
                $"共 {vm.SavedClassrooms.Count} 条");

            Check("有记录时列表可见", vm.HasSavedClassrooms, $"HasSavedClassrooms={vm.HasSavedClassrooms}");

            Check("最近绑定的排在最前",
                vm.SavedClassrooms.Count > 0 && vm.SavedClassrooms[0].Name == "三年二班",
                vm.SavedClassrooms.Count == 0 ? "列表是空的" : $"首条={vm.SavedClassrooms[0].Name}");

            Check("存了口令的教室标成「已存口令」",
                vm.SavedClassrooms.Any(item => item.HasSecret && item.DetailText.Contains("已存口令", StringComparison.Ordinal)),
                vm.SavedClassrooms.Count == 0 ? "无记录" : vm.SavedClassrooms[0].DetailText);

            Check("靠授权绑定的教室标成「管理员授权」",
                vm.SavedClassrooms.Any(item => !item.HasSecret && item.DetailText.Contains("管理员授权", StringComparison.Ordinal)),
                "有一条没有口令的记录");

            Check("没有绑定时没有任何一间被标成「使用中」",
                vm.SavedClassrooms.All(item => !item.IsCurrent),
                $"使用中 {vm.SavedClassrooms.Count(item => item.IsCurrent)} 条");

            // 移除：列表要少一条，而且文件里也要真的少一条 ——
            // 只改内存的话，重启之后被删掉的那间会自己回来。
            var target = vm.SavedClassrooms[1];
            target.RemoveCommand.Execute(null);

            Check("移除后列表里少一条",
                vm.SavedClassrooms.Count == 2 && vm.SavedClassrooms.All(item => item.Uuid != target.Uuid),
                $"共 {vm.SavedClassrooms.Count} 条");

            var onDisk = LocalSettings.LoadTeacher();
            Check("移除会落盘（否则重启后它又回来了）",
                onDisk.RecentClassrooms.All(item => item.Uuid != target.Uuid) && onDisk.RecentClassrooms.Count == 2,
                $"文件里 {onDisk.RecentClassrooms.Count} 条");

            Check("被移除的那间的口令也一起删了",
                onDisk.RecentClassrooms.All(item => item.Secret != "secret-of-三年三班"),
                "口令未残留在配置里");

            // 文字页的"发给谁"用的是同一批数据
            Check("文字页拿到的目标班级与保存列表一致",
                vm.Text.Targets.Count == onDisk.RecentClassrooms.Count,
                $"文字页 {vm.Text.Targets.Count} 个，列表里 {onDisk.RecentClassrooms.Count} 个");

            Check("多于一间时才会显示「发给谁」",
                vm.Text.HasMultipleTargets == vm.Text.Targets.Count > 1,
                $"{vm.Text.Targets.Count} 个目标，HasMultipleTargets={vm.Text.HasMultipleTargets}");

            Check("一个都没勾时兜底勾上一间（否则发送按钮等于什么都不做）",
                vm.Text.Targets.Count(t => t.IsSelected) >= 1,
                $"已勾 {vm.Text.Targets.Count(t => t.IsSelected)} 个");

            Check("摘要里列出了会发给哪几间",
                !string.IsNullOrWhiteSpace(vm.Text.TargetSummaryText),
                vm.Text.TargetSummaryText);
        }
        finally
        {
            if (hadFile)
            {
                File.WriteAllText(dataPath, backup!);
            }
            else
            {
                File.Delete(dataPath);
            }
        }

        Console.WriteLine();
        return passed;
    }

    /// <summary>
    /// 往临时数据目录里预置三条「已保存的教室」。
    ///
    /// 走的是真实配置文件而不是往视图模型里塞对象：这个功能的全部意义就是"存下来、
    /// 下次打开还在"，所以验证也必须经过那一次落盘。
    /// </summary>
    private static void SeedSavedClassrooms()
    {
        var stamp = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var settings = new TeacherRelaySettings { ServerUrl = "https://relay.example.com" };

        // 倒着放：Remember 会把最新的排到最前，所以「三年二班」要最后入列
        TeacherRelaySettings.Remember(settings.RecentClassrooms,
            new BoundClassroom("uuid-c", "三年四班", stamp.AddMinutes(-30), "https://relay.example.com", null));
        TeacherRelaySettings.Remember(settings.RecentClassrooms,
            new BoundClassroom("uuid-b", "三年三班", stamp.AddMinutes(-20), "https://relay.example.com", "secret-of-三年三班"));
        TeacherRelaySettings.Remember(settings.RecentClassrooms,
            new BoundClassroom("uuid-a", "三年二班", stamp.AddMinutes(-10), "https://relay.example.com", "secret-of-三年二班"));

        LocalSettings.SaveTeacher(settings);
    }

    /// <summary>
    /// 教师端文字页，并先把三条常用语种进本机设置。
    ///
    /// 种完立刻把文件还原 —— 视图模型在构造时已经把内容读进内存，
    /// 之后的渲染用的就是内存里那份，所以不必让渲染过程占着别人的真实设置文件。
    /// 两个常用语场景（平时 / 编辑）共用它，保证两张图只差"进没进编辑态"这一个变量。
    /// </summary>
    private static Control CreatePhrasePage(bool editing)
    {
        var path = Path.Combine(LocalSettings.Directory, "teacher-phrases.json");
        var had = File.Exists(path);
        var backup = had ? File.ReadAllText(path) : null;

        try
        {
            LocalSettings.SavePhrases(new TeacherPhraseSettings
            {
                Phrases = ["同学们请安静", "请翻到课本第 __ 页", "课代表把作业收上来"],
            });

            var vm = new TeacherShellViewModel();

            if (editing)
            {
                vm.Text.BeginEditPresetsCommand.Execute(null);

                // 新增一条空行：水位提示只有在空行上才看得到
                vm.Text.AddPresetCommand.Execute(null);
            }

            return new TeacherView { DataContext = vm };
        }
        finally
        {
            if (had && backup is not null)
            {
                File.WriteAllText(path, backup);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 教师端"服务器地址"这条配置的断言。
    ///
    /// 锁的是一个具体的死锁：服务器地址原本和「教室 UUID / 口令」挤在同一张卡里，
    /// 而把地址写进配置的唯一路径是「绑定教室」—— 绑定要求已登录，登录读的却正是
    /// 那个还没写进去的地址。新装的机器于是卡死：登录提示"请先填写服务器地址"
    /// （尽管地址框里刚填了），而能让地址生效的那个按钮永远点不亮。
    ///
    /// 断言覆盖"填地址"能独立完成、地址已规范化并落盘、以及未配置时登录会指出该去哪一步。
    /// 整个过程跑在临时数据目录里，不碰使用者真实的 teacher.json。
    /// </summary>
    private static bool VerifyServerAddressFlow()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cs-preview-serverdir");

        if (Directory.Exists(dataDir))
        {
            Directory.Delete(dataDir, recursive: true);
        }

        // 记下原值再改，跑完恢复 —— 不能直接置 null，
        // 否则后面的画面会退回读使用者真实的设置文件。
        var previousDataDir = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
        Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", dataDir);

        Console.WriteLine("教师端服务器地址断言：");

        var passed = true;

        void Check(string label, bool ok, string detail)
        {
            passed &= ok;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {label} —— {detail}");
        }

        try
        {
            var vm = new TeacherShellViewModel();

            Check("新装机器上默认未配置", !vm.IsServerConfigured, vm.ServerAddressStatus);

            // 空地址时按钮应当是禁用的（否则点下去只会得到一个错误提示）
            Check("地址为空时两个按钮都不可点",
                !vm.SaveServerAddressCommand.CanExecute(null) && !vm.TestServerCommand.CanExecute(null),
                $"保存={vm.SaveServerAddressCommand.CanExecute(null)} 测试={vm.TestServerCommand.CanExecute(null)}");

            // 未配置就去登录：必须指出"该去哪一步"，而不是一句含糊的失败
            vm.LoginAccount = "someone";
            vm.LoginPassword = "whatever";
            Pump(vm.LoginCommand.ExecuteAsync(null));
            Check("未配置时登录给出明确指引",
                vm.AccountError?.Contains("中继服务器", StringComparison.Ordinal) == true,
                vm.AccountError ?? "（没有错误信息）");

            // 只填主机名：应当被规范化后保存 —— 这正是"填地址"独立成一步的核心
            vm.ServerUrl = "relay.example.com";
            Pump(vm.SaveServerAddressCommand.ExecuteAsync(null));

            Check("保存后即视为已配置", vm.IsServerConfigured, vm.ServerAddressStatus);
            Check("地址被规范化（补 http:// 并去掉末尾斜杠）",
                vm.SavedServerUrl == "http://relay.example.com",
                vm.SavedServerUrl);

            // 关键：真的写进了磁盘，而不是只改内存 —— 否则重启后又回到死锁
            var saved = LocalSettings.LoadTeacher().ServerUrl;
            Check("已落盘", saved == "http://relay.example.com", saved ?? "（空）");

            vm.ServerUrl = "https://relay.wrd1145.dev/";
            Pump(vm.SaveServerAddressCommand.ExecuteAsync(null));
            Check("改地址后覆盖保存",
                LocalSettings.LoadTeacher().ServerUrl == "https://relay.wrd1145.dev",
                LocalSettings.LoadTeacher().ServerUrl ?? "（空）");

            // 地址非法时要拦住，且不能把上一次的正确值改坏
            vm.ServerUrl = "ftp://nope";
            Pump(vm.SaveServerAddressCommand.ExecuteAsync(null));
            Check("非法地址被拒且不影响已保存的值",
                vm.HasServerAddressError && LocalSettings.LoadTeacher().ServerUrl == "https://relay.wrd1145.dev",
                vm.ServerAddressError ?? "（没有错误信息）");

            // 走一遍"界面上真正会发生的事"：清空再逐字输入。
            //
            // 注意这里**不能**只查 CanExecute 的返回值。CanExecute() 是每次现算的，
            // 就算属性变更时从不发通知，它照样返回 true —— 而按钮的禁用态来自
            // CanExecuteChanged 事件：不发通知，按钮就一直停在灰的样子。
            // 用户报的"填了地址按钮还是灰的"正是后者。
            // 所以真正要验的是"改 ServerUrl 会不会让命令通知界面重新求值"。
            vm.ServerUrl = string.Empty;

            var saveNotified = false;
            var testNotified = false;
            vm.SaveServerAddressCommand.CanExecuteChanged += (_, _) => saveNotified = true;
            vm.TestServerCommand.CanExecuteChanged += (_, _) => testNotified = true;

            vm.ServerUrl = "10.0.0.5:8080";

            Check("输入地址会通知按钮重新求值（漏了这条，按钮就永远是灰的）",
                saveNotified && testNotified,
                $"保存={saveNotified} 测试={testNotified}");
            Check("此刻两个按钮确实可点",
                vm.SaveServerAddressCommand.CanExecute(null) && vm.TestServerCommand.CanExecute(null),
                $"保存={vm.SaveServerAddressCommand.CanExecute(null)} 测试={vm.TestServerCommand.CanExecute(null)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [失败] 断言过程抛异常：{ex.GetType().Name}: {ex.Message}");
            passed = false;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", previousDataDir);
            Directory.Delete(dataDir, recursive: true);
        }

        Console.WriteLine();
        return passed;
    }

    /// <summary>
    /// 等一个异步命令跑完，其间持续泵消息。
    ///
    /// 不能直接 .GetAwaiter().GetResult()：视图模型里用的是 ConfigureAwait(true)，
    /// 续体要回到 UI 线程，而阻塞等待的正是 UI 线程 —— 那样会直接死锁。
    /// </summary>
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();

        if (task.IsFaulted)
        {
            throw task.Exception!;
        }
    }

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

        // 把设置目录指到一个临时位置，让整轮预览从"全新安装"的状态出发。
        //
        // 不这样做的话，导出的图会取决于跑预览这台机器上残留的设置：本机若选过主题色、
        // 登录过某个测试账号，画面上就会出现"色卡选中了青、界面却还是基线紫"
        // 这种自相矛盾的状态（截图里真的见到过）——
        // 而预览的全部价值就是"图里看到的等于用户看到的"，它不该随开发机漂移。
        var previewDataDir = Path.Combine(Path.GetTempPath(), "cs-preview-data");

        if (Directory.Exists(previewDataDir))
        {
            Directory.Delete(previewDataDir, recursive: true);
        }

        Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", previewDataDir);

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

        // 教师端服务器地址这条配置的逻辑（原本存在一条死锁）
        var serverAddressPassed = VerifyServerAddressFlow();

        // 教室端顶栏 chip 的状态（原本在服务器链路下恒为"未连接"）
        var linkChipPassed = VerifyClassroomLinkChip();

        // 已保存的教室列表（读取、标记、移除落盘）—— 同样是视图模型里的逻辑
        var savedClassroomsPassed = VerifySavedClassrooms();

        // 发图片前的压缩：大图缩到 1600、小图原样放过
        var imagePrepPassed = VerifyImagePreparation();

        var scenes = new Scene[]
        {
            new("design-system",
                () => new Showcase(), 880, 1560,
                isDark => isDark ? Combine(CommonDark, GalleryExtraDark) : Combine(CommonLight, GalleryExtraLight)),

            new("teacher",
                () => new TeacherView { DataContext = new TeacherShellViewModel() }, 430, 900,
                isDark => isDark ? Combine(CommonDark, TeacherExtraDark) : Combine(CommonLight, TeacherExtraLight)),

            // 教师端「设备」页里的「已保存的教室」：三间，其中一间标成"使用中"。
            //
            // 刻意种了数据再构造：空列表时那一段整个不可见（IsVisible 绑在数量上），
            // 而"使用中"徽标、副标题、移除按钮都只在有条目时才画得出来 ——
            // 折叠状态下这一块等于没渲染过。
            new("teacher-classrooms",
                () =>
                {
                    var dataPath = Path.Combine(LocalSettings.Directory, "teacher.json");
                    var hadFile = File.Exists(dataPath);
                    var backup = hadFile ? File.ReadAllText(dataPath) : null;

                    try
                    {
                        SeedSavedClassrooms();

                        var vm = new TeacherShellViewModel();
                        vm.NavigateDevicesCommand.Execute(null);

                        // 让列表里有一间是"当前正在用的"，好把那个徽标也画出来。
                        // 这里只改界面状态、不去真的连服务器 —— 渲染校验不该依赖网络。
                        if (vm.SavedClassrooms.Count > 0)
                        {
                            vm.SavedClassrooms[0].IsCurrent = true;
                        }

                        return new TeacherView { DataContext = vm };
                    }
                    finally
                    {
                        if (hadFile)
                        {
                            File.WriteAllText(dataPath, backup!);
                        }
                        else
                        {
                            File.Delete(dataPath);
                        }
                    }
                }, 430, 1900,
                _ => null),

            // 教师端文字页的「这条发给谁」：保存了三间教室时才会出现的那一块。
            //
            // 它是条件显示的（只有一间时整块不画），所以默认那张 teacher 图里根本看不到它 ——
            // 而勾选框、当前绑定徽标、摘要那一行都只在有条目时才画得出来。
            new("teacher-targets",
                () =>
                {
                    var dataPath = Path.Combine(LocalSettings.Directory, "teacher.json");
                    var hadFile = File.Exists(dataPath);
                    var backup = hadFile ? File.ReadAllText(dataPath) : null;

                    try
                    {
                        SeedSavedClassrooms();

                        var vm = new TeacherShellViewModel();

                        // 标一间为"当前绑定"、再勾上第二间，把两种状态都画出来。
                        // 渲染校验不该依赖真的连上服务器。
                        if (vm.Text.Targets.Count > 0)
                        {
                            vm.Text.Targets[0].IsCurrent = true;
                        }

                        if (vm.Text.Targets.Count > 1)
                        {
                            vm.Text.Targets[1].IsSelected = true;
                        }

                        vm.Text.Text = "明天带实验报告，两个班都通知一下。";

                        return new TeacherView { DataContext = vm };
                    }
                    finally
                    {
                        if (hadFile)
                        {
                            File.WriteAllText(dataPath, backup!);
                        }
                        else
                        {
                            File.Delete(dataPath);
                        }
                    }
                }, 430, 1150,
                _ => null),

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
                }, 430, 1750,
                _ => null),

            // 教师端「名单」页：导入区 + 名单里的学生。
            // 空名单时那一块整块不显示，所以必须种一份数据才画得出来。
            new("teacher-students",
                () =>
                {
                    var path = Path.Combine(LocalSettings.Directory, "teacher-rosters.json");
                    var had = File.Exists(path);
                    var backup = had ? File.ReadAllText(path) : null;

                    try
                    {
                        var settings = new TeacherRosterSettings();
                        var parsed = RosterCsv.Parse(
                            "张三,20250101,小张,A组\n李四,20250102,,B组\n王五\n赵六,20250105,六六,A组",
                            "三年二班");

                        if (parsed.Roster is { } roster)
                        {
                            settings.Rosters.Add(roster);
                            settings.ActiveRosterId = roster.Id;
                        }

                        LocalSettings.SaveRosters(settings);

                        var vm = new TeacherShellViewModel();
                        vm.NavigateStudentsCommand.Execute(null);

                        return new TeacherView { DataContext = vm };
                    }
                    finally
                    {
                        if (had && backup is not null)
                        {
                            File.WriteAllText(path, backup);
                        }
                        else
                        {
                            File.Delete(path);
                        }
                    }
                }, 430, 1500,
                _ => null),

            // 教师端「呼叫」页：模板 + 组件 + 拼装区 + 选学生。
            // 都没有名单时整页只显示"先去导入名单"那一张卡，所以必须种一份。
            new("teacher-call",
                () =>
                {
                    var path = Path.Combine(LocalSettings.Directory, "teacher-rosters.json");
                    var had = File.Exists(path);
                    var backup = had ? File.ReadAllText(path) : null;

                    try
                    {
                        var settings = new TeacherRosterSettings();
                        var parsed = RosterCsv.Parse(
                            "张三,20250101,小张,A组\n李四,20250102,,A组\n王五,,小五,B组\n赵六,20250105,六六,B组",
                            "三年二班");

                        if (parsed.Roster is { } roster)
                        {
                            settings.Rosters.Add(roster);
                            settings.ActiveRosterId = roster.Id;
                        }

                        LocalSettings.SaveRosters(settings);

                        var vm = new TeacherShellViewModel();
                        vm.NavigateCallCommand.Execute(null);


                        // 勾上一位学生，让预览那一行有内容可看
                        var first = vm.Call.Students.FirstOrDefault();
                        if (first is not null)
                        {
                            first.IsSelected = true;
                        }

                        return new TeacherView { DataContext = vm };
                    }
                    finally
                    {
                        if (had && backup is not null)
                        {
                            File.WriteAllText(path, backup);
                        }
                        else
                        {
                            File.Delete(path);
                        }
                    }
                }, 430, 1700,
                _ => null),

            // 教师端文字页的「常用语」：平时（点一下填入）与编辑态各一张。
            //
            // 同一块地方的两种样子，是否显示绑的是 !IsEditingPresets 这种取反表达式 ——
            // 编译通过不代表两个分支都画对了。两个场景共用同一份种子数据，
            // 否则两张图没法互相对照。
            new("teacher-phrases", () => CreatePhrasePage(editing: false), 430, 1400, _ => null),

            // 编辑态：每一行变成输入框 + 删除按钮，并顺手加一条空的，
            // 好看清"新增的那一行"长什么样（水位提示只在空行上才看得到）。
            new("teacher-phrases-edit", () => CreatePhrasePage(editing: true), 430, 1400, _ => null),

            // 教室端窗口自带尺寸，这里传 0 表示用窗口自己的
            new("classroom",
                () => new ClassroomWindow { DataContext = new ClassroomViewModel() }, 0, 0,
                isDark => isDark ? Combine(CommonDark, ClassroomExtraDark) : Combine(CommonLight, ClassroomExtraLight)),

            // 教室端正在显示语音转写字幕的样子。
            //
            // 大字区那块面板的可见性从「正在朗读」改成了「有字要显示就显示」
            // （文字喊话与转写字幕共用它），而待机那张图里它整个是不可见的 ——
            // 绑定名写错、chip 文案取错，都只有真把它显示出来才看得出来。
            new("classroom-transcript",
                () =>
                {
                    var vm = new ClassroomViewModel
                    {
                        CurrentSpeaker = "张老师",
                        CurrentText = "同学们把书翻到第三十七页，我们看第三题。",
                        Stage = ClassroomStage.ShowingTranscript,
                    };

                    return new ClassroomWindow { DataContext = vm };
                }, 0, 0,
                _ => null),

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

            // 语音转文字卡片展开的样子。
            //
            // 单独来一张的理由和上面的置顶档位一样：开关关着时，那几行输入框
            // 根本不在画面里（IsVisible 绑在开关上），而它们恰恰是新写的 XAML ——
            // 排版走样只有真渲染一次才看得出来。
            // 这里的赋值会走 setter 顺手落盘，但这个工具全程用临时数据目录
            // （见 previewDataDir），不会碰使用者真实的 classroom-stt.json。
            new("classroom-stt",
                () =>
                {
                    var vm = new ClassroomViewModel
                    {
                        SttEnabled = true,
                        SttBaseUrl = "https://api.openai.com/v1",
                        SttApiKey = "sk-example-not-a-real-key",
                        SttModel = "whisper-1",
                        SttLanguage = "zh",
                    };

                    return new ClassroomSettings { DataContext = vm, Height = 2600 };
                }, 0, 0,
                _ => null),

            // 开了 PIN 保护之后，设置窗口长什么样。
            //
            // 这一张必须单独渲染：分项保护的勾选列表与"有项目正被锁定"那一块
            // 都绑在"是否启用了 PIN"上，而默认那张 classroom-settings 是关着锁的 ——
            // 也就是说新加的 UI 在那张图上根本不存在。
            new("classroom-locked",
                () =>
                {
                    var lockPath = Path.Combine(LocalSettings.Directory, "settings-lock.json");
                    var hadLock = File.Exists(lockPath);
                    var lockBackup = hadLock ? File.ReadAllText(lockPath) : null;

                    try
                    {
                        SettingsLock.SetPin("2468");

                        var settings = SettingsLock.Load();
                        settings.ProtectedAreas =
                            [ProtectedAreas.Stt, ProtectedAreas.Relay, ProtectedAreas.Background];
                        settings.ProtectExit = true;
                        LocalSettings.Save("settings-lock.json", settings);

                        return new ClassroomSettings { DataContext = new ClassroomViewModel(), Height = 2900 };
                    }
                    finally
                    {
                        if (hadLock && lockBackup is not null)
                        {
                            File.WriteAllText(lockPath, lockBackup);
                        }
                        else
                        {
                            File.Delete(lockPath);
                        }
                    }
                }, 0, 0,
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
                () => new ClassShout.Design.Diagnostics.FontDiagnostics(), 1180, 460,
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
                }, 430, 1850,
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

        var allPassed = fontsPassed && palettePassed && pickerPassed && serverAddressPassed && linkChipPassed && savedClassroomsPassed && imagePrepPassed;
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

        // 收尾：撤掉临时设置目录，别给这台机器留下垃圾
        Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", null);

        if (Directory.Exists(previewDataDir))
        {
            Directory.Delete(previewDataDir, recursive: true);
        }

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
