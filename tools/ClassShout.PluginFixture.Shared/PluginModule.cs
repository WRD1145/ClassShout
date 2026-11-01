using ClassShout.Plugin.Abstractions;
using ClassShout.PluginFixture.Dependency;
using Microsoft.Extensions.DependencyInjection;

namespace ClassShout.PluginFixture;

public sealed class PluginModule : IPluginModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<FixtureState>();
        services.AddScoped<IClassShoutPlugin, FixturePlugin>();
    }
}

public sealed class FixtureState(IPluginContext context) : IAsyncDisposable
{
    public string FilePath => Path.Combine(context.DataDirectory, "state.txt");
    public bool IgnoreStop => File.Exists(Path.Combine(context.DataDirectory, "ignore-stop"));
    public async Task StartAsync(CancellationToken token)
    {
        Directory.CreateDirectory(context.DataDirectory);
        await File.WriteAllTextAsync(FilePath, "start:" + DependencyInfo.Version + ":" + Guid.NewGuid().ToString("N"), token);
    }
    public ValueTask DisposeAsync()
    {
        File.AppendAllText(FilePath, "\n作用域已释放");
        if (File.Exists(Path.Combine(context.DataDirectory, "fail-dispose"))) throw new InvalidOperationException("验收夹具模拟服务释放失败。");
        return ValueTask.CompletedTask;
    }
}

public sealed class FixturePlugin(FixtureState state, IPluginShoutService shouts) : IClassShoutPlugin
{
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await shouts.GetStatusAsync(cancellationToken);
        await state.StartAsync(cancellationToken);
    }
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (state.IgnoreStop) return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan));
        File.AppendAllText(state.FilePath, "\n插件已停止");
        return ValueTask.CompletedTask;
    }
}
