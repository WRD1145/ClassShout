using System.Security.Cryptography;

namespace ClassShout.Core.Remote;

/// <summary>IPC 只保存监听开关和凭据，不包含插件清单。</summary>
public sealed class LocalIpcSettings
{
    public bool IpcEnabled { get; set; }
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

public static class SeparatedExtensionSettings
{
    public static string IpcFile(string role) => role == "teacher" ? "teacher-ipc.json" : "classroom-ipc.json";
    public static string PluginsFile(string role) => role == "teacher" ? "teacher-plugins.json" : "classroom-plugins.json";
    public static string LegacyFile(string role) => role == "teacher" ? "teacher-extensions.json" : "extensions.json";

    public static LocalIpcSettings LoadIpc(string role)
    {
        var file = IpcFile(role);
        if (File.Exists(Path.Combine(LocalSettings.Directory, file)))
            return LocalSettings.Load(file, static () => new LocalIpcSettings());
        var legacy = LocalSettings.Load(LegacyFile(role), static () => new LocalExtensionSettings());
        var settings = new LocalIpcSettings { IpcEnabled = legacy.IpcEnabled, Token = legacy.Token };
        // 保留旧文件以便恢复；新文件存在后不再从旧开关覆盖新设置。
        LocalSettings.Save(file, settings);
        return settings;
    }
}
