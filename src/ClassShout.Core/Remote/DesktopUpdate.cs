using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassShout.Core.Remote;

public sealed record UpdateFile(string Path, string Sha256, bool Executable);
public sealed record DesktopUpdateManifest(int Format, string AppId, string Version, string Runtime, string EntryPoint, UpdateFile[] Files);
public sealed record DesktopUpdateRequest(string Payload, string Target, string AppId, string Version, int ParentId,
    long ParentStartTicks, string ResultPath, bool Restart = true);

/// <summary>桌面更新包：先验证和暂存，再由暂存目录中的新程序等待旧进程退出后替换。</summary>
public static class DesktopUpdate
{
    public const string ManifestName = "classshout-update.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Protected = ["logs", "plugins", "plugin-data", "runtime", "config", "data"];
    private static readonly string[] Settings = ["bells.json", "appearance.json", "classroom.json", "teacher.json", "updates.json", "relay-config.json", "relay-users.json"];
    public static string Runtime => $"{(OperatingSystem.IsWindows() ? "win" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    public static string AssetName(string appId) => $"ClassShout.{(appId == "classroom" ? "Classroom" : "Teacher")}-{Runtime}-update.zip";
    public static string ResultPath => Path.Combine(LocalSettings.Directory, "desktop-update-result.txt");

    private static void WriteResult(string path, string message)
    {
        try { File.WriteAllText(path, message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("无法写入更新结果：" + ex.Message); }
    }

    public static void CleanupOldStages()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), "classshout-update-*"))
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-7)
                    && !File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)
                    && !Directory.EnumerateDirectories(folder, "backup-*").Any()) Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 缓存被占用不影响启动。 */ }
    }

    public static string? InstallationHint(string directory)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE")))
            return "AppImage 请下载新版 AppImage 并替换旧文件。";
        if (File.Exists(Path.Combine(directory, "classshout-package-manager")))
            return "此版本由系统包管理器安装，请下载新版 deb/rpm，通过包管理器更新。";
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64) || (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()))
            return "当前平台没有可用的桌面更新包，请从发行版页面下载。";
        return null;
    }

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.StartsWith('/') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("更新包包含非法路径。");
        var parts = relative.Split('/');
        if (parts.Any(part => part.EndsWith('.') || part.EndsWith(' ')
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            || (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !relative.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                && !relative.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                && relative != ManifestName))
            throw new InvalidDataException("更新包不能包含设置文件或有歧义的路径。");
        if (Protected.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
            || Settings.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包不能覆盖用户配置、日志或插件。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var current = new DirectoryInfo(root);
        while (current is not null)
        {
            if (current.Exists && current.LinkTarget is not null) throw new InvalidDataException("更新目录不能经过符号链接。");
            current = current.Parent;
        }
        var cursor = root;
        foreach (var part in parts)
        {
            cursor = Path.Combine(cursor, part);
            if (new FileInfo(cursor).LinkTarget is not null
                || ((File.Exists(cursor) || Directory.Exists(cursor)) && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidDataException("更新路径不能经过符号链接。");
        }
        return full;
    }

    public static DesktopUpdateManifest ReadManifest(string directory, string appId, string version)
    {
        var path = Path.Combine(directory, ManifestName);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("更新清单过大。");
        var manifest = JsonSerializer.Deserialize<DesktopUpdateManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("更新清单为空。");
        var entry = appId switch { "classroom" => "ClassShout.Classroom", "teacher" => "ClassShout.Teacher.Desktop", _ => throw new InvalidDataException("未知应用。") };
        if (OperatingSystem.IsWindows()) entry += ".exe";
        if (manifest.Format != 1 || manifest.AppId != appId || manifest.Version != version || manifest.Runtime != Runtime
            || !Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+\.\d+$") || manifest.EntryPoint != entry
            || manifest.Files is not { Length: > 0 and <= 4000 })
            throw new InvalidDataException("更新包的应用、版本、架构或格式不匹配。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!names.Add(file.Path) || file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(file.Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("更新清单文件重复或校验值无效。");
            var filePath = SafePath(directory, file.Path);
            using var stream = File.OpenRead(filePath);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"更新文件校验失败：{file.Path}");
        }
        if (!names.Contains(entry)) throw new InvalidDataException("更新包缺少程序入口。");
        return manifest;
    }

    public static DesktopUpdateManifest Extract(string archive, string destination, string appId, string version)
    {
        if (Directory.Exists(destination)) throw new IOException("更新暂存目录已存在。");
        SafePath(Path.GetDirectoryName(Path.GetFullPath(destination))!, Path.GetFileName(destination));
        Directory.CreateDirectory(destination);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count is 0 or > 4001) throw new InvalidDataException("更新包文件数量异常。");
            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in zip.Entries)
            {
                if (!names.Add(item.FullName) || item.FullName.EndsWith('/') || ((item.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("更新包包含重复路径、目录或符号链接。");
                total = checked(total + item.Length);
                if (total > 1024L * 1024 * 1024 || item.Length > 256L * 1024 * 1024) throw new InvalidDataException("更新包解压大小超过限制。");
                var path = SafePath(destination, item.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                item.ExtractToFile(path);
            }
            var manifest = ReadManifest(destination, appId, version);
            if (names.Count != manifest.Files.Length + 1 || !names.Contains(ManifestName)) throw new InvalidDataException("更新包包含未登记文件。");
            if (!OperatingSystem.IsWindows())
                foreach (var file in manifest.Files.Where(file => file.Executable))
                    File.SetUnixFileMode(SafePath(destination, file.Path), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return manifest;
        }
        catch { Directory.Delete(destination, true); throw; }
    }

    public static void Launch(string payload, string target, string appId, string version)
    {
        var manifest = ReadManifest(payload, appId, version);
        if (InstallationHint(target) is { } hint) throw new IOException(hint);
        // 在关闭当前程序之前试写目录，权限不足时仍可在界面说明原因。
        var probe = Path.Combine(target, ".classshout-write-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, ""); File.Delete(probe);
        using var parent = Process.GetCurrentProcess();
        var request = new DesktopUpdateRequest(payload, Path.GetFullPath(target), appId, version, parent.Id,
            parent.StartTime.ToUniversalTime().Ticks, ResultPath);
        var requestPath = Path.Combine(Path.GetDirectoryName(payload)!, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, Json));
        var start = new ProcessStartInfo(SafePath(payload, manifest.EntryPoint)) { UseShellExecute = false, WorkingDirectory = payload };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新进程。");
        for (var count = 0; count < 100; count++)
        {
            if (File.Exists(requestPath + ".ready")) return;
            if (process.HasExited) throw new IOException("更新进程启动失败，请查看更新结果日志。");
            Thread.Sleep(100);
        }
        if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); }
        throw new IOException("更新进程未就绪，当前程序保持运行。");
    }

    public static bool TryHandleArguments(string[] args, string appId, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        DesktopUpdateRequest? request = null;
        try
        {
            if (args.Length != 2 || new FileInfo(args[1]).Length > 65536) throw new InvalidDataException("更新请求无效。");
            request = JsonSerializer.Deserialize<DesktopUpdateRequest>(File.ReadAllText(args[1]), Json) ?? throw new InvalidDataException("更新请求为空。");
            if (request.AppId != appId) throw new InvalidDataException("更新请求应用不匹配。");
            ReadManifest(request.Payload, appId, request.Version);
            File.WriteAllText(args[1] + ".ready", "更新进程已就绪。");
            if (request.ParentId > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(request.ParentId);
                    if (parent.StartTime.ToUniversalTime().Ticks == request.ParentStartTicks && !parent.WaitForExit(60000))
                        throw new IOException("旧进程未退出，更新已取消。");
                }
                catch (ArgumentException) { /* 旧进程已经退出。 */ }
            }
            Apply(request);
            WriteResult(request.ResultPath, $"更新成功：v{request.Version}");
            if (request.Restart) Process.Start(new ProcessStartInfo(Path.Combine(request.Target, ReadManifest(request.Payload, appId, request.Version).EntryPoint))
                { UseShellExecute = true, WorkingDirectory = request.Target })?.Dispose();
        }
        catch (Exception ex)
        {
            exitCode = 1;
            var message = "更新失败：" + ex.Message;
            Console.Error.WriteLine(message);
            if (request is not null)
            {
                WriteResult(request.ResultPath, message);
                if (request.Restart)
                {
                    var entry = appId == "classroom" ? "ClassShout.Classroom" : "ClassShout.Teacher.Desktop";
                    if (OperatingSystem.IsWindows()) entry += ".exe";
                    try { Process.Start(new ProcessStartInfo(Path.Combine(request.Target, entry)) { UseShellExecute = true, WorkingDirectory = request.Target })?.Dispose(); }
                    catch (Exception restartError) { Console.Error.WriteLine("恢复启动失败：" + restartError.Message); }
                }
            }
        }
        return true;
    }

    /// <summary>仅覆盖清单文件；失败回滚已替换的文件，用户数据始终不在更新清单中。</summary>
    public static void Apply(DesktopUpdateRequest request, Action<int>? afterReplace = null)
    {
        var manifest = ReadManifest(request.Payload, request.AppId, request.Version);
        var target = Path.GetFullPath(request.Target);
        if (!Directory.Exists(target) || Path.GetFullPath(request.Payload).StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("更新目录无效或暂存目录位于程序目录内。");
        if (!File.Exists(SafePath(target, manifest.EntryPoint))) throw new IOException("目标不是该应用的现有安装目录。");
        if (File.Exists(Path.Combine(target, "classshout-package-manager"))) throw new IOException("系统安装包应通过包管理器更新。");
        var backup = Path.Combine(Path.GetDirectoryName(request.Payload)!, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var changed = new List<(string Path, string? Backup)>();
        var canRemoveBackup = false;
        try
        {
            foreach (var file in manifest.Files)
            {
                var path = SafePath(target, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string? old = null;
                if (File.Exists(path))
                {
                    old = SafePath(backup, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                    File.Copy(path, old);
                }
                changed.Add((path, old));
                File.Copy(SafePath(request.Payload, file.Path), path, true);
                if (!OperatingSystem.IsWindows() && file.Executable)
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                afterReplace?.Invoke(changed.Count);
            }
            canRemoveBackup = true;
        }
        catch (Exception updateError)
        {
            try
            {
                foreach (var (path, old) in changed.AsEnumerable().Reverse())
                {
                    if (old is null) File.Delete(path); else File.Copy(old, path, true);
                }
                canRemoveBackup = true;
            }
            catch (Exception rollbackError) { throw new AggregateException($"更新及回滚失败，旧文件备份保留在：{backup}", updateError, rollbackError); }
            throw;
        }
        finally
        {
            if (canRemoveBackup)
                try { Directory.Delete(backup, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
