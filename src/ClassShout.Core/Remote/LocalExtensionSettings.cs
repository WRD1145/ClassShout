using System.Security.Cryptography;

namespace ClassShout.Core.Remote;

/// <summary>本机扩展默认关闭；安装插件不等于允许它执行。</summary>
public sealed class LocalExtensionSettings
{
    public bool IpcEnabled { get; set; }
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public List<ProcessPluginSettings> Plugins { get; set; } = [];
}

public sealed class ProcessPluginSettings
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public bool ManagedPackage { get; set; }
    public string Runner { get; set; } = "native";
    public string EntryPoint { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string Id { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public bool Enabled { get; set; }
}
