using System.Globalization;

namespace ClassShout.Core.Remote;

/// <summary>教室本机的每周铃声；周一为最低位，周日为第六位。</summary>
public sealed class BellSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Time { get; set; } = "08:00";
    public int DaysOfWeek { get; set; } = 31;
    public string Label { get; set; } = "上课铃";
    public string? AudioWav { get; set; }
    public int FrequencyHz { get; set; } = 880;
    public int DurationMs { get; set; } = 3000;
    public int Volume { get; set; } = 80;
    public bool ShowLabel { get; set; } = true;

    public bool IsDue(DateTime localTime) => Enabled
        && TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        && time.Hour == localTime.Hour && time.Minute == localTime.Minute
        && (DaysOfWeek & (1 << (((int)localTime.DayOfWeek + 6) % 7))) != 0;
}

/// <summary>只保存本机规则，不依赖服务器或教师端连接。</summary>
public sealed class BellSettings
{
    public List<BellSchedule> Bells { get; set; } = [];

    public BellSettings Normalized()
    {
        Bells ??= [];
        // 手改 JSON 里的空项目不能使教室端在启动读取配置时崩溃。
        Bells = Bells.Where(bell => bell is not null).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bell in Bells)
        {
            if (string.IsNullOrWhiteSpace(bell.Id) || !ids.Add(bell.Id))
            {
                bell.Id = Guid.NewGuid().ToString("N");
                ids.Add(bell.Id);
            }
            // 无效时刻不能悄悄变成另一时刻响铃，保留输入并禁用。
            if (!TimeOnly.TryParseExact(bell.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                bell.Enabled = false;
            bell.DaysOfWeek &= 127;
            bell.Volume = Math.Clamp(bell.Volume, 0, 100);
            bell.FrequencyHz = Math.Clamp(bell.FrequencyHz, 100, 4000);
            bell.DurationMs = Math.Clamp(bell.DurationMs, 100, 30000);
            bell.Label = string.IsNullOrWhiteSpace(bell.Label) ? "上课铃" : bell.Label.Trim();
        }
        return this;
    }
}
