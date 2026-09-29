namespace ClassShout.Core.Remote;

/// <summary>
/// 随机叫人：按"时间因子"加权抽学生。
///
/// 为什么不是纯随机：纯随机会出现"这节课连叫同一个学生三次"这种显然不公平的结果，
/// 而老师想要的是"人人都会被叫到，但刚叫过的人先缓一缓"。
///
/// 规则（老师看得见的那部分）：
///   · 每人有一个**隐形**的时间因子，0.00 起步，上限 1.00；
///   · 因子越小越容易被抽到（权重 = 1 − 因子，最低留 5% 的边）；
///   · 抽中一次就把因子重置到 0.95～1.00 之间（刚叫过 → 概率压到最低）；
///   · 因子随时间**线性**衰减回 0，衰减窗口可调（默认 40 分钟，差不多一节课）。
///
/// 实现上刻意不跑后台计时器：存的是"因子值 + 写入时刻"，当前值用一个减法推出来
/// （见 <see cref="EffectiveFactor"/>）。这样应用关掉、机器休眠、甚至系统时间被改过，
/// 都不会让这套权重失准或失效。
/// </summary>
public static class RandomCall
{
    /// <summary>默认的衰减窗口：一节课。</summary>
    public static readonly TimeSpan DefaultDecayWindow = TimeSpan.FromMinutes(40);

    /// <summary>可选的衰减窗口（设置界面里那几档）。</summary>
    public static readonly IReadOnlyList<TimeSpan> DecayChoices =
    [
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(40),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(90),
    ];

    /// <summary>一次最多抽几位（再多，屏幕上念一长串名字也没意义）。</summary>
    public const int MaxCount = 10;

    /// <summary>刚被抽中时的重置下限（0.95～1.00 之间随机）。</summary>
    public const double ResetFloor = 0.95;

    /// <summary>因子的上限。</summary>
    public const double MaxFactor = 1.0;

    /// <summary>权重的最低值：刚叫过的人也不是完全没机会（否则连抽两次永远轮不到他）。</summary>
    public const double MinWeight = 0.05;

    /// <summary>
    /// 某个学生**此刻**的时间因子：按 <see cref="Student.FactorSetAt"/> 到现在的时间线性衰减。
    /// </summary>
    /// <param name="student">学生。</param>
    /// <param name="now">当前时刻。</param>
    /// <param name="decayWindow">从 1.00 衰减到 0.00 要多久。</param>
    public static double EffectiveFactor(Student student, DateTimeOffset now, TimeSpan decayWindow)
    {
        if (student.TimeFactor <= 0 || student.FactorSetAt is not { } setAt)
        {
            return 0;
        }

        var window = decayWindow <= TimeSpan.Zero ? DefaultDecayWindow : decayWindow;
        var elapsed = now - setAt;

        // 时间被往回调过（校时、改系统时间）时当作"没过去多久"，而不是让因子凭空变大
        if (elapsed <= TimeSpan.Zero)
        {
            return Math.Clamp(student.TimeFactor, 0, MaxFactor);
        }

        var remaining = student.TimeFactor - (elapsed.TotalSeconds / window.TotalSeconds);
        return Math.Clamp(remaining, 0, MaxFactor);
    }

    /// <summary>抽中概率的权重：因子越小权重越大。</summary>
    public static double Weight(Student student, DateTimeOffset now, TimeSpan decayWindow)
        => Math.Max(MinWeight, 1 - EffectiveFactor(student, now, decayWindow));

    /// <summary>
    /// 从候选里抽 <paramref name="count"/> 位（不重复）。
    ///
    /// 抽中的学生会被就地更新：新的因子（0.95～1.00）与写入时刻都写到对象上，
    /// 由调用方负责落盘 —— 抽人这件事本来就该顺手记住，不然"刚叫过"就没意义了。
    /// </summary>
    /// <param name="candidates">候选（已按小组/性别筛过）。</param>
    /// <param name="count">抽几位。</param>
    /// <param name="now">当前时刻。</param>
    /// <param name="decayWindow">衰减窗口。</param>
    /// <param name="random">随机源；自检里塞一个固定的，好复现。</param>
    /// <returns>抽中的学生，顺序就是抽出来的顺序。</returns>
    public static IReadOnlyList<Student> Pick(
        IReadOnlyList<Student> candidates,
        int count,
        DateTimeOffset now,
        TimeSpan decayWindow,
        Random? random = null)
    {
        if (candidates.Count == 0 || count <= 0)
        {
            return [];
        }

        random ??= Random.Shared;

        var wanted = Math.Min(count, candidates.Count);
        var pool = candidates.ToList();
        var picked = new List<Student>(wanted);

        for (var i = 0; i < wanted; i++)
        {
            var total = pool.Sum(student => Weight(student, now, decayWindow));
            var roll = random.NextDouble() * total;
            var index = pool.Count - 1;

            for (var j = 0; j < pool.Count; j++)
            {
                roll -= Weight(pool[j], now, decayWindow);

                if (roll <= 0)
                {
                    index = j;
                    break;
                }
            }

            var chosen = pool[index];
            pool.RemoveAt(index);

            // 重置：0.95～1.00 之间随机。固定成 1.00 的话，连着抽两次的结果会完全一样
            // （两次都是"刚叫过"里权重最低的那个），随机一点更像真的在抽。
            chosen.TimeFactor = ResetFloor + (random.NextDouble() * (MaxFactor - ResetFloor));
            chosen.FactorSetAt = now;

            picked.Add(chosen);
        }

        return picked;
    }

    /// <summary>
    /// 按小组与性别筛选候选。
    /// </summary>
    /// <param name="students">整份名单。</param>
    /// <param name="group">只要这一组；留空表示不限。</param>
    /// <param name="gender">只要这个性别（「男」「女」）；留空表示不限。</param>
    /// <param name="random">随机源（打乱用）；自检里塞一个固定的，好复现。</param>
    /// <returns>候选（顺序已打乱——名单顺序里，同组的人常常连在一起）。</returns>
    public static IReadOnlyList<Student> Candidates(
        IReadOnlyList<Student> students,
        string? group,
        string? gender,
        Random? random = null)
    {
        var matched = students.Where(student =>
            (string.IsNullOrWhiteSpace(group)
             || string.Equals(student.Group?.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(gender)
                || string.Equals(RosterCsv.NormalizeGender(student.Gender), gender.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 打乱之后再交给加权抽取：名单里同组的人常常连在一起，
        // 而"权重算出来正好相等"（都没被叫过）时，抽取结果会偏向排在前面的那些。
        Shuffle(matched, random ?? Random.Shared);

        return matched;
    }

    private static void Shuffle(List<Student> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
