using System.Collections.ObjectModel;
using ClassShout.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassShout.Teacher.ViewModels;

/// <summary>当前会话中的消息送达时间线，历史内容复用与送达记录分别保存。</summary>
public sealed class DeliveryItem : ObservableObject
{
    public DeliveryItem(TextShoutMessage message)
    {
        Receipt = new DeliveryReceipt { Id = message.Id };
        Receipt.Apply(new DeliveryEvent("sent", DateTimeOffset.UtcNow));
        Text = message.Text;
    }
    public DeliveryReceipt Receipt { get; }
    public string Text { get; }
    public string Status => Receipt.Stage switch
    {
        DeliveryStages.Delivered => "✓ 教室已送达",
        DeliveryStages.Displayed => "✓ 已上屏",
        DeliveryStages.Completed => "✓ 已朗读完",
        DeliveryStages.Failed => $"⚠ {Receipt.Failure}",
        _ => "◷ 已发出",
    };
    public string Timeline => string.Join(" → ", Receipt.Timeline.Select(item =>
        $"{Label(item.Stage)} {item.At.ToLocalTime():HH:mm:ss}{(item.Detail is null ? "" : "（" + item.Detail + "）")}"));
    private static string Label(string stage) => stage switch
    {
        "sent" => "发出", DeliveryStages.Delivered => "送达", DeliveryStages.Displayed => "上屏",
        DeliveryStages.Completed => "朗读完", _ => "失败",
    };
    public void Apply(DeliveryEvent update)
    {
        if (!Receipt.Apply(update)) return;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Timeline));
    }
}

public partial class TeacherShellViewModel
{
    public ObservableCollection<DeliveryItem> Deliveries { get; } = [];
    public DeliveryItem? LatestDelivery => Deliveries.FirstOrDefault();

    private void TrackDelivery(TextShoutMessage message)
    {
        if (Deliveries.Any(item => item.Receipt.Id == message.Id)) return;
        var item = new DeliveryItem(message);
        Deliveries.Insert(0, item);
        OnPropertyChanged(nameof(LatestDelivery));
        while (Deliveries.Count > 100) Deliveries.RemoveAt(Deliveries.Count - 1);
        _ = ExpireDeliveryAsync(item);
    }

    private async Task ExpireDeliveryAsync(DeliveryItem item)
    {
        await Task.Delay(TimeSpan.FromSeconds(45));
        Post(() =>
        {
            if (item.Receipt.Stage is "sent" or DeliveryStages.Delivered)
                item.Apply(new DeliveryEvent(DeliveryStages.Failed, DateTimeOffset.UtcNow, "教室端未上报执行结果（等待 45 秒）。"));
        });
    }

    private void ApplyDelivery(string? id, string? stage, DateTimeOffset? at, string? detail)
    {
        if (id is null || stage is null) return;
        Deliveries.FirstOrDefault(item => item.Receipt.Id == id)?.Apply(new DeliveryEvent(stage, at ?? DateTimeOffset.UtcNow, detail));
    }
}
