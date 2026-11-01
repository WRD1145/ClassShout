using ClassShout.Plugin.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ClassShout.Examples.HelloPlugin;

public sealed class PluginModule : IPluginModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<GreetingService>();
        services.AddScoped<IClassShoutPlugin, HelloPlugin>();
    }
}

public sealed class GreetingService(IPluginShoutService shouts, IPluginLog log)
{
    public async Task SendAsync(CancellationToken token)
    {
        var result = await shouts.SendAsync(Guid.NewGuid().ToString("N"), "ClassShout 示例插件已启动。", false, token);
        log.Information(result.Accepted ? "示例消息已提交，请通过回执确认执行。" : "示例消息未提交，请检查教室连接。");
    }
}

public sealed class HelloPlugin(GreetingService greeting) : IClassShoutPlugin
{
    public async ValueTask StartAsync(CancellationToken cancellationToken) => await greeting.SendAsync(cancellationToken);
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
