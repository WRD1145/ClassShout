using System.Net.Http.Json;
using Avalonia.Threading;
using ClassShout.Core.Protocol;
using ClassShout.Core.Remote;

namespace ClassShout.Classroom.ViewModels;

public partial class ClassroomViewModel
{
    private readonly HttpClient _ciScheduleHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ClassIslandTaskSettings _ciTasks = LocalSettings.Load("classisland-schedules.json", () => new ClassIslandTaskSettings());
    private ClassIslandSchedule _ciSchedule = new();
    private DispatcherTimer? _ciScheduleTimer;
    private bool _ciSchedulePolling;
    private bool _ciScheduleDisposed;
    private string? _ciScheduleLastError;

    private void StartClassIslandSchedule()
    {
        if (_ciScheduleTimer is not null) return;
        _ciTasks.Tasks ??= [];
        _ciTasks.Tasks = _ciTasks.Tasks.Where(task => task is not null).ToList();
        foreach (var task in _ciTasks.Tasks)
        {
            task.Text ??= "";
            if (string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Text)
                || task.Text.Length > 500 || task.Period is < 1 or > 100 || task.OffsetMinutes is < 0 or > 180)
            {
                task.Cancelled = true;
                task.Error = "本机任务参数无效，已取消，请重新创建。";
            }
        }
        _ciScheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ciScheduleTimer.Tick += async (_, _) => await PollClassIslandScheduleAsync();
        _ciScheduleTimer.Start();
        _ = PollClassIslandScheduleAsync();
    }

    private async Task PollClassIslandScheduleAsync()
    {
        if (_ciSchedulePolling || _ciScheduleDisposed) return;
        _ciSchedulePolling = true;
        try
        {
            _ciSchedule = await _ciScheduleHttp.GetFromJsonAsync<ClassIslandSchedule>("http://127.0.0.1:45902/schedule/today")
                ?? new ClassIslandSchedule { Error = "ClassIsland 返回空课表。" };
            _ciSchedule.Periods = (_ciSchedule.Periods ?? []).Where(period => period is not null).Take(100).ToList();
            if (_ciSchedule.Available && (DateTimeOffset.UtcNow - _ciSchedule.CapturedAt).Duration() > TimeSpan.FromSeconds(20))
                _ciSchedule = new ClassIslandSchedule { Error = "ClassIsland 课表已过期，请检查两端时钟。" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _ciSchedule = new ClassIslandSchedule { Error = "无法读取 ClassIsland：请确认宿主运行且已安装支持课表读取的联动插件。" };
        }
        catch (ObjectDisposedException) when (_ciScheduleDisposed) { return; }
        try
        {
            if (_ciScheduleDisposed) return;
            if (_ciScheduleLastError != _ciSchedule.Error)
            {
                _ciScheduleLastError = _ciSchedule.Error;
                AddLog("课表", _ciSchedule.Error ?? $"已读取 ClassIsland 当前课表：{_ciSchedule.PlanName}");
            }
            ExecuteClassIslandTasks();
            _ciSchedule.Tasks = BuildClassIslandTaskSummaries();
            if (_relay is { IsConnected: true } relay)
                await relay.ReportStatusAsync(IsMuted, Volume, "idle", classIsland: _ciSchedule);
        }
        catch (Exception ex)
        {
            if (!_ciScheduleDisposed) AddLog("课表", $"处理本地课表任务失败：{ex.Message}");
        }
        finally { _ciSchedulePolling = false; }
    }

    private void ExecuteClassIslandTasks()
    {
        var now = DateTimeOffset.Now;
        foreach (var task in _ciTasks.Tasks.Where(task => task is not null && !task.Cancelled && task.HandledAt is null).ToArray())
        {
            if (task.Date < DateOnly.FromDateTime(DateTime.Today))
            {
                task.HandledAt = now;
                task.Error = "已错过任务日期，不补发。";
                LocalSettings.Save("classisland-schedules.json", _ciTasks);
                continue;
            }
            if (!_ciSchedule.Available || task.Date != _ciSchedule.Date) continue;
            var due = _ciSchedule.Resolve(task.Period, task.AfterEnd, task.OffsetMinutes);
            if (due is null || due > now) continue;
            task.HandledAt = now;
            if (now - due > TimeSpan.FromMinutes(3)) task.Error = "已错过课表触发时刻（三分钟以上），不补发。";
            if (!LocalSettings.Save("classisland-schedules.json", _ciTasks))
            {
                task.Error = "无法保存执行标记，本次不发送以防重启后重复喊话。";
                AddLog("课表", task.Error);
                continue;
            }
            if (task.Error is not null) { AddLog("课表", task.Error); continue; }
            AddLog("课表", $"本地 ClassIsland 课表触发：{task.Summary}，依据最新课表「{_ciSchedule.PlanName}」。");
            PresentTextShout("ClassIsland 定时", new TextShoutMessage
            {
                Id = task.Id, Text = task.Text, Speak = task.Speak,
            });
        }
    }

    private async Task AcceptClassIslandTaskAsync(ClassIslandTask task)
    {
        var before = System.Text.Json.JsonSerializer.Deserialize<ClassIslandTaskSettings>(System.Text.Json.JsonSerializer.Serialize(_ciTasks))!;
        string? error = null;
        var existing = _ciTasks.Tasks.FirstOrDefault(item => item.Id == task.Id);
        if (string.IsNullOrWhiteSpace(task.Id) || task.Id.Length > 80) error = "任务标识无效。";
        else if (task.Cancelled)
        {
            if (existing is null) error = "要取消的任务不存在。";
            else if (existing.HandledAt is not null) error = "任务已经执行，不能取消。";
            else existing.Cancelled = true;
        }
        else if (!_ciSchedule.Available || _ciSchedule.Date != task.Date || _ciSchedule.Resolve(task.Period, task.AfterEnd, task.OffsetMinutes) is not { } due)
            error = "当前 ClassIsland 课表不可用或指定节次不存在，请刷新课表。";
        else if (due <= DateTimeOffset.Now) error = "该课表时刻已经过去，请选择尚未到点的节次。";
        else if (string.IsNullOrWhiteSpace(task.Text) || task.Text.Length > 500) error = "喊话内容需为 1–500 字。";
        else if (_ciTasks.Tasks.Count(item => item.HandledAt is null && !item.Cancelled) >= 10 && existing is null)
            error = "本教室最多保留 10 条待发课表任务。";
        else
        {
            if (existing is not null) _ciTasks.Tasks.Remove(existing);
            task.HandledAt = null;
            task.Error = null;
            _ciTasks.Tasks.Add(task);
            while (_ciTasks.Tasks.Count > 20)
            {
                var historyIndex = _ciTasks.Tasks.FindIndex(item => item.HandledAt is not null || item.Cancelled);
                if (historyIndex < 0) break;
                _ciTasks.Tasks.RemoveAt(historyIndex);
            }
        }
        if (error is null && !LocalSettings.Save("classisland-schedules.json", _ciTasks))
        {
            _ciTasks.Tasks = before.Tasks;
            error = "教室本机保存失败，任务未确认。";
        }
        AddLog("课表", error ?? $"已保存到本机：{task.Summary}");
        _ciSchedule.Tasks = BuildClassIslandTaskSummaries();
        if (_relay is { } relay)
            await relay.ReportStatusAsync(IsMuted, Volume, "idle", classIsland: _ciSchedule,
                classIslandTaskAck: task.Id, classIslandTaskError: error);
    }

    // 状态通道有请求体上限，只传必要摘要；完整文字始终保存在教室本机。
    private List<ClassIslandTask> BuildClassIslandTaskSummaries() => _ciTasks.Tasks
        .OrderBy(task => task.HandledAt is null && !task.Cancelled ? 0 : 1)
        .ThenByDescending(task => task.HandledAt).Take(10).Select(task => new ClassIslandTask
    {
        Id = task.Id, Date = task.Date, Period = task.Period, AfterEnd = task.AfterEnd,
        OffsetMinutes = task.OffsetMinutes, Speak = task.Speak, Cancelled = task.Cancelled,
        HandledAt = task.HandledAt, Error = task.Error,
        Text = task.Text.Length > 60 ? task.Text[..60] + "…" : task.Text,
    }).ToList();
}
