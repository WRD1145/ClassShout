using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClassShout.Core.Net;
using ClassShout.Core.Remote;

namespace ClassShout.EndToEnd;

internal static partial class Program
{
    private static byte[] BuildPluginPackage(PluginManifest manifest, Dictionary<string, string>? extra = null, bool link = false)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name);
                if (link && name == "link") entry.ExternalAttributes = unchecked((int)0xA0000000);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text);
            }
            Add("manifest.json", JsonSerializer.Serialize(manifest));
            var fixture = manifest.EntryPoint.Contains(".B.") ? "B" : "A";
            var folder = Path.GetFullPath($"../../../../../tools/ClassShout.PluginFixture.{fixture}/bin/Release/net10.0", AppContext.BaseDirectory);
            foreach (var file in new[] { manifest.EntryPoint, Path.ChangeExtension(manifest.EntryPoint, ".deps.json"), "ClassShout.PluginFixture.Dependency.dll" })
            {
                using var output = zip.CreateEntry(file).Open();
                using var input = File.OpenRead(Path.Combine(folder, file)); input.CopyTo(output);
            }
            foreach (var entry in extra ?? []) Add(entry.Key, entry.Value);
        }
        return bytes.ToArray();
    }

    private static async Task<int> AssertPluginManagementAsync()
    {
        var previous = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
        var root = Path.Combine(Path.GetTempPath(), "ClassShout-plugin-manager-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", root);
        try
        {
            var legacy = new LocalExtensionSettings { IpcEnabled = true, Plugins = [new() { Id = "legacy", Executable = Environment.ProcessPath!, Enabled = false }] };
            LocalSettings.Save("teacher-extensions.json", legacy);
            var ipc = SeparatedExtensionSettings.LoadIpc("teacher");
            Check("配置分离：迁移保留原 IPC 令牌与开关", ipc.Token == legacy.Token && ipc.IpcEnabled, "新文件 teacher-ipc.json");
            var allowed = true;
            await using var manager = new PluginManager("teacher", (_, _, _, _) => Task.FromResult(new IpcResponse(200, new { connected = true, classroom = "验收教室", muted = false, volume = 80 })), _ => { }, action => action(), () => allowed);
            Check("配置分离：旧插件注册独立迁移", manager.Items.Single().Id == "legacy" && File.Exists(Path.Combine(root, "teacher-plugins.json")), "原配置保留备查");
            var ipcBytes = File.ReadAllBytes(Path.Combine(root, "teacher-ipc.json"));
            await manager.UninstallAsync("legacy");
            Check("卸载旧注册：不删除外部可执行文件", File.Exists(Environment.ProcessPath!), "只移除注册");
            var marker = Path.Combine(root, "plugin-data", "teacher", "test.plugin", "state.txt");
            var manifest = new PluginManifest { Id = "test.plugin", Name = "测试插件", Version = "1.0.0", EntryPoint = "ClassShout.PluginFixture.A.dll", EntryType = "ClassShout.PluginFixture.PluginModule" };
            byte[] Package(Dictionary<string, string>? extra = null, bool link = false) => BuildPluginPackage(manifest, extra, link);
            bool Reject(byte[] bytes, string role = "teacher")
            {
                try { PluginPackage.Read(bytes, role); return false; }
                catch (Exception ex) when (ex is InvalidDataException or JsonException) { return true; }
            }
            Check("插件包安全：拒绝路径越界", Reject(Package(new() { ["../outside.txt"] = "禁止写入" })), "解压前拒绝");
            Check("插件包安全：拒绝绝对路径及盘符", Reject(Package(new() { ["C:/escape.txt"] = "禁止写入" })) && Reject(Package(new() { ["/escape.txt"] = "禁止写入" })), "跨平台检查");
            Check("插件包安全：拒绝链接文件", Reject(Package(new() { ["link"] = "../../outside" }, true)), "不跟随符号链接");
            Check("插件包安全：拒绝大小写冲突及设备名", Reject(Package(new() { [manifest.EntryPoint.ToUpperInvariant()] = "冲突" })) && Reject(Package(new() { ["CON"] = "设备路径" })), "便携路径规则");
            Check("插件包安全：拒绝过深目录", Reject(Package(new() { [string.Join('/', Enumerable.Repeat("deep", 18)) + "/x"] = "过深" })), "限制路径层级");
            var savedHosts = manifest.Hosts;
            manifest.Hosts = ["classroom"];
            Check("插件兼容：拒绝错误适用端", Reject(Package()), "教师端不安装教室专用包");
            manifest.Hosts = savedHosts;
            manifest.ApiVersion = 99;
            Check("插件兼容：拒绝未知 API 版本", Reject(Package()), "不尝试运行");
            manifest.ApiVersion = 1;
            Check("插件包安全：拒绝超限压缩包", Reject(new byte[PluginPackage.MaxBytes + 1]), "32 MiB 上限");
            var valid = PluginPackage.Read(Package(), "teacher");
            valid.Manifest.EntryPoint = "../../outside";
            await manager.InstallAsync(valid);
            Check("插件包安全：安装重新校验原始包", File.Exists(Path.Combine(manager.DirectoryPath, manifest.Id, manifest.EntryPoint)) && !File.Exists(Path.Combine(root, "outside")), "防止预览对象被修改");
            Check("插件安装：默认禁用且未运行", manager.Items.Single() is { Enabled: false, Running: false } && !File.Exists(marker), "安装不执行代码");
            await manager.SetEnabledAsync(manifest.Id, true);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(marker) && DateTime.UtcNow < until) await Task.Delay(50);
            Check("插件启用：DI 构造实例并调用类型化宿主服务", File.Exists(marker) && manager.Items.Single().Running, "独立程序集加载上下文");
            manifest.Id = "test.second"; manifest.EntryPoint = "ClassShout.PluginFixture.B.dll";
            await manager.InstallAsync(PluginPackage.Read(Package(), "teacher"));
            await manager.SetEnabledAsync(manifest.Id, true);
            var secondMarker = Path.Combine(root, "plugin-data", "teacher", manifest.Id, "state.txt");
            Check("程序集隔离：同名依赖的两个版本可同时使用", File.ReadAllText(marker).Contains("1.0.0.0") && File.ReadAllText(secondMarker).Contains("2.0.0.0") && manager.Items.All(item => item.Running), "两个真实插件及私有 DLL");
            Check("程序集隔离：私有依赖未进入默认加载上下文", !System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Any(assembly => assembly.GetName().Name == "ClassShout.PluginFixture.Dependency"), "契约共享、私有依赖隔离");
            await manager.UninstallAsync(manifest.Id);
            manifest.Id = "test.plugin"; manifest.EntryPoint = "ClassShout.PluginFixture.A.dll";
            allowed = false;
            var denied = false;
            try { await manager.SetEnabledAsync(manifest.Id, false); } catch (InvalidOperationException) { denied = true; }
            Check("插件安全：PIN 保护阻止管理操作", denied && manager.Items.Single().Running, "后端再次校验权限");
            allowed = true;
            await manager.SetEnabledAsync(manifest.Id, false);
            Check("插件禁用：实例停止并保存状态", !manager.Items.Single().Running && !LocalSettings.Load("teacher-plugins.json", static () => new PluginRegistry()).Plugins.Single().Enabled, "不是只改 UI 开关");
            Check("依赖注入：停止钩子和异步作用域均已释放", File.ReadAllText(marker).Contains("插件已停止") && File.ReadAllText(marker).Contains("作用域已释放"), "Scoped 服务真实 DisposeAsync");
            manifest.Id = "test.invalid";
            var originalEntryType = manifest.EntryType;
            manifest.EntryType = "Missing.PluginModule";
            await manager.InstallAsync(PluginPackage.Read(Package(), "teacher"));
            await manager.SetEnabledAsync(manifest.Id, true);
            Check("插件故障：无效入口不导致宿主退出", manager.Items.Single(item => item.Id == manifest.Id) is { Running: false, Failure: not null }, "初始化失败在管理器中明确显示");
            await manager.UninstallAsync(manifest.Id);
            manifest.Id = "test.plugin"; manifest.EntryType = originalEntryType;
            manifest.Version = "1.1.0";
            await manager.InstallAsync(PluginPackage.Read(Package(), "teacher"));
            Check("插件升级：更新元数据并保持禁用", manager.Items.Single() is { Version: "1.1.0", Enabled: false, Running: false }, "旧包替换");
            var lower = false;
            try { await manager.InstallAsync(PluginPackage.Read(Package(), "teacher")); } catch (InvalidDataException) { lower = true; }
            Check("插件升级：拒绝相同版本覆盖", lower && manager.Items.Single().Version == "1.1.0", "升级版本必须递增");
            var registryPath = Path.Combine(root, "teacher-plugins.json");
            var registryBytes = File.ReadAllBytes(registryPath);
            File.Delete(registryPath); Directory.CreateDirectory(registryPath);
            manifest.Version = "1.2.0";
            var failed = false;
            try { await manager.InstallAsync(PluginPackage.Read(Package(), "teacher")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check("插件升级：配置写入失败恢复旧包和注册", failed && manager.Items.Single().Version == "1.1.0" && File.Exists(Path.Combine(manager.DirectoryPath, manifest.Id, manifest.EntryPoint)), "真实文件系统故障回滚");
            Directory.Delete(registryPath); File.WriteAllBytes(registryPath, registryBytes);
            await manager.UninstallAsync(manifest.Id);
            Check("插件卸载：移除托管目录与注册", manager.Items.Count == 0 && !Directory.Exists(Path.Combine(manager.DirectoryPath, manifest.Id)), "限定本插件目录清理");
            Check("插件卸载：独立数据目录保留", File.Exists(marker), "升级与卸载不删除用户配置");
            Check("配置分离：插件全生命周期不修改 IPC", File.ReadAllBytes(Path.Combine(root, "teacher-ipc.json")).SequenceEqual(ipcBytes), "令牌和监听配置保持原样");
            Check("插件包安全：没有越界或暂存残留", !File.Exists(Path.Combine(root, "outside.txt")) && Directory.GetDirectories(manager.DirectoryPath).Length == 0, "安装、失败、卸载均核对");
            var weak = await RunCollectiblePluginFixtureAsync(root);
            for (var attempt = 0; attempt < 12 && weak.IsAlive; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(30); }
            Check("程序集卸载：停止后加载上下文实际回收", !weak.IsAlive, "WeakReference 与真实 GC 验证");
            var hungData = Path.Combine(root, "uncooperative"); Directory.CreateDirectory(hungData);
            File.WriteAllText(Path.Combine(hungData, "ignore-stop"), "验收夹具：模拟不响应停止");
            var fixture = Path.GetFullPath("../../../../../tools/ClassShout.PluginFixture.A/bin/Release/net10.0/ClassShout.PluginFixture.A.dll", AppContext.BaseDirectory);
            var hung = new AssemblyPluginRuntime(fixture, "ClassShout.PluginFixture.PluginModule", "hung", "teacher", hungData,
                (_, _, _) => Task.FromResult(new IpcResponse(200, new { connected = true, classroom = "验收教室" })), _ => { });
            await hung.StartAsync();
            var restartRequired = false;
            try { await hung.DisposeAsync(); } catch (InvalidOperationException) { restartRequired = true; }
            Check("程序集卸载：不响应停止时明确要求重启", restartRequired && hung.RequiresRestart && hung.UnloadReference!.IsAlive, "不伪装为已经完成卸载");
            var failedData = Path.Combine(root, "dispose-failed"); Directory.CreateDirectory(failedData);
            File.WriteAllText(Path.Combine(failedData, "fail-dispose"), "验收夹具：服务释放失败");
            var failedRuntime = new AssemblyPluginRuntime(fixture, "ClassShout.PluginFixture.PluginModule", "failed", "teacher", failedData,
                (_, _, _) => Task.FromResult(new IpcResponse(200, new { connected = true, classroom = "验收教室" })), _ => { });
            await failedRuntime.StartAsync();
            var cleanupFailure = false;
            try { await failedRuntime.DisposeAsync(); } catch (InvalidOperationException) { cleanupFailure = true; }
            Check("依赖注入：服务释放异常不伪装为正常卸载", cleanupFailure && failedRuntime.RequiresRestart && !failedRuntime.IsRunning, "继续释放根容器并明确提示重启");
            return _failed == 0 ? 0 : 1;
        }
        finally { Environment.SetEnvironmentVariable("CLASSSHOUT_DATA_DIR", previous); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunCollectiblePluginFixtureAsync(string root)
    {
        var path = Path.GetFullPath("../../../../../tools/ClassShout.PluginFixture.A/bin/Release/net10.0/ClassShout.PluginFixture.A.dll", AppContext.BaseDirectory);
        var runtime = new AssemblyPluginRuntime(path, "ClassShout.PluginFixture.PluginModule", "unload", "teacher", Path.Combine(root, "unload-data"),
            (_, _, _) => Task.FromResult(new IpcResponse(200, new { connected = true, classroom = "验收教室" })), _ => { });
        await runtime.StartAsync();
        var weak = runtime.UnloadReference!;
        await runtime.DisposeAsync();
        return weak;
    }
}
