using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassShout.Classroom.ViewModels;
using ClassShout.Core.Remote;
using ClassShout.Design.Controls;
using ClassShout.Teacher.ViewModels;

namespace ClassShout.DesignPreview;

internal static partial class Program
{
    private static int VerifyMaintenanceRuntime()
    {
        var failures = 0;
        void Check(string title, bool passed)
        {
            Console.WriteLine($"[{(passed ? "通过" : "失败")}] 诊断与迁移界面：{title}");
            if (!passed) failures++;
        }
        void PumpUntil(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            if (!done()) throw new TimeoutException("界面操作未在限定时间完成。");
        }
        var classroom = new ClassroomViewModel(45992, 45993);
        classroom.Start();
        var panel = new MaintenancePanel { DataContext = classroom };
        var window = new Window { Content = panel, Width = 650, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            panel.FindControl<Button>("Test")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => panel.FindControl<Button>("Test")!.IsEnabled);
            var text = panel.FindControl<TextBlock>("Results")!.Text ?? "";
            Check("教室端按钮执行真实本机监听诊断", text.Contains("教室：通过") && text.Contains("TTS：") && text.Contains("ClassIsland：") && text.Contains("中继："));
            Check("完成测试后可以导出报告", panel.FindControl<Button>("Report")!.IsEnabled);
            LocalSettings.Save("bells.json", new BellSettings { Bells = [new() { Label = "等待导入" }] });
            var backup = ConfigurationTransfer.Export(true);
            LocalSettings.Save("bells.json", new BellSettings { Bells = [new() { Label = "当前配置" }] });
            panel.PrepareImport(backup);
            Check("选择备份只展示预览，不提前覆盖", panel.FindControl<StackPanel>("Confirmation")!.IsVisible && LocalSettings.LoadBells().Bells.Single().Label == "当前配置");
            var confirm = panel.FindControl<StackPanel>("Confirmation")!.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "确认覆盖并导入"));
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("点击确认才落盘，并提示重启", LocalSettings.LoadBells().Bells.Single().Label == "等待导入" && panel.FindControl<TextBlock>("Status")!.Text!.Contains("重启"));
            panel.PrepareImport(backup);
            LocalSettings.Save("bells.json", new BellSettings { Bells = [new() { Label = "受保护配置" }] });
            SettingsLock.SetPin("2468");
            var locks = SettingsLock.Load();
            locks.ProtectedAreas = [ProtectedAreas.Speech];
            LocalSettings.Save("settings-lock.json", locks);
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("预览后重新锁定也不能绕过 PIN 导入", LocalSettings.LoadBells().Bells.Single().Label == "受保护配置" && panel.FindControl<TextBlock>("Status")!.Text!.Contains("解锁"));
            panel.PrepareImport(backup);
            Check("受保护设置未解锁时拒绝准备导入", !panel.FindControl<StackPanel>("Confirmation")!.IsVisible);
            SettingsLock.Disable();
            var teacher = new TeacherShellViewModel();
            try
            {
                panel.DataContext = teacher;
                panel.FindControl<Button>("Test")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => panel.FindControl<Button>("Test")!.IsEnabled);
                text = panel.FindControl<TextBlock>("Results")!.Text ?? "";
                Check("教师端未连接时显示未连接，而非虚报成功", text.Contains("教室：未连接") && text.Contains("TTS：远程项目") && text.Contains("ClassIsland：未验证"));
            }
            finally
            {
                var dispose = teacher.DisposeAsync().AsTask();
                PumpUntil(() => dispose.IsCompleted);
                dispose.GetAwaiter().GetResult();
            }
        }
        finally
        {
            window.Close();
            var dispose = classroom.DisposeAsync().AsTask();
            PumpUntil(() => dispose.IsCompleted);
            dispose.GetAwaiter().GetResult();
        }
        Console.WriteLine($"诊断与迁移界面验收：{7 - failures}/7 通过。");
        return failures == 0 ? 0 : 1;
    }
}
