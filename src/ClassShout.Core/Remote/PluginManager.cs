using System.Text.Json;
using ClassShout.Core.Net;

namespace ClassShout.Core.Remote;

public sealed class PluginRegistry
{
    public List<ProcessPluginSettings> Plugins { get; set; } = [];
}

public sealed record PluginListItem(string Id, string Name, string Version, string Author, string Description, bool Enabled, bool Running, bool ManagedPackage, bool CanManage, string? Failure, bool RequiresRestart, bool Starting)
{
    public string Status => RequiresRestart ? "需重启应用以释放程序集" : Starting ? "正在初始化服务" : Running ? "运行中" : Enabled ? Failure is null ? "已退出（不会自动重启）" : "启动失败：" + Failure : "已禁用";
    public string ToggleLabel => Enabled ? "禁用" : "启用";
    public string Source => ManagedPackage ? "托管程序集插件包" : "旧版进程兼容注册（卸载仅移除注册）";
}

/// <summary>独立于 IPC 的插件注册与进程生命周期；所有操作由所属端的 UI 线程串行执行。</summary>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly string _role;
    private readonly string _root;
    private readonly string _registryPath;
    private readonly Func<string, string, string, string, Task<IpcResponse>> _dispatch;
    private readonly Action<string> _log;
    private readonly Action<Action> _post;
    private readonly Func<bool> _canManage;
    private readonly Dictionary<string, IPluginRuntime> _hosts = [];
    private readonly Dictionary<string, string> _failures = [];
    private PluginRegistry _registry = new();
    private bool _usable = true;
    public event Action? Changed;
    public string Status { get; private set; } = "插件安装后默认禁用，启用后才运行。";
    public string DirectoryPath => _root;
    public string Role => _role;
    public IReadOnlyList<PluginListItem> Items => _registry.Plugins.Select(plugin => new PluginListItem(plugin.Id,
        string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name, plugin.Version, plugin.Author, plugin.Description,
        plugin.Enabled, _hosts.TryGetValue(plugin.Id, out var host) && host.IsRunning, plugin.ManagedPackage, _canManage(), _failures.GetValueOrDefault(plugin.Id), host?.RequiresRestart == true, host?.IsStarting == true)).ToArray();

    public PluginManager(string role, Func<string, string, string, string, Task<IpcResponse>> dispatch,
        Action<string> log, Action<Action> post, Func<bool> canManage)
    {
        if (role is not ("teacher" or "classroom")) throw new ArgumentException("插件所属端无效。", nameof(role));
        _role = role; _dispatch = dispatch; _log = log; _post = post; _canManage = canManage;
        _root = Path.GetFullPath(Path.Combine(LocalSettings.Directory, "plugins", role));
        _registryPath = Path.Combine(LocalSettings.Directory, SeparatedExtensionSettings.PluginsFile(role));
        try
        {
            if (File.Exists(_registryPath))
                _registry = JsonSerializer.Deserialize<PluginRegistry>(File.ReadAllText(_registryPath)) ?? throw new InvalidDataException();
            else
            {
                var legacy = LocalSettings.Load(SeparatedExtensionSettings.LegacyFile(role), static () => new LocalExtensionSettings());
                _registry.Plugins = (legacy.Plugins ?? []).Where(plugin => plugin is not null).Take(16).ToList();
                foreach (var plugin in _registry.Plugins) plugin.ManagedPackage = false;
                Save();
            }
            if (_registry.Plugins is null || _registry.Plugins.Count > 64 || _registry.Plugins.Any(plugin => plugin is null || !PluginPackage.ValidId(plugin.Id))
                || _registry.Plugins.Select(plugin => plugin.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _registry.Plugins.Count)
                throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { _usable = false; _registry = new(); Status = "插件配置读取或迁移失败，未启动插件；请检查配置格式及目录权限。"; }
    }

    private void Save()
    {
        var temp = _registryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _registryPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void EnsureEditable()
    {
        if (!_canManage()) throw new InvalidOperationException("请先解锁受保护的插件设置。");
        if (!_usable) throw new InvalidOperationException("插件配置尚未恢复，不能覆盖现有文件。");
    }
    private void Notify() => Changed?.Invoke();
    public void StartEnabled()
        => _ = StartAllEnabledAsync();

    private async Task StartAllEnabledAsync()
    {
        if (!_usable) { _log(Status); return; }
        foreach (var plugin in _registry.Plugins.Where(plugin => plugin.Enabled).ToArray()) await StartAsync(plugin);
        Notify();
    }
    private async Task StartAsync(ProcessPluginSettings plugin)
    {
        if (!plugin.Enabled || !_registry.Plugins.Contains(plugin)) return;
        if (_hosts.ContainsKey(plugin.Id)) return;
        _failures.Remove(plugin.Id);
        try
        {
            IPluginRuntime runtime;
            if (plugin.ManagedPackage)
            {
                var folder = OwnedDirectory(plugin.Id);
                EnsureNoLinks(folder);
                var entry = Path.GetFullPath(Path.Combine(folder, plugin.EntryPoint));
                if (!entry.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry)
                    || plugin.Runner != "assembly")
                    throw new InvalidDataException("插件入口不在所属目录中。");
                var data = Path.Combine(Path.GetDirectoryName(_registryPath)!, "plugin-data", _role, plugin.Id);
                EnsureNoLinks(data); Directory.CreateDirectory(data);
                runtime = new AssemblyPluginRuntime(entry, plugin.EntryType, plugin.Id, _role, data,
                    (method, path, body) => _dispatch(method, path, body, "plugin:" + plugin.Id + ":"), message => _log($"插件 {plugin.Id}：{message}"));
            }
            else
            {
                var process = new ProcessPluginHost(plugin, (method, path, body) => _dispatch(method, path, body, "plugin:" + plugin.Id + ":"),
                    message => _log($"旧版进程扩展 {plugin.Id}：{message}"));
                process.Stopped += () => _post(Notify);
                runtime = process;
            }
            _hosts.Add(plugin.Id, runtime);
            await runtime.StartAsync();
            if (_hosts.GetValueOrDefault(plugin.Id) == runtime && plugin.Enabled) _log($"插件已启动：{plugin.Id}。");
        }
        catch (Exception)
        {
            _failures[plugin.Id] = "请检查程序集、依赖和初始化服务。";
            Status = $"插件 {plugin.Id} 启动失败：{_failures[plugin.Id]}"; _log(Status);
        }
        Notify();
    }
    private async Task StopAsync(string id)
    {
        if (_hosts.TryGetValue(id, out var host))
        {
            await host.DisposeAsync();
            _hosts.Remove(id);
        }
    }
    public async Task SetEnabledAsync(string id, bool enabled)
    {
        EnsureEditable();
        var plugin = _registry.Plugins.Single(plugin => plugin.Id == id);
        var prior = plugin.Enabled;
        plugin.Enabled = enabled;
        try { Save(); } catch { plugin.Enabled = prior; throw; }
        await StopAsync(id);
        if (enabled) await StartAsync(plugin);
        else _failures.Remove(id);
        if (!_failures.ContainsKey(id)) Status = enabled ? "插件已设为启用；请查看运行状态。" : "插件已禁用并停止。";
        Notify();
    }
    public async Task RestartAsync(string id)
    {
        EnsureEditable();
        var plugin = _registry.Plugins.Single(plugin => plugin.Id == id);
        if (!plugin.Enabled) throw new InvalidOperationException("请先启用插件。");
        await StopAsync(id); await StartAsync(plugin); Notify();
    }
    private string OwnedDirectory(string id)
    {
        if (!PluginPackage.ValidId(id)) throw new InvalidDataException("插件标识无效。");
        var path = Path.GetFullPath(Path.Combine(_root, id));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件目录越界。");
        return path;
    }
    private static void EnsureNoLinks(string path)
    {
        // 检查每个祖先，防止将托管插件目录换成链接后删除或写入其他位置。
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("插件目录包含链接，已拒绝操作。");
        if (!Directory.Exists(path)) return;
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(path), 0));
        var count = 0;
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 32) throw new IOException("插件目录层级异常，已拒绝自动清理。");
            foreach (var item in current.Directory.EnumerateFileSystemInfos())
            {
                if (++count > 10000) throw new IOException("插件目录文件过多，已拒绝自动清理。");
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("插件目录包含链接，已拒绝操作。");
                if (item is DirectoryInfo child) pending.Push((child, current.Depth + 1));
            }
        }
    }
    private void DeleteOwned(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (!absolute.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("拒绝删除插件根目录以外的文件。");
        EnsureNoLinks(absolute);
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
    public async Task InstallAsync(PluginPackage package)
    {
        EnsureEditable();
        package = package.Revalidate(_role);
        var manifest = package.Manifest;
        var old = _registry.Plugins.SingleOrDefault(plugin => plugin.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (old is not null && (!old.ManagedPackage || old.Id != manifest.Id)) throw new InvalidDataException("旧版注册或不同大小写的标识已占用，请先卸载该注册。");
        if (old is null && _registry.Plugins.Count >= 64) throw new InvalidDataException("最多安装 64 个插件。");
        if (old is not null && System.Version.Parse(manifest.Version) <= System.Version.Parse(old.Version)) throw new InvalidDataException("升级版本必须高于已安装版本。");
        var target = OwnedDirectory(manifest.Id);
        var stage = OwnedDirectory(".install-" + Guid.NewGuid().ToString("N"));
        var backup = OwnedDirectory(".backup-" + Guid.NewGuid().ToString("N"));
        EnsureNoLinks(_root);
        Directory.CreateDirectory(stage);
        var movedOld = false;
        var movedNew = false;
        try
        {
            package.Extract(stage);
            var entry = Path.Combine(target, manifest.EntryPoint);
            var item = new ProcessPluginSettings { Id = manifest.Id, Name = manifest.Name, Version = manifest.Version, Author = manifest.Author,
                Description = manifest.Description, ManagedPackage = true, Enabled = false, Executable = entry,
                Runner = manifest.Runner, EntryPoint = manifest.EntryPoint, EntryType = manifest.EntryType, Arguments = [] };
            await StopAsync(manifest.Id);
            if (Directory.Exists(target))
            {
                if (old is null) throw new IOException("插件目标目录已存在，未覆盖未登记内容。");
                Directory.Move(target, backup); movedOld = true;
            }
            Directory.Move(stage, target); movedNew = true;
            if (old is not null) _registry.Plugins.Remove(old);
            _registry.Plugins.Add(item);
            try { Save(); }
            catch { _registry.Plugins.Remove(item); if (old is not null) _registry.Plugins.Add(old); throw; }
        }
        catch
        {
            if (movedNew) DeleteOwned(target);
            if (movedOld) Directory.Move(backup, target);
            if (old?.Enabled == true) await StartAsync(old);
            throw;
        }
        finally { DeleteOwned(stage); Notify(); }
        if (movedOld) DeleteOwned(backup);
        Status = "插件已安装，当前为禁用状态；审核来源后再启用。"; Notify();
    }
    private static string FindPowerShell()
    {
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var file = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
            if (File.Exists(file)) return file;
        }
        throw new InvalidDataException("此插件需要 PowerShell 7，请先安装并加入 PATH。");
    }
    public async Task UninstallAsync(string id)
    {
        EnsureEditable();
        var item = _registry.Plugins.Single(plugin => plugin.Id == id);
        var target = OwnedDirectory(id);
        if (item.ManagedPackage) EnsureNoLinks(target);
        var trash = OwnedDirectory(".remove-" + Guid.NewGuid().ToString("N"));
        await StopAsync(id);
        var moved = item.ManagedPackage && Directory.Exists(target);
        if (moved) Directory.Move(target, trash);
        _registry.Plugins.Remove(item);
        try { Save(); }
        catch
        {
            _registry.Plugins.Add(item);
            if (moved) Directory.Move(trash, target);
            if (item.Enabled) await StartAsync(item);
            throw;
        }
        if (moved) DeleteOwned(trash);
        Status = item.ManagedPackage ? "插件已停止并卸载。" : "旧版插件注册已移除，外部可执行文件未删除。"; Notify();
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _hosts.Keys.ToArray())
        {
            try { await StopAsync(id); }
            catch (Exception) { _log($"插件 {id} 未能完全释放，将随宿主退出。" ); }
        }
    }
}

public interface IPluginManagementContext
{
    bool SupportsPlugins { get; }
    bool CanManagePlugins { get; }
    PluginManager Plugins { get; }
}
