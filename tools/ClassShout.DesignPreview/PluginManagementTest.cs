using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassShout.Classroom.ViewModels;
using ClassShout.Core.Remote;
using ClassShout.Design.Controls;

namespace ClassShout.DesignPreview;

internal static partial class Program
{
    private static PluginManagerPanel CreatePluginManagementPreview(bool teacher)
    {
        var role = teacher ? "teacher" : "classroom";
        LocalSettings.Save(SeparatedExtensionSettings.PluginsFile(role), new PluginRegistry { Plugins =
        [
            new() { Id = "preview.reminder", Name = "上课提醒", Version = "1.0.0", Author = "示例作者", Description = "通过构造函数注入宿主服务，在独立加载上下文中发送提醒。", ManagedPackage = true, Runner = "assembly", EntryType = "Preview.Module", EntryPoint = "Preview.dll", Enabled = false },
            new() { Id = "preview.legacy", Name = "旧版进程兼容示例", Author = "旧配置迁移", Description = "旧注册保留，卸载不会删除外部程序。", Enabled = false }
        ] });
        var panel = new PluginManagerPanel { Margin = new Avalonia.Thickness(16), DataContext = teacher
            ? (object)new ClassShout.Teacher.ViewModels.TeacherShellViewModel() : new ClassroomViewModel() };
        if (!teacher) panel.PrepareInstall(CreateExamplePluginPackage());
        return panel;
    }

    private static byte[] CreateExamplePluginPackage()
    {
        var source = Path.GetFullPath("../../../../../examples/plugins/HelloPlugin/bin/Release/net10.0", AppContext.BaseDirectory);
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var file in new[] { "manifest.json", "ClassShout.Examples.HelloPlugin.dll", "ClassShout.Examples.HelloPlugin.deps.json" })
            {
                using var output = zip.CreateEntry(file).Open();
                using var input = File.OpenRead(Path.Combine(source, file)); input.CopyTo(output);
            }
        return bytes.ToArray();
    }

    private static byte[] CreatePersistentPluginPackage()
    {
        var source = Path.GetFullPath("../../../../../tools/ClassShout.PluginFixture.A/bin/Release/net10.0", AppContext.BaseDirectory);
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using (var manifest = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                manifest.Write(JsonSerializer.Serialize(new PluginManifest { Id = "gui.test", Name = "界面验收插件", Version = "1.0.0", Author = "ClassShout 验收",
                    Description = "通过构造函数注入宿主服务，在独立程序集上下文中运行。", EntryType = "ClassShout.PluginFixture.PluginModule", EntryPoint = "ClassShout.PluginFixture.A.dll" }));
            foreach (var file in new[] { "ClassShout.PluginFixture.A.dll", "ClassShout.PluginFixture.A.deps.json", "ClassShout.PluginFixture.Dependency.dll" })
            {
                using var output = zip.CreateEntry(file).Open();
                using var input = File.OpenRead(Path.Combine(source, file)); input.CopyTo(output);
            }
        }
        return bytes.ToArray();
    }

    private static int VerifyPluginManagementRuntime()
    {
        var failures = 0;
        var checks = 0;
        void Check(string title, bool passed)
        {
            checks++;
            Console.WriteLine($"[{(passed ? "通过" : "失败")}] 插件管理界面：{title}");
            if (!passed) failures++;
        }
        void PumpUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!done() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            if (!done()) throw new TimeoutException("插件管理界面等待超时。");
        }
        void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        var vm = new ClassroomViewModel(45994, 45995);
        vm.Start();
        var panel = new PluginManagerPanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 650, Height = 1000 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        Button Find(string text)
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            return panel.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, text));
        }
        void Click(string text) => Find(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var marker = Path.Combine(LocalSettings.Directory, "plugin-data", "classroom", "gui.test", "state.txt");
        try
        {
            panel.PrepareInstall(CreatePersistentPluginPackage());
            Check("安装前展示来源与权限预览", panel.FindControl<StackPanel>("Confirmation")!.IsVisible && vm.Plugins.Items.Count == 0);
            panel.FindControl<Button>("Confirm")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => vm.Plugins.Items.Count == 1);
            Check("确认安装后默认禁用", !vm.Plugins.Items.Single().Enabled && !File.Exists(marker));
            Click("详情");
            Check("显示名称版本作者及详情", panel.FindControl<TextBlock>("Details")!.Text!.Contains("ClassShout 验收"));
            panel.FindControl<TextBox>("Search")!.Text = "不存在";
            PumpUntil(() => panel.FindControl<StackPanel>("Rows")!.Children.Count == 0);
            Check("搜索过滤已安装列表", panel.FindControl<StackPanel>("Rows")!.Children.Count == 0);
            panel.FindControl<TextBox>("Search")!.Text = "";
            PumpUntil(() => panel.FindControl<StackPanel>("Rows")!.Children.Count == 1);
            Click("启用");
            PumpUntil(() => File.Exists(marker) && panel.FindControl<Button>("Install")!.IsEnabled && vm.Plugins.Items.Single().Running); Dispatcher.UIThread.RunJobs();
            var pid = File.ReadAllText(marker);
            Check("点击启用真正加载程序集并注入宿主服务", vm.Plugins.Items.Single().Running);
            vm.IpcEnabled = true;
            Pump(vm.ApplyExtensionsCommand.ExecuteAsync(null));
            Check("应用 IPC 设置不会停止或重启插件", vm.Plugins.Items.Single().Running && File.ReadAllText(marker) == pid);
            vm.IpcEnabled = false;
            Pump(vm.ApplyExtensionsCommand.ExecuteAsync(null));
            Check("关闭 IPC 后插件仍正常运行", vm.Plugins.Items.Single().Running && File.ReadAllText(marker) == pid);
            panel.PrepareUninstall("gui.test");
            SettingsLock.SetPin("2468");
            var locks = SettingsLock.Load(); locks.ProtectedAreas = [ProtectedAreas.Speech]; LocalSettings.Save("settings-lock.json", locks);
            panel.FindControl<Button>("Confirm")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check("卸载确认时再次校验 PIN", vm.Plugins.Items.Count == 1 && vm.Plugins.Items.Single().Running);
            SettingsLock.Disable(); panel.Refresh();
            window.Close();
            Check("关闭管理页不会停止插件", vm.Plugins.Items.Single().Running);
            panel = new PluginManagerPanel { DataContext = vm };
            window = new Window { Content = panel, Width = 650, Height = 1000 }; window.Show(); Dispatcher.UIThread.RunJobs();
            Click("卸载");
            Check("卸载前明确确认删除范围", panel.FindControl<StackPanel>("Confirmation")!.IsVisible);
            panel.FindControl<Button>("Confirm")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => vm.Plugins.Items.Count == 0);
            Check("确认卸载释放插件实例并删除安装文件", !Directory.Exists(Path.Combine(vm.Plugins.DirectoryPath, "gui.test")));
        }
        catch (Exception ex) { Check("界面验收异常：" + ex.Message, false); }
        finally { window.Close(); Pump(vm.DisposeAsync().AsTask()); }
        Console.WriteLine($"插件管理界面验收：{checks - failures}/{checks} 通过。");
        return failures == 0 ? 0 : 1;
    }
}
