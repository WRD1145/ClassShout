using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassShout.Setup;

internal sealed class InstallationRecord
{
    public string AppId { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Installed { get; set; } = true;
    public List<string> Files { get; set; } = [];
}

public static partial class Program
{
    private const string RecordName = "classshout-install.json";
    internal static string? RequestedTarget;
    internal static bool GuiMode;
    internal static bool LaunchRequested;
    internal static bool SkipOsIntegration;
    private static bool IntegrationFailed;
    private static string CurrentAction = "install";
    internal static Func<Stream?>? TestPayload;
    internal static string? TestIntegrationRoot;
    internal static string? TestRegistryRoot;
    internal static void ConfigureTest(PayloadInfo info, Func<Stream?> payload) { Info = info; TestPayload = payload; SkipOsIntegration = true; GuiMode = true; LaunchRequested = false; }
    internal static PayloadInfo CurrentInfo => Info;
    internal static string SuggestedTarget => ExistingInstallDir() ?? DefaultInstallDir();
    internal static bool HasPayload { get { using var stream = OpenResource("payload.zip"); return stream is not null; } }
    private static void ValidateInfo()
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(Info.AppId, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(Info.Folder, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(Info.Exe, "^[a-zA-Z0-9][a-zA-Z0-9._-]*\\.exe$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(Info.Version, "^\\d+\\.\\d+\\.\\d+(\\.\\d+)?$") || SameVersion(Info.Version, "0.0.0"))
            throw new InvalidDataException("安装包信息缺失或版本无效，请重新下载完整安装包。");
    }
    private static string MaintenanceProgram => TestIntegrationRoot is { } root ? Path.Combine(root, "maintenance", Info.Version, "ClassShout.Setup.exe") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassShout", "Maintenance", Info.AppId, Info.Version, "ClassShout.Setup.exe");

    internal static bool SameVersion(string? a, string? b) => ParseVersion(a) is { } first && ParseVersion(b) is { } second && first == second;
    private static Version? ParseVersion(string? value) => Version.TryParse(value?.Split('+')[0], out var version)
        ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)) : null;
    internal static bool IsDowngrade(string? existing)
    {
        // 四段编号重置时，允许已登记的三段版本迁移到新编号。
        if (Info.Version.Count(c => c == '.') == 3 && existing?.Count(c => c == '.') == 2)
            return false;
        return ParseVersion(existing) is { } installed && ParseVersion(Info.Version) is { } incoming && installed > incoming;
    }
    private static void RejectDowngrade(string? existing)
    {
        if (IsDowngrade(existing)) throw new InvalidOperationException("已安装较新版本，请使用较新的安装包；不能用旧包覆盖。");
    }

    internal static int ExecuteAction(string command, bool quiet)
    {
        ValidateInfo();
        var target = ValidateTarget(RequestedTarget ?? ExistingInstallDir() ?? DefaultInstallDir());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..24];
        using var mutex = new Mutex(false, @"Local\ClassShout.Setup." + hash);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("另一安装程序正在操作此目录，请等待它完成。");
            RequestedTarget = target;
            CurrentAction = command; IntegrationFailed = false;
            return command switch
            {
                "install" or "setup" or "i" => Install(quiet),
                "update" or "u" => Update(quiet),
                "repair" => Repair(quiet),
                "uninstall" or "x" or "remove" => Uninstall(quiet),
                _ => Unsupported(command),
            };
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private static int Repair(bool quiet)
    {
        var target = RequestedTarget ?? ExistingInstallDir() ?? DefaultInstallDir();
        if (!SameVersion(ReadManagedVersion(target), Info.Version)) throw new InvalidOperationException("修复仅适用于与安装包相同的版本，请改用更新。");
        Console.WriteLine("正在修复本版程序文件和快捷方式，保留配置与日志。");
        return Update(quiet);
    }

    private static void NoLinks(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("安装路径包含链接或重解析点，不能安全操作。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("程序文件不能是链接。");
    }

    private static string ValidateTarget(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("请选择本机磁盘上的完整安装路径。");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var protectedRoots = new[] { Path.GetPathRoot(target), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassShout"), Path.TrimEndingDirectorySeparator(Path.GetTempPath()) };
        if (protectedRoots.Any(root => !string.IsNullOrWhiteSpace(root) && string.Equals(Path.TrimEndingDirectorySeparator(root), target, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("不能将磁盘根目录、个人文件夹或配置根目录作为安装目录。");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var maintenance = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassShout", "Maintenance");
        foreach (var root in new[] { windows, maintenance })
            if (target.Equals(root, StringComparison.OrdinalIgnoreCase) || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("该目录不能用作应用安装目录。");
        NoLinks(target);
        return target;
    }

    private static string OwnedFile(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length > 32 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("安装包文件路径无效。");
        if (parts[0].Equals("logs", StringComparison.OrdinalIgnoreCase) || parts[0].Equals(RecordName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包不能覆盖日志或安装记录。");
        foreach (var part in parts)
            if (System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException("安装包包含设备名。");
        var result = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包文件越界。");
        NoLinks(result);
        return result;
    }

    private static InstallationRecord? ReadRecord(string target, bool strict)
    {
        var file = Path.Combine(target, RecordName);
        if (!File.Exists(file)) return null;
        try
        {
            NoLinks(file);
            if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("安装记录过大。");
            var record = JsonSerializer.Deserialize<InstallationRecord>(File.ReadAllText(file)) ?? throw new InvalidDataException("安装记录为空。");
            if (record.AppId != Info.AppId || ParseVersion(record.Version) is null || record.Files is null || record.Files.Count > 4096 || record.Files.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("安装记录不属于当前应用或内容无效。");
            foreach (var relative in record.Files) _ = OwnedFile(target, relative);
            return record;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            if (strict) throw new InvalidDataException("安装记录损坏，不能安全卸载；请先修复安装。", ex);
            return null;
        }
    }

    internal static string? ReadManagedVersion(string target)
    {
        target = ValidateTarget(target);
        if (ReadRecord(target, false) is { } record) return record.Installed ? record.Version : null;
        if (!SkipOsIntegration)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallKeyRoot}\ClassShout-{Info.AppId}");
            if (key?.GetValue("InstallLocation") is string location && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
                return key.GetValue("DisplayVersion") as string ?? "未知";
        }
        var exe = Path.Combine(target, Info.Exe);
        if (File.Exists(exe))
        {
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            if (version.ProductName == "ClassShout") return version.FileVersion ?? "未知";
        }
        return null;
    }

    private static void SaveRecord(string target, InstallationRecord record)
    {
        var path = Path.Combine(target, RecordName);
        NoLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(record)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ExtractManagedPayload(string path)
    {
        var target = ValidateTarget(path);
        var previous = ReadRecord(target, false);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && previous is null && ReadManagedVersion(target) is null)
            throw new IOException("请选择空目录或此应用已有的安装目录，不能覆盖其他文件。");
        if (File.Exists(Path.Combine(target, RecordName)) && previous is null && ReadManagedVersion(target) is null)
            throw new IOException("无法确认安装目录归属。");
        using var payload = OpenResource("payload.zip") ?? throw new InvalidDataException("此安装程序未嵌入应用，不能执行安装或修复。");
        var stage = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClassShout-Setup-" + Guid.NewGuid().ToString("N")));
        var staged = Path.Combine(stage, "files");
        var backup = Path.Combine(stage, "backup");
        Directory.CreateDirectory(staged);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<(string Relative, bool Existed)>();
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0) continue;
                var relative = entry.FullName.Replace('/', '\\');
                if (!files.Add(relative) || files.Count > 4096 || entry.Length > 512L * 1024 * 1024 || (total += entry.Length) > 1024L * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException("安装包条目重复、包含链接或超过大小限制。");
                var file = OwnedFile(staged, relative);
                _ = OwnedFile(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var input = entry.Open();
                using var output = File.Create(file);
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    if ((written += read) > entry.Length) throw new InvalidDataException("安装包解压长度不一致。");
                    output.Write(buffer, 0, read);
                }
            }
            if (!files.Contains(Info.Exe)) throw new InvalidDataException("安装包缺少应用主程序。");
            if (!Directory.Exists(target)) createdDirectories.Add(target);
            Directory.CreateDirectory(target);
            foreach (var relative in files)
            {
                var destination = OwnedFile(target, relative);
                var existed = File.Exists(destination);
                if (existed)
                {
                    var copy = OwnedFile(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(destination, copy);
                }
                changed.Add((relative, existed));
                for (var parent = Path.GetDirectoryName(destination)!; parent.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)!)
                    createdDirectories.Add(parent);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(OwnedFile(staged, relative), destination, true);
            }
            SaveRecord(target, new InstallationRecord { AppId = Info.AppId, Version = Info.Version, Files = files.Union(previous?.Files ?? [], StringComparer.OrdinalIgnoreCase).ToList() });
            Console.WriteLine($"已写入 {files.Count} 个程序文件，配置和日志保留。");
            return true;
        }
        catch (Exception installError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = OwnedFile(target, item.Relative);
                    if (item.Existed)
                    {
                        var original = OwnedFile(backup, item.Relative);
                        // 被占用而未写入的文件无需再次覆盖；继续恢复其他已修改文件。
                        if (File.Exists(destination))
                        {
                            using var first = File.OpenRead(original); using var second = File.OpenRead(destination);
                            if (SHA256.HashData(first).SequenceEqual(SHA256.HashData(second))) continue;
                        }
                        File.Copy(original, destination, true);
                    }
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rollbackErrors.Add(ex); }
            }
            foreach (var directory in createdDirectories.OrderByDescending(item => item.Length))
            {
                try { NoLinks(directory); if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (IOException ex) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count > 0) throw new AggregateException("安装失败且部分文件无法恢复，请关闭程序后运行修复。", new[] { installError }.Concat(rollbackErrors));
            Console.WriteLine("安装未完成，已恢复本次修改的程序文件。");
            throw;
        }
        finally
        {
            // 只递归删除本次创建的唯一暂存目录，并重新核对绝对路径和链接。
            if (!string.Equals(Path.GetDirectoryName(stage), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(stage).StartsWith("ClassShout-Setup-", StringComparison.Ordinal))
                throw new IOException("暂存清理路径异常。");
            NoLinks(stage);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private static int RemoveManagedInstallation(string path)
    {
        var target = ValidateTarget(path);
        if (!Directory.Exists(target)) { if (!SkipOsIntegration) Unregister(); return IntegrationFailed ? 1 : 0; }
        var record = ReadRecord(target, true);
        if (record is null)
        {
            if (ReadManagedVersion(target) is null) throw new IOException("不能确认目录属于当前应用，已拒绝卸载。");
            using var payload = OpenResource("payload.zip") ?? throw new IOException("缺少旧安装的文件清单，请先运行安装包修复。");
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            record = new InstallationRecord { AppId = Info.AppId, Version = Info.Version, Files = zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName.Replace('/', '\\')).ToList() };
        }
        if (record.Files.Count > 4096) throw new InvalidDataException("卸载清单超过数量限制。");
        foreach (var relative in record.Files) _ = OwnedFile(target, relative);
        CloseRunningInstances(target);
        foreach (var relative in record.Files)
        {
            var file = OwnedFile(target, relative);
            if (File.Exists(file)) File.Delete(file);
        }
        record.Installed = false;
        SaveRecord(target, record);
        if (!SkipOsIntegration) { DeleteShortcuts(); Unregister(); }
        Console.WriteLine("程序文件已卸载；配置、日志、个人文件和维护安装包保留。可再次运行维护安装包重新安装。");
        return IntegrationFailed ? 1 : 0;
    }

    private static void CacheMaintenanceProgram()
    {
        var source = Environment.ProcessPath ?? throw new IOException("找不到维护安装程序。");
        var target = MaintenanceProgram;
        NoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true);
        // 缓存按版本分开，避免正在运行的同版维护器锁住自身；只清理已知的旧缓存文件。
        var parent = Directory.GetParent(Path.GetDirectoryName(target)!)!.FullName;
        foreach (var directory in Directory.EnumerateDirectories(parent))
        {
            if (ParseVersion(Path.GetFileName(directory)) is null || directory.Equals(Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase)) continue;
            var old = Path.Combine(directory, "ClassShout.Setup.exe");
            if (source.Equals(old, StringComparison.OrdinalIgnoreCase)) continue;
            try { NoLinks(old); if (File.Exists(old)) File.Delete(old); if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("旧维护缓存未清理，将保留到下次更新。"); }
        }
    }
}
