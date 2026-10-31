namespace ClassShout.Core.Remote;

/// <summary>从绑定教室的 ClassIsland 实时取得的当天课表摘要，不传输完整档案附加数据。</summary>
public sealed class ClassIslandSchedule
{
    public bool Available { get; set; }
    public string? Error { get; set; }
    public DateOnly Date { get; set; }
    public string PlanName { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public List<ClassIslandPeriod> Periods { get; set; } = [];
    public List<ClassIslandTask> Tasks { get; set; } = [];

    public DateTimeOffset? Resolve(int period, bool afterEnd, int offsetMinutes)
    {
        if (!Available || Date == default || Periods is null || offsetMinutes is < 0 or > 180) return null;
        var item = Periods.FirstOrDefault(item => item is not null && item.Number == period);
        if (item is null || item.Start < TimeSpan.Zero || item.End >= TimeSpan.FromDays(1) || item.End < item.Start) return null;
        var time = afterEnd ? item.End : item.Start;
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return null;
        return new DateTimeOffset(Date.ToDateTime(TimeOnly.MinValue).Add(time), CapturedAt.Offset).AddMinutes(offsetMinutes);
    }
}

public sealed record ClassIslandPeriod(int Number, TimeSpan Start, TimeSpan End)
{
    public string Label => $"第 {Number} 节 · {Start:hh\\:mm}–{End:hh\\:mm}";
}

/// <summary>当天某一节课的本地定时喊话。执行时重新读取 ClassIsland，不冻结旧时间。</summary>
public sealed class ClassIslandTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateOnly Date { get; set; }
    public int Period { get; set; } = 1;
    public bool AfterEnd { get; set; } = true;
    public int OffsetMinutes { get; set; }
    public string Text { get; set; } = "";
    public bool Speak { get; set; } = true;
    public bool Cancelled { get; set; }
    public DateTimeOffset? HandledAt { get; set; }
    public string? Error { get; set; }
    public string Summary => $"第 {Period} 节{(AfterEnd ? "下课" : "上课")}后 {OffsetMinutes} 分钟：{Text}";
    public string StatusText => Cancelled ? "已取消" : HandledAt is null ? "等待课表触发" : Error ?? "已在教室本地触发";
}

public sealed class ClassIslandTaskSettings
{
    public List<ClassIslandTask> Tasks { get; set; } = [];
}
