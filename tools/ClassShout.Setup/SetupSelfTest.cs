using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassShout.Setup;

internal static class SetupSelfTest
{
    internal static int Run()
    {
        ApplicationConfiguration.Initialize();
        var state = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClassShout-Setup-Tests-" + Guid.NewGuid().ToString("N")));
        var target = Path.Combine(state, "app");
        Directory.CreateDirectory(state);
        var screenshots = Path.GetFullPath(Path.Combine("artifacts", "setup-preview"));
        Directory.CreateDirectory(screenshots);
        var stdout = Console.Out;
        var failures = 0;
        var checks = 0;
        void Check(string title, bool passed) { checks++; if (!passed) failures++; stdout.WriteLine($"[{(passed ? "通过" : "失败")}] 安装器：{title}"); }
        void Wait(SetupWindow window)
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            do { Application.DoEvents(); Thread.Sleep(10); } while (window.Busy && DateTime.UtcNow < deadline);
            if (window.Busy) throw new TimeoutException("安装器界面未结束操作。");
            Application.DoEvents();
        }
        bool Rejected(Action action) { try { action(); return false; } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or AggregateException) { return true; } }
        var info = new PayloadInfo { AppId = "classroom", Title = "ClassShout 安装验收", Exe = "ClassShout.Classroom.exe", Folder = "Classroom", Version = "2.0.0" };
        byte[] Package(params (string Name, string Value)[] files)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open(), Encoding.UTF8); writer.Write(file.Value); }
            return memory.ToArray();
        }
        var bytes = Package((info.Exe, "原始程序"), ("lib.dll", "原始依赖"), ("native/helper.dll", "原始组件"));
        Program.ConfigureTest(info, () => new MemoryStream(bytes));
        var approve = true;
        try
        {
            Check("三段与四段零修订号识别为同版本", Program.SameVersion("2.0.0", "2.0.0.0"));
            Program.RequestedTarget = target;
            Program.ConfigureTest(new PayloadInfo { AppId = "..", Version = "2.0.0" }, () => new MemoryStream(bytes));
            Check("无效安装包身份在写入前拒绝", Rejected(() => Program.ExecuteAction("install", true)) && !Directory.Exists(target));
            Program.ConfigureTest(info, () => new MemoryStream(bytes));
            using var window = new SetupWindow(target, confirm: _ => approve);
            window.SetLaunchAfter(false);
            Console.SetOut(new GuiLogWriter(window.AppendLog, stdout));
            window.Show(); Application.DoEvents();
            Check("全新目录仅提供安装", window.AvailableActions.SequenceEqual(["install"]));
            window.CaptureView(Path.Combine(screenshots, "setup-install.png"));
            window.ClickAction(); Wait(window);
            Check("点击安装实际写入程序与安装记录", window.ResultCode == 0 && File.Exists(Path.Combine(target, info.Exe)) && File.Exists(Path.Combine(target, "classshout-install.json")));
            Check("同版本提供更新、修复和卸载", window.AvailableActions.Order().SequenceEqual(new[] { "repair", "uninstall", "update" }.Order()));
            window.SelectAction("repair"); Application.DoEvents(); window.CaptureView(Path.Combine(screenshots, "setup-same-version.png"));
            Directory.CreateDirectory(Path.Combine(target, "logs"));
            var log = Path.Combine(target, "logs", "keep.log"); File.WriteAllText(log, "保留日志");
            var personal = Path.Combine(target, "personal.txt"); File.WriteAllText(personal, "个人文件");
            File.Delete(Path.Combine(target, info.Exe)); File.WriteAllText(Path.Combine(target, "lib.dll"), "已损坏");
            window.RefreshState(); window.SelectAction("repair"); window.ClickAction(); Wait(window);
            Check("修复恢复丢失主程序与损坏依赖", window.ResultCode == 0 && File.ReadAllText(Path.Combine(target, info.Exe)) == "原始程序" && File.ReadAllText(Path.Combine(target, "lib.dll")) == "原始依赖");
            Check("修复保留日志和个人文件", File.ReadAllText(log) == "保留日志" && File.ReadAllText(personal) == "个人文件");
            var recordPath = Path.Combine(target, "classshout-install.json");
            var record = JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(recordPath))!;
            record.Version = "1.0.0"; File.WriteAllText(recordPath, JsonSerializer.Serialize(record)); window.RefreshState();
            Check("旧版本提供更新和卸载，不提供跨版本修复", window.AvailableActions.SequenceEqual(["update", "uninstall"]));
            window.SelectAction("update"); Application.DoEvents(); window.CaptureView(Path.Combine(screenshots, "setup-update.png"));
            window.ClickAction(); Wait(window);
            Check("更新写入安装包版本", window.ResultCode == 0 && Program.ReadManagedVersion(target) == info.Version);
            record.Version = "3.0.0"; File.WriteAllText(recordPath, JsonSerializer.Serialize(record)); window.RefreshState(); window.SelectAction("update");
            Check("旧安装包不能覆盖较新版本", !window.CanExecute && Rejected(() => Program.ExecuteAction("update", true)));
            record.Version = info.Version; File.WriteAllText(recordPath, JsonSerializer.Serialize(record)); window.RefreshState();
            Program.RequestedTarget = Path.GetPathRoot(target)!;
            Check("拒绝磁盘根目录操作", Rejected(() => Program.ExecuteAction("uninstall", true)));
            Program.RequestedTarget = target;
            var other = Path.Combine(state, "other"); Directory.CreateDirectory(other); var otherFile = Path.Combine(other, "keep.txt"); File.WriteAllText(otherFile, "不能覆盖");
            Program.RequestedTarget = other;
            Check("拒绝覆盖无关非空目录", Rejected(() => Program.ExecuteAction("install", true)) && File.ReadAllText(otherFile) == "不能覆盖");
            Program.RequestedTarget = target;
            var outside = Path.Combine(state, "outside.txt"); File.WriteAllText(outside, "不能越界");
            var bad = Package(("../outside.txt", "越界"), (info.Exe, "错误"));
            Program.TestPayload = () => new MemoryStream(bad);
            Check("安装包越界在写入前拒绝", Rejected(() => Program.ExecuteAction("update", true)) && File.ReadAllText(outside) == "不能越界" && File.ReadAllText(Path.Combine(target, info.Exe)) == "原始程序");
            var duplicate = Package((info.Exe, "一个"), (info.Exe.ToUpperInvariant(), "另一个")); Program.TestPayload = () => new MemoryStream(duplicate);
            Check("重复路径在写入前拒绝", Rejected(() => Program.ExecuteAction("update", true)));
            Program.TestPayload = () => new MemoryStream(Package((info.Exe, "覆盖后"), ("lib.dll", "新依赖")));
            using (var locked = new FileStream(Path.Combine(target, "lib.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Check("文件占用导致失败时回滚已经写入的文件", Rejected(() => Program.ExecuteAction("update", true)) && File.ReadAllText(Path.Combine(target, info.Exe)) == "原始程序");
            Program.TestPayload = () => new MemoryStream(bytes);
            record.Files.Add("../outside.txt"); File.WriteAllText(recordPath, JsonSerializer.Serialize(record));
            Check("损坏卸载记录不能越界删除", Rejected(() => Program.ExecuteAction("uninstall", true)) && File.ReadAllText(outside) == "不能越界");
            record.Files.Remove("../outside.txt"); File.WriteAllText(recordPath, JsonSerializer.Serialize(record));
            window.RefreshState(); window.SelectAction("uninstall"); Application.DoEvents(); window.CaptureView(Path.Combine(screenshots, "setup-uninstall.png"));
            approve = false; window.ClickAction(); Application.DoEvents();
            Check("取消卸载不删除程序", !window.Busy && File.Exists(Path.Combine(target, info.Exe)));
            approve = true; window.ClickAction(); Wait(window);
            Check("确认卸载只移除清单中的程序", window.ResultCode == 0 && !File.Exists(Path.Combine(target, info.Exe)) && File.Exists(log) && File.Exists(personal));
            Check("卸载后保留归属记录便于安全重新安装", Program.ReadManagedVersion(target) is null && window.AvailableActions.SequenceEqual(["install"]));
            window.ClickAction(); Wait(window);
            Check("保留个人文件的目录可以重新安装", window.ResultCode == 0 && File.Exists(Path.Combine(target, info.Exe)) && File.Exists(personal));

            // 使用独立测试注册表节点和文件夹验证真实 COM 快捷方式，不碰正常安装。
            Program.TestRegistryRoot = @"Software\ClassShout\InstallerSelfTest\" + Guid.NewGuid().ToString("N");
            Program.TestIntegrationRoot = Path.Combine(state, "integration"); Directory.CreateDirectory(Program.TestIntegrationRoot);
            Program.SkipOsIntegration = false;
            Check("安装和修复可创建维护器、真实快捷方式与登记", Program.ExecuteAction("repair", true) == 0 && File.Exists(Path.Combine(Program.TestIntegrationRoot, info.Title + ".lnk")));
            using (var key = Registry.CurrentUser.OpenSubKey(Program.TestRegistryRoot + "\\ClassShout-" + info.AppId))
            {
                Check("卸载登记指向维护器而非应用本体", (key?.GetValue("UninstallString") as string)?.Contains("ClassShout.Setup.exe") == true && (int?)(key?.GetValue("NoRepair")) == 0);
            }
            var otherShortcut = Path.Combine(Program.TestIntegrationRoot, "menu", "另一端.lnk"); File.WriteAllText(otherShortcut, "保留另一端");
            Check("卸载不删除另一端开始菜单项", Program.ExecuteAction("uninstall", true) == 0 && File.Exists(otherShortcut));
            window.Close();
        }
        catch (Exception ex) { failures++; stdout.WriteLine("[失败] 安装器验收异常：" + ex); }
        finally
        {
            Console.SetOut(stdout);
            if (Program.TestRegistryRoot is { } registry && registry.StartsWith(@"Software\ClassShout\InstallerSelfTest\", StringComparison.Ordinal) && Guid.TryParse(Path.GetFileName(registry), out _))
                Registry.CurrentUser.DeleteSubKeyTree(registry, false);
            if (!string.Equals(Path.GetDirectoryName(state), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(state).Replace("ClassShout-Setup-Tests-", ""), out _))
                throw new IOException("验收目录边界异常，拒绝清理。");
            Directory.Delete(state, true);
        }
        stdout.WriteLine($"安装器 GUI 与文件操作验收：{checks - failures}/{checks}，失败 {failures}。");
        return failures == 0 ? 0 : 1;
    }
}
