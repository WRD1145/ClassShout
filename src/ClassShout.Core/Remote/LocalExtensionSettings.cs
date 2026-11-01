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
    public string Id { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public bool Enabled { get; set; }
}
