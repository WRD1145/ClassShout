using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using ClassShout.Core.Remote;
using ClassShout.Teacher.ViewModels;

namespace ClassShout.DesignPreview;

internal static partial class Program
{
    private static int VerifyTeacherExtensionsRuntime()
    {
        var failures = 0;
        void Check(string title, bool passed)
        {
            Console.WriteLine($"[{(passed ? "通过" : "失败")}] 教师端扩展：{title}");
            if (!passed) failures++;
        }
        void Pump(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            task.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
        }
        var settings = new LocalExtensionSettings { IpcEnabled = true };
        LocalSettings.Save("teacher-extensions.json", settings);
        var shell = new TeacherShellViewModel();
        shell.StartTeacherExtensions();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        { BaseAddress = new Uri("http://127.0.0.1:45904"), Timeout = TimeSpan.FromSeconds(10) };
        Task<HttpResponseMessage> Send(string id, string text) => http.PostAsync("/v1/shouts",
            new StringContent(JsonSerializer.Serialize(new { id, text, speak = false }), Encoding.UTF8, "application/json"));
        try
        {
            var denied = http.GetAsync("/v1/status"); Pump(denied);
            Check("缺少令牌返回 401", denied.Result.StatusCode == HttpStatusCode.Unauthorized);
            denied.Result.Dispose();
            http.DefaultRequestHeaders.Authorization = new("Bearer", settings.Token);
            var offline = Send("offline", "离线不得发送"); Pump(offline);
            Check("未连接时返回 409", offline.Result.StatusCode == HttpStatusCode.Conflict);
            offline.Result.Dispose();
            shell.ManualAddress = "127.0.0.1:45900";
            Pump(shell.ConnectManualCommand.ExecuteAsync(null));
            Check("真实 TCP 连接教室端", shell.IsConnected);
            var send = Send("teacher-ipc-test", "教师端 IPC 实际上屏验收"); Pump(send);
            Check("发送返回 202", send.Result.StatusCode == HttpStatusCode.Accepted);
            send.Result.Dispose();
            var displayed = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!displayed && DateTime.UtcNow < deadline)
            {
                var read = http.GetStringAsync("/v1/receipts/teacher-ipc-test"); Pump(read);
                using var receipt = JsonDocument.Parse(read.Result);
                displayed = receipt.RootElement.GetProperty("stage").GetString() == "displayed";
                if (!displayed) Thread.Sleep(50);
            }
            Check("收到实际教室上屏回执", displayed);
            var duplicate = Send("teacher-ipc-test", "教师端 IPC 实际上屏验收"); Pump(duplicate);
            Check("重复消息不重发", duplicate.Result.StatusCode == HttpStatusCode.OK && shell.Deliveries.Count == 1);
            duplicate.Result.Dispose();
            var conflict = Send("teacher-ipc-test", "同标识不同内容"); Pump(conflict);
            Check("内容冲突返回 409", conflict.Result.StatusCode == HttpStatusCode.Conflict);
            conflict.Result.Dispose();
            var hidden = http.GetAsync("/v1/receipts/not-found"); Pump(hidden);
            Check("未知回执返回 404", hidden.Result.StatusCode == HttpStatusCode.NotFound);
            hidden.Result.Dispose();
            var pluginScript = Path.GetFullPath("../../../../../examples/plugins/hello.ps1", AppContext.BaseDirectory);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            settings.Plugins.Add(new() { Id = "teacher-hello", Executable = powershell,
                Arguments = ["-NoProfile", "-File", pluginScript], Enabled = true });
            LocalSettings.Save("teacher-extensions.json", settings);
            Pump(shell.ApplyTeacherExtensionsCommand.ExecuteAsync(null));
            var pluginDeadline = DateTime.UtcNow.AddSeconds(15);
            while (shell.Deliveries.Count < 2 && DateTime.UtcNow < pluginDeadline)
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Check("独立插件通过教师端向教室发送", shell.Deliveries.Any(item => item.Text == "ClassShout 示例插件已启动。"));
            while (shell.Deliveries.FirstOrDefault(item => item.Text == "ClassShout 示例插件已启动。")?.Receipt.Stage != "displayed" && DateTime.UtcNow < pluginDeadline)
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Check("插件消息获得真实上屏回执", shell.Deliveries.FirstOrDefault(item => item.Text == "ClassShout 示例插件已启动。")?.Receipt.Stage == "displayed");
        }
        catch (Exception ex) { Check($"运行异常：{ex.Message}", false); }
        finally { Pump(shell.DisposeAsync().AsTask()); }
        Console.WriteLine($"教师端扩展验收失败项：{failures}");
        return failures == 0 ? 0 : 1;
    }
}
