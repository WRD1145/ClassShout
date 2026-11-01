namespace ClassShout.PluginFixture.Dependency;

public static class DependencyInfo
{
    public static string Version => typeof(DependencyInfo).Assembly.GetName().Version!.ToString();
}
