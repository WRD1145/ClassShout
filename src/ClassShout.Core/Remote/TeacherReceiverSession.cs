namespace ClassShout.Core.Remote;

/// <summary>前台接收服务的恢复点；令牌不得输出到日志或诊断界面。</summary>
public sealed record TeacherReceiverSession(string ServerUrl, string Token, string Uuid, string? ClassroomName, long Since);
