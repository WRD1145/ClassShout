using System.Collections.ObjectModel;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

public partial class TeacherShellViewModel
{
    public ObservableCollection<ClassIslandPeriod> ClassIslandPeriods { get; } = [];
    public ObservableCollection<ClassIslandTask> ClassIslandTasks { get; } = [];
    [ObservableProperty] private ClassIslandPeriod? _selectedClassIslandPeriod;
    [ObservableProperty] private bool _classIslandAfterEnd = true;
    [ObservableProperty] private int _classIslandOffsetMinutes;
    [ObservableProperty] private string _classIslandStatus = "绑定教室后刷新，可读取该教室 ClassIsland 当天正在使用的课表。";
    [ObservableProperty] private bool _classIslandBusy;
    private ClassIslandSchedule? _classIslandSnapshot;
    private string? _classIslandSnapshotUuid;
    private string? _classIslandSnapshotServer;

    [RelayCommand]
    private async Task RefreshClassIslandAsync()
    {
        if (_relay?.BoundUuid is not { } uuid)
        {
            ClassIslandStatus = "请先登录并绑定教室，再读取该教室的 ClassIsland 课表。";
            return;
        }
        ClassIslandBusy = true;
        var server = _relaySettings.ServerUrl;
        try
        {
            var snapshot = await new ClassIslandScheduleClient(_http, _relaySettings).ReadAsync(uuid);
            if (_relay?.BoundUuid != uuid || _relaySettings.ServerUrl != server)
            {
                ClassIslandStatus = "教室或服务器已切换，请重新刷新课表。";
                return;
            }
            _classIslandSnapshot = snapshot;
            _classIslandSnapshotUuid = uuid;
            _classIslandSnapshotServer = server;
            ClassIslandPeriods.Clear();
            ClassIslandTasks.Clear();
            foreach (var period in snapshot.Periods ?? []) ClassIslandPeriods.Add(period);
            foreach (var task in snapshot.Tasks ?? []) ClassIslandTasks.Add(task);
            SelectedClassIslandPeriod = ClassIslandPeriods.FirstOrDefault();
            ClassIslandStatus = snapshot.Available
                ? $"{ClassroomName} · {snapshot.Date:yyyy-MM-dd} · {snapshot.PlanName}（教室实时读取）"
                : snapshot.Error ?? "ClassIsland 今天没有可用课表。";
        }
        finally { ClassIslandBusy = false; }
    }

    [RelayCommand]
    private async Task ScheduleByClassIslandAsync()
    {
        if (_relay?.BoundUuid is not { } uuid || _classIslandSnapshot is not { Available: true } snapshot || SelectedClassIslandPeriod is not { } period
            || _classIslandSnapshotUuid != uuid || _classIslandSnapshotServer != _relaySettings.ServerUrl)
        {
            ClassIslandStatus = "请先刷新绑定教室的课表并选择节次。";
            return;
        }
        if (Text.HasImage || string.IsNullOrWhiteSpace(Text.Text))
        {
            ClassIslandStatus = "请在文字页填写要发送的文字；课表定时暂不支持图片。";
            return;
        }
        var task = new ClassIslandTask
        {
            Date = snapshot.Date, Period = period.Number, AfterEnd = ClassIslandAfterEnd,
            OffsetMinutes = ClassIslandOffsetMinutes, Text = Text.Text.Trim(), Speak = Text.Speak,
        };
        ClassIslandBusy = true;
        try
        {
            var error = await new ClassIslandScheduleClient(_http, _relaySettings).SendAsync(uuid, task);
            ClassIslandStatus = error ?? $"教室已确认保存：{task.Summary}。执行时按最新课表触发，手机可关闭。";
            AddLog(ClassIslandStatus);
            if (error is null) ClassIslandTasks.Add(task);
        }
        finally { ClassIslandBusy = false; }
    }

    [RelayCommand]
    private async Task CancelClassIslandTaskAsync(ClassIslandTask? task)
    {
        if (task is null || _relay?.BoundUuid is not { } uuid) return;
        if (_classIslandSnapshotUuid != uuid || _classIslandSnapshotServer != _relaySettings.ServerUrl)
        {
            ClassIslandStatus = "教室或服务器已切换，请刷新后再取消任务。";
            return;
        }
        var cancellation = new ClassIslandTask { Id = task.Id, Cancelled = true };
        var error = await new ClassIslandScheduleClient(_http, _relaySettings).SendAsync(uuid, cancellation);
        ClassIslandStatus = error ?? "教室已确认取消课表任务。";
        if (error is null) ClassIslandTasks.Remove(task);
    }
}
