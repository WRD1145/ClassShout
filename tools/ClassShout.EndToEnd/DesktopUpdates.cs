using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassShout.Core.Remote;

namespace ClassShout.EndToEnd;

internal static partial class Program
{
    private sealed class DesktopDownloadHandler(bool omitChecksum) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            string content;
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                content = new string('0', 64) + "  " + DesktopUpdate.AssetName("classroom");
            else if (url.EndsWith(".zip", StringComparison.Ordinal)) content = "模拟被篡改的下载内容";
            else
            {
                var assets = new List<object>
                {
                    new { name = DesktopUpdate.AssetName("classroom"), size = 100, browser_download_url = "https://update.invalid/" + DesktopUpdate.AssetName("classroom") },
                    new { name = "legacy.zip", size = 100, browser_download_url = "https://update.invalid/legacy.zip" },
                };
                if (!omitChecksum) assets.Add(new { name = "SHA256SUMS.txt", size = 100, browser_download_url = "https://update.invalid/SHA256SUMS.txt" });
                content = JsonSerializer.Serialize(new { tag_name = "v1.0.0.1", assets, html_url = "https://update.invalid/release" });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }

    private static async Task AssertDesktopUpdateCardAsync()
    {
        var previous = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
        var state = Path.Combine(Path.GetTempPath(), "cs-update-ui-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", state);
        try
        {
            foreach (var omit in new[] { false, true })
            {
                var handler = new DesktopDownloadHandler(omit);
                var browserOpened = false;
                var shutdown = false;
                var card = new ClassShout.Design.UpdateCardViewModel(null, new UpdateSettings(),
                    _ => { browserOpened = true; return Task.CompletedTask; }, settings => new UpdateChecker(settings, _ => handler))
                { DesktopAppId = "classroom", CurrentVersion = "1.0.0.0", PreferredAssetName = "legacy.zip", RequestShutdown = () => shutdown = true };
                await card.CheckAsync();
                Check("更新界面：优先选择专用更新包", card.DownloadButtonText == "下载更新并重启", "不再优先下载整份便携安装压缩包");
                await card.OpenDownloadAsync();
                Check(omit ? "更新界面：缺少校验清单时拒绝更新" : "更新界面：下载校验不符时拒绝更新",
                    !browserOpened && !shutdown && card.Status.Contains(omit ? "缺少" : "SHA-256", StringComparison.Ordinal), card.Status);
                Check("更新界面：失败不关闭当前程序", !card.IsBusy && !shutdown, "不会下载失败后仍退出");
                if (!omit) Check("更新界面：真正请求了更新附件", handler.Requested.Any(url => url.EndsWith("-update.zip", StringComparison.Ordinal)), "不是只更换按钮文案");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", previous);
            if (Directory.Exists(state)) Directory.Delete(state, true);
        }
    }

    private static void AssertDesktopUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entry = OperatingSystem.IsWindows() ? "ClassShout.Classroom.exe" : "ClassShout.Classroom";
        var files = new Dictionary<string, string> { [entry] = "新入口", ["ClassShout.Core.dll"] = "新程序" };
        string Archive(string name, Func<DesktopUpdateManifest, DesktopUpdateManifest>? alter = null, string? extra = null)
        {
            var manifest = new DesktopUpdateManifest(1, "classroom", "1.0.0.1", DesktopUpdate.Runtime, entry,
                files.Select(pair => new UpdateFile(pair.Key, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))), pair.Key == entry)).ToArray());
            var path = Path.Combine(root, name + ".zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var pair in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(pair.Key).Open());
                writer.Write(pair.Value);
            }
            using (var writer = new StreamWriter(archive.CreateEntry(DesktopUpdate.ManifestName).Open()))
                writer.Write(JsonSerializer.Serialize(alter?.Invoke(manifest) ?? manifest, JsonOptions));
            if (extra is not null)
            {
                var item = archive.CreateEntry(extra);
                if (extra == "link") item.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using var writer = new StreamWriter(item.Open()); writer.Write("不应解压");
            }
            return path;
        }
        void Reject(string name, string archive)
        {
            var target = Path.Combine(root, name);
            var rejected = false;
            try { DesktopUpdate.Extract(archive, target, "classroom", "1.0.0.1"); } catch (InvalidDataException) { rejected = true; }
            Check("更新包安全：" + name, rejected && !Directory.Exists(target), "失败时清理暂存目录");
        }
        try
        {
            var archive = Archive("valid");
            var payload = Path.Combine(root, "payload");
            var manifest = DesktopUpdate.Extract(archive, payload, "classroom", "1.0.0.1");
            Check("更新包：验证角色、版本与架构", manifest.Runtime == DesktopUpdate.Runtime && manifest.Files.Length == 2, "不从其他平台拿更新包");
            Reject("拒绝错误应用", Archive("wrong-app", m => m with { AppId = "teacher" }));
            Reject("拒绝错误版本", Archive("wrong-version", m => m with { Version = "9.0.0.0" }));
            Reject("拒绝错误架构", Archive("wrong-rid", m => m with { Runtime = DesktopUpdate.Runtime.EndsWith("arm64", StringComparison.Ordinal) ? DesktopUpdate.Runtime.Replace("-arm64", "-x64") : DesktopUpdate.Runtime.Replace("-x64", "-arm64") }));
            Reject("拒绝文件篡改", Archive("bad-hash", m => m with { Files = [m.Files[0] with { Sha256 = new string('0', 64) }, m.Files[1]] }));
            Reject("拒绝路径穿越", Archive("traversal", extra: "../outside"));
            Reject("拒绝反斜杠路径", Archive("backslash", extra: "..\\outside"));
            Reject("拒绝绝对路径", Archive("absolute", extra: "/outside"));
            Reject("拒绝设备文件名", Archive("device", extra: "CON.dll"));
            Reject("拒绝符号链接", Archive("symlink", extra: "link"));
            Reject("拒绝未登记文件", Archive("extra", extra: "unknown.dll"));
            Reject("拒绝覆盖配置", Archive("settings", extra: "custom-tts.json"));
            Reject("拒绝覆盖插件", Archive("plugins", extra: "plugins/test.dll"));
            Reject("拒绝大小写碰撞", Archive("collision", extra: entry.ToUpperInvariant()));
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, entry), "旧入口");
            File.WriteAllText(Path.Combine(target, "ClassShout.Core.dll"), "旧程序");
            File.WriteAllText(Path.Combine(target, "bells.json"), "本机铃声");
            var request = new DesktopUpdateRequest(payload, target, "classroom", "1.0.0.1", 0, 0, Path.Combine(root, "result"), false);
            var rolledBack = false;
            try { DesktopUpdate.Apply(request, _ => throw new IOException("模拟磁盘写入失败")); }
            catch (IOException) { rolledBack = true; }
            Check("更新事务：失败回滚旧程序", rolledBack && File.ReadAllText(Path.Combine(target, entry)) == "旧入口", "不会留下半个新入口");
            DesktopUpdate.Apply(request);
            Check("更新事务：真实替换程序文件", File.ReadAllText(Path.Combine(target, entry)) == "新入口" && File.ReadAllText(Path.Combine(target, "ClassShout.Core.dll")) == "新程序", "覆盖清单文件");
            Check("更新事务：保留本机配置", File.ReadAllText(Path.Combine(target, "bells.json")) == "本机铃声", "不改设置目录");
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(target, "ClassShout.Core.dll");
                var outside = Path.Combine(root, "outside.dll");
                File.Delete(link);
                File.CreateSymbolicLink(link, outside);
                var linkRejected = false;
                try { DesktopUpdate.Apply(request); } catch (InvalidDataException) { linkRejected = true; }
                Check("更新事务：拒绝目标中的悬空链接", linkRejected && !File.Exists(outside), "不沿链接创建目录外文件");
                File.Delete(link);
                File.WriteAllText(link, "新程序");
            }
            var start = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("pwsh", "-NoProfile -Command \"Start-Sleep -Seconds 30\"")
                : new System.Diagnostics.ProcessStartInfo("/bin/sleep", "30");
            start.UseShellExecute = false;
            using (var parent = System.Diagnostics.Process.Start(start)!)
            {
                var pending = request with { ParentId = parent.Id, ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks };
                var requestFile = Path.Combine(root, "waiting-request.json");
                File.WriteAllText(requestFile, JsonSerializer.Serialize(pending, JsonOptions));
                File.WriteAllText(Path.Combine(target, entry), "等待旧进程退出");
                var apply = Task.Run(() =>
                {
                    var handled = DesktopUpdate.TryHandleArguments(["--apply-update", requestFile], "classroom", out var code);
                    return handled && code == 0;
                });
                try
                {
                    for (var count = 0; count < 300 && !File.Exists(requestFile + ".ready"); count++) Thread.Sleep(10);
                    Check("更新进程：旧进程退出前不替换文件", File.Exists(requestFile + ".ready") && !apply.IsCompleted && File.ReadAllText(Path.Combine(target, entry)) == "等待旧进程退出", "等待真实进程而非固定延时");
                }
                finally { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); } }
                Check("更新进程：退出后才应用更新", apply.Wait(TimeSpan.FromSeconds(8)) && apply.Result
                    && File.ReadAllText(Path.Combine(target, entry)) == "新入口", "执行真实更新入口");
            }
            File.WriteAllText(Path.Combine(target, "classshout-package-manager"), "deb");
            var systemRejected = false;
            try { DesktopUpdate.Apply(request); } catch (IOException) { systemRejected = true; }
            Check("更新策略：不覆盖包管理器安装", systemRejected, "系统文件由 deb/rpm 管理");
            File.Delete(Path.Combine(target, "classshout-package-manager"));
            var backupKept = false;
            try
            {
                DesktopUpdate.Apply(request, _ =>
                {
                    File.Delete(Path.Combine(target, entry));
                    Directory.CreateDirectory(Path.Combine(target, entry));
                    throw new IOException("模拟替换失败且原路径变成目录");
                });
            }
            catch (AggregateException)
            {
                backupKept = Directory.EnumerateDirectories(root, "backup-*")
                    .Any(folder => File.Exists(Path.Combine(folder, entry)));
            }
            Check("更新事务：回滚受阻仍保留备份", backupKept, "不清掉最后一份旧文件");
        }
        finally { Directory.Delete(root, true); }
    }
}
