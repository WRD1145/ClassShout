using Avalonia.Controls;
using Avalonia.Input;

namespace ClassShout.Design;

/// <summary>
/// 「连点版本号 N 次开启开发者模式」这个手势。
///
/// 手势借自 ClassIsland（它在「关于」里连点应用图标 10 次开启调试界面），用同一套
/// 约定是有意的：会去翻开发者选项的人多半用过 ClassIsland，不必再学一套。
///
/// 为什么单独抽出来：界面上**不止一处**显示版本号（「关于」里一行，
/// 「版本与更新」卡片里还有一行），而用户会去点自己最先看到的那一行。
/// 手势在两处各写一遍，迟早会出现"一处能点开、另一处点了没反应" ——
/// 那种不一致比两个地方都点不开更让人困惑。
/// </summary>
public sealed class DeveloperTapGesture
{
    /// <summary>解锁需要连点多少次。</summary>
    public const int UnlockTapCount = 10;

    /// <summary>两次点击间隔超过它就重新计数。手滑点两下不该被算进那 10 次里。</summary>
    public static readonly TimeSpan TapWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>从第几次开始给出"还差几次"的提示：太早提示会让人误以为按错了。</summary>
    public const int HintAfterTaps = 5;

    private readonly Action<string>? _hint;
    private readonly Action? _unlocked;

    private int _tapCount;
    private DateTimeOffset _lastTapAt = DateTimeOffset.MinValue;
    private bool _attached;

    /// <param name="hint">要说一句临时提示时调用（"再点 3 次…"）。</param>
    /// <param name="unlocked">刚刚解锁时调用。</param>
    public DeveloperTapGesture(Action<string>? hint = null, Action? unlocked = null)
    {
        _hint = hint;
        _unlocked = unlocked;
    }

    /// <summary>当前是否需要解锁（已经开着的时候再点只给提示）。</summary>
    public bool IsUnlocked => DeveloperMode.IsEnabled;

    /// <summary>挂到一个控件上。</summary>
    public void Attach(Control target)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;

        // 用 PointerPressed 而不是 Button：版本号必须看起来就是一行普通的文字，
        // 一旦做成按钮，它就变成了"一个功能"，而不是藏在关于里的入口。
        target.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(target, $"连点 {UnlockTapCount} 次可开启开发者模式");
        target.PointerPressed += (_, _) => Tap(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 记录一次点击。
    ///
    /// 时间由调用方给：这样"连点"的判定规则可以脱离界面直接断言 ——
    /// 规则本身很短（间隔太久就重新计数），但写错的表现是"怎么点都不解锁"，
    /// 而那种问题只有真人反复点才能发现。
    /// </summary>
    public void Tap(DateTimeOffset now)
    {
        _tapCount = NextCount(_tapCount, _lastTapAt, now);
        _lastTapAt = now;

        if (DeveloperMode.IsEnabled)
        {
            // 已解锁时再点，给一句明确的提示 —— 否则用户会以为"怎么点都没反应"，
            // 而这正是我们最不希望开发者模式给人的印象。
            _hint?.Invoke("开发者模式已开启。");
            return;
        }

        if (_tapCount >= UnlockTapCount)
        {
            _tapCount = 0;
            DeveloperMode.Enable();
            _unlocked?.Invoke();
            return;
        }

        if (_tapCount >= HintAfterTaps)
        {
            _hint?.Invoke($"再点 {UnlockTapCount - _tapCount} 次可开启开发者模式…");
        }
    }

    /// <summary>
    /// 数到第几次了。
    ///
    /// 两次之间隔太久就从头数起：连点必须是"连"着点，
    /// 隔一天再点一下不该把昨天那几下续上。
    /// </summary>
    public static int NextCount(int current, DateTimeOffset lastTapAt, DateTimeOffset now)
        => now - lastTapAt > TapWindow ? 1 : current + 1;
}
