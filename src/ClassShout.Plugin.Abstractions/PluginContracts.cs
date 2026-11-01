using Microsoft.Extensions.DependencyInjection;

namespace ClassShout.Plugin.Abstractions;

/// <summary>无参模块只负责注册服务，真正的插件实例由所属 DI 容器构造。</summary>
public interface IPluginModule
{
    void ConfigureServices(IServiceCollection services);
}

public interface IClassShoutPlugin
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IPluginContext
{
    string Id { get; }
    string Role { get; }
    string DataDirectory { get; }
}

public interface IPluginLog
{
    void Information(string message);
}

public sealed record PluginHostStatus(string Role, bool Connected, string Classroom, bool? Muted, int? Volume);
public sealed record PluginSendResult(bool Accepted, bool Duplicate, string? Error);
public sealed record PluginDeliveryEvent(string Stage, DateTimeOffset At, string? Detail);
public sealed record PluginReceipt(string Id, string Stage, string? Failure, IReadOnlyList<PluginDeliveryEvent> Timeline);

/// <summary>插件的类型化宿主服务，与 HTTP IPC 无关，不暴露服务器和登录凭据。</summary>
public interface IPluginShoutService
{
    Task<PluginHostStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<PluginSendResult> SendAsync(string id, string text, bool speak = false, CancellationToken cancellationToken = default);
    Task<PluginReceipt?> GetReceiptAsync(string id, CancellationToken cancellationToken = default);
}
