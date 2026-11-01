using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using ClassShout.Core.Net;
using ClassShout.Plugin.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ClassShout.Core.Remote;

public interface IPluginRuntime : IAsyncDisposable
{
    bool IsRunning { get; }
    bool IsStarting { get; }
    bool RequiresRestart { get; }
    Task StartAsync();
}

/// <summary>每个插件独享可回收加载上下文、服务容器和作用域；程序集隔离不是安全沙箱。</summary>
public sealed class AssemblyPluginRuntime : IPluginRuntime
{
    private readonly string _assemblyPath;
    private readonly string _entryType;
    private readonly PluginContext _context;
    private readonly PluginLog _log;
    private readonly PluginShoutService _shouts;
    private readonly CancellationTokenSource _lifetime = new();
    private PluginLoadContext? _loadContext;
    private ServiceProvider? _provider;
    private AsyncServiceScope _scope;
    private bool _hasScope;
    private IClassShoutPlugin? _plugin;
    private Task? _startup;
    private Task? _shutdown;
    private volatile bool _running;
    private volatile bool _starting;
    private bool _started;
    private bool _disposed;
    private readonly object _shutdownGate = new();
    public bool IsRunning => _running;
    public bool IsStarting => _starting;
    public bool RequiresRestart { get; private set; }
    public WeakReference? UnloadReference { get; private set; }

    public AssemblyPluginRuntime(string assemblyPath, string entryType, string id, string role, string dataDirectory,
        Func<string, string, string, Task<IpcResponse>> dispatch, Action<string> log)
    {
        _assemblyPath = Path.GetFullPath(assemblyPath); _entryType = entryType;
        _context = new(id, role, dataDirectory); _log = new(log); _shouts = new(role, dispatch);
    }

    public async Task StartAsync()
    {
        if (_started) throw new InvalidOperationException("插件实例已经启动过。");
        _started = true;
        _starting = true;
        _startup = Task.Run(async () =>
        {
            _loadContext = new PluginLoadContext(_assemblyPath);
            UnloadReference = new WeakReference(_loadContext);
            var assembly = _loadContext.LoadMain(_assemblyPath);
            var type = assembly.GetType(_entryType, true)!;
            if (!typeof(IPluginModule).IsAssignableFrom(type) || type.IsAbstract || !type.IsPublic || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("插件入口必须是公开、可构造的 IPluginModule。");
            var services = new ServiceCollection();
            services.AddSingleton<IPluginContext>(_context);
            services.AddSingleton<IPluginLog>(_log);
            services.AddSingleton<IPluginShoutService>(_shouts);
            var reserved = services.ToArray();
            var module = (IPluginModule)Activator.CreateInstance(type)!;
            module.ConfigureServices(services);
            if (reserved.Any(descriptor => services.Count(item => item.ServiceType == descriptor.ServiceType) != 1 || !services.Contains(descriptor)))
                throw new InvalidDataException("插件不能替换宿主提供的契约服务。");
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            _scope = _provider.CreateAsyncScope(); _hasScope = true;
            _plugin = _scope.ServiceProvider.GetRequiredService<IClassShoutPlugin>();
            await _plugin.StartAsync(_lifetime.Token).ConfigureAwait(false);
            _running = true;
        });
        try { await _startup.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { await _lifetime.CancelAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (Exception) { }
            RequiresRestart = true; throw new InvalidOperationException("插件启动超时，已请求取消；请禁用插件，无法释放时重启应用。");
        }
        catch { _running = false; await DisposeAsync().ConfigureAwait(false); throw; }
        finally { _starting = false; if (_startup?.IsCompleted == true) _startup = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await _lifetime.CancelAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        catch (TimeoutException) { RequiresRestart = true; throw new InvalidOperationException("插件取消回调未完成，需重启应用才能释放程序集。"); }
        catch (Exception) { _log.Information("插件取消回调失败，继续尝试释放服务。"); }
        if (_startup is not null && !_startup.IsCompleted)
        {
            try { await _startup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { RequiresRestart = true; throw new InvalidOperationException("插件未响应取消，需重启应用才能释放程序集。"); }
            catch (Exception) { }
        }
        Task shutdown;
        lock (_shutdownGate) shutdown = _shutdown ??= Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                if (_plugin is not null)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _plugin.StopAsync(deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { failure = ex; }
            try
            {
                if (_hasScope)
                {
                    try { await _scope.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { failure ??= ex; }
                    finally { _hasScope = false; _scope = default; }
                }
                if (_provider is not null)
                {
                    try { await _provider.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { failure ??= ex; }
                }
            }
            finally
            {
                _plugin = null; _provider = null; _running = false;
                _loadContext?.Unload(); _loadContext = null;
                if (failure is not null) _log.Information("插件停止或服务释放失败，已清除宿主引用，请重启应用完成清理。");
            }
            if (failure is not null) { RequiresRestart = true; throw new InvalidOperationException("插件停止或服务释放失败，需重启应用完成清理。"); }
        });
        try
        {
            await shutdown.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            _disposed = true; RequiresRestart = false; _startup = null; _shutdown = null;
            _lifetime.Dispose();
        }
        catch (TimeoutException) { RequiresRestart = true; throw new InvalidOperationException("插件停止超时，需重启应用才能释放程序集。"); }
    }

    private sealed record PluginContext(string Id, string Role, string DataDirectory) : IPluginContext;
    private sealed class PluginLog(Action<string> write) : IPluginLog
    {
        public void Information(string message) => write(message.Length > 2048 ? message[..2048] : message);
    }
    private sealed class PluginShoutService(string role, Func<string, string, string, Task<IpcResponse>> dispatch) : IPluginShoutService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static JsonElement Body(IpcResponse response) => JsonSerializer.SerializeToElement(response.Body, Json);
        public async Task<PluginHostStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var response = await dispatch("GET", "/v1/status", "{}").WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response.Status != 200) throw new InvalidOperationException("宿主状态暂不可用。");
            var body = Body(response);
            return new(role, role == "classroom" || body.GetProperty("connected").GetBoolean(), body.GetProperty("classroom").GetString() ?? "",
                body.TryGetProperty("muted", out var muted) ? muted.GetBoolean() : null,
                body.TryGetProperty("volume", out var volume) ? volume.GetInt32() : null);
        }
        public async Task<PluginSendResult> SendAsync(string id, string text, bool speak = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await dispatch("POST", "/v1/shouts", JsonSerializer.Serialize(new { id, text, speak }, Json)).WaitAsync(cancellationToken).ConfigureAwait(false);
            var body = Body(response);
            return new(response.Status is 200 or 202, body.TryGetProperty("duplicate", out var duplicate) && duplicate.GetBoolean(),
                body.TryGetProperty("error", out var error) ? error.GetString() : null);
        }
        public async Task<PluginReceipt?> GetReceiptAsync(string id, CancellationToken cancellationToken = default)
        {
            var response = await dispatch("GET", "/v1/receipts/" + Uri.EscapeDataString(id), "{}").WaitAsync(cancellationToken).ConfigureAwait(false);
            return response.Status is 404 or 410 ? null : response.Status == 200
                ? Body(response).Deserialize<PluginReceipt>(Json) : throw new InvalidOperationException("宿主回执暂不可用。");
        }
    }
}

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _root;
    private static readonly Assembly[] Shared = [typeof(IPluginModule).Assembly, typeof(IServiceCollection).Assembly];
    public PluginLoadContext(string path) : base("ClassShout 插件：" + Guid.NewGuid().ToString("N"), true)
    { _resolver = new AssemblyDependencyResolver(path); _root = Path.GetDirectoryName(path)!; }
    public Assembly LoadMain(string path) { using var stream = File.OpenRead(path); return LoadFromStream(stream); }
    protected override Assembly? Load(AssemblyName name)
    {
        var shared = Shared.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        if (shared is not null)
        {
            if (name.Version?.Major != shared.GetName().Version?.Major) throw new FileLoadException("插件契约主版本不兼容。");
            return shared;
        }
        // 框架契约使用宿主副本，私有依赖只从各插件自己的包目录解析。
        if (name.Name is null || name.Name == "netstandard" || name.Name == "mscorlib" || name.Name == "Microsoft.CSharp" || name.Name.StartsWith("Microsoft.Win32.") || name.Name.StartsWith("System.") || name.Name == "System") return null;
        var path = _resolver.ResolveAssemblyToPath(name) ?? Path.Combine(_root, name.Name + ".dll");
        if (!File.Exists(path)) throw new FileNotFoundException("插件私有依赖缺失。");
        if (!Path.GetFullPath(path).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new FileLoadException("插件依赖超出安装目录。");
        using var stream = File.OpenRead(path); return LoadFromStream(stream);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        if (path is null) return 0;
        if (!Path.GetFullPath(path).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new FileLoadException("插件本机依赖超出安装目录。");
        return LoadUnmanagedDllFromPath(path);
    }
}
