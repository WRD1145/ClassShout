namespace ClassShout.Core.Protocol;

/// <summary>同一消息的执行回执阶段，随 status 消息传输。</summary>
public static class DeliveryStages
{
    public const string Delivered = "delivered";
    public const string Displayed = "displayed";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>回执时间线上的一个节点；保留教室端的实际发生时间。</summary>
public sealed record DeliveryEvent(string Stage, DateTimeOffset At, string? Detail = null);

/// <summary>单条消息的回执状态。重复或乱序到达不得使状态倒退。</summary>
public sealed class DeliveryReceipt
{
    public required string Id { get; init; }
    public List<DeliveryEvent> Timeline { get; } = [];
    public string Stage { get; private set; } = "sent";
    public string? Failure { get; private set; }

    public bool Apply(DeliveryEvent update)
    {
        static int Rank(string stage) => stage switch
        {
            "sent" => 0,
            DeliveryStages.Delivered => 1,
            DeliveryStages.Displayed => 2,
            DeliveryStages.Completed => 3,
            DeliveryStages.Failed => 4,
            _ => -1,
        };
        if (Rank(update.Stage) < 0 || Timeline.Any(item => item.Stage == update.Stage)) return false;
        Timeline.Add(update);
        Timeline.Sort((a, b) => a.At.CompareTo(b.At));
        if (Rank(update.Stage) >= Rank(Stage))
        {
            Stage = update.Stage;
            Failure = update.Stage == DeliveryStages.Failed ? update.Detail ?? "教室端执行失败" : null;
        }
        return true;
    }
}
