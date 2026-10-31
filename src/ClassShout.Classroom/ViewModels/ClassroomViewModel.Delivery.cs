using ClassShout.Core.Protocol;

namespace ClassShout.Classroom.ViewModels;

public partial class ClassroomViewModel
{
    private string _audioReceiptId = string.Empty;
    private string? _audioReceiptSession;
    private async Task ReportDeliveryAsync(string id, string? sessionId, string stage, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var at = DateTimeOffset.UtcNow;
        try
        {
            if (sessionId is not null)
            {
                ClassShout.Core.Net.TeacherSession? session;
                lock (_sessions) _sessions.TryGetValue(sessionId, out session);
                ShoutMessage report = stage == DeliveryStages.Delivered
                    ? new AckMessage { Id = id, Ok = true, Detail = "教室已送达" }
                    : new StatusMessage
                    {
                        MessageId = id, DeliveryStage = stage, DeliveryDetail = detail, DeliveryAt = at,
                        Muted = IsMuted, Volume = Volume,
                    };
                if (session is null || !await session.SendAsync(report))
                    Post(() => AddLog("回执", $"消息 {id} 的 {stage} 回执发送失败：教师端已断开。"));
            }
            else if (_relay is { } relay)
            {
                await relay.ReportStatusAsync(IsMuted, Volume, "idle", messageId: id,
                    deliveryStage: stage, deliveryDetail: detail, deliveryAt: at);
            }
        }
        catch (Exception ex)
        {
            Post(() => AddLog("回执", $"消息 {id} 回执发送失败：{ex.Message}"));
        }
    }
}
