namespace ClassShout.RelayServer;

using System.Runtime.Versioning;

/// <summary>
/// 状态文件的原子写入。
///
/// 所有状态文件都是"先写 .tmp 再改名替换"，这样断电时最坏也只是丢掉一次写入，
/// 不会留下半个 JSON。但这里有一个容易忽略的副作用：
/// 新文件的权限来自进程的 umask，而不是被替换掉的那个文件。
///
/// 于是运维照文档把 relay-config.json 收紧成 600 之后，
/// 只要在 WebUI 里改一次口令，文件就会悄悄变回 umask 给的 644 ——
/// 里面存着明文口令，却变成同机所有账号都能读了，而且没有任何提示。
/// 所以替换之前，必须把原文件的模式位显式搬到新文件上。
/// </summary>
public static class AtomicStateFile
{
    /// <summary>
    /// 写内容并原子替换目标文件，保留目标原有的权限位。
    /// 目标不存在时按"仅宿主可读写"创建。
    /// </summary>
    public static void Write(string path, string content)
    {
        var temp = path + ".tmp";

        File.WriteAllText(temp, content);

        if (!OperatingSystem.IsWindows())
        {
            ApplyUnixMode(temp, path);
        }

        File.Move(temp, path, overwrite: true);
    }

    [UnsupportedOSPlatform("windows")]
    private static void ApplyUnixMode(string temp, string target)
    {
        try
        {
            // 目标已存在就沿用它的模式位，尊重运维做过的收紧；
            // 全新创建时默认只给宿主读写 —— 这些文件里有口令。
            var mode = File.Exists(target)
                ? File.GetUnixFileMode(target)
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;

            File.SetUnixFileMode(temp, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 改不动模式位不该让保存失败 —— 那会丢掉业务数据，
            // 比权限放宽严重得多。真正的权限故障由启动前体检负责发现。
        }
    }
}
