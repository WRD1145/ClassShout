using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassShout.Core.Remote;

/// <summary>ClassShout 插件包清单，与 ClassIsland 的二进制插件格式相互独立。</summary>
public sealed class PluginManifest
{
    public int ApiVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string EntryPoint { get; set; } = "";
    public string Runner { get; set; } = "assembly";
    public string EntryType { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public string[] Hosts { get; set; } = ["classroom", "teacher"];
    public string[] Platforms { get; set; } = ["windows", "linux"];
}

public sealed class PluginPackage
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private readonly byte[] _bytes;
    public PluginManifest Manifest { get; }
    private PluginPackage(byte[] bytes, PluginManifest manifest) { _bytes = bytes; Manifest = manifest; }
    public static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 64
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && !id.EndsWith('.')
        && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(id.Split('.')[0], StringComparer.OrdinalIgnoreCase);

    private static string SafePath(string? name)
    {
        if (string.IsNullOrEmpty(name)) throw new InvalidDataException("插件路径不能为空。");
        var path = name.Replace('\\', '/');
        var parts = path.TrimEnd('/').Split('/');
        if (path.Length > 240 || parts.Length > 16 || path.StartsWith('/') || parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))
            || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(part.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("插件包包含不安全的文件路径。");
        return path;
    }

    public static PluginPackage Read(byte[] bytes, string role)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("插件包超过 32 MiB。");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 256) throw new InvalidDataException("插件包文件数超过 256。");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = SafePath(entry.FullName);
            if (!paths.Add(path.TrimEnd('/')) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("插件包包含重复路径或链接文件。");
            if (entry.Length > MaxBytes || (total += entry.Length) > 64L * 1024 * 1024)
                throw new InvalidDataException("插件包解压后过大。");
        }
        var json = zip.GetEntry("manifest.json");
        if (json is null || json.Length > 32768) throw new InvalidDataException("插件包根目录需要不超过 32 KiB 的 manifest.json。");
        using var input = json.Open();
        using var manifestBytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (manifestBytes.Length + read > 32768) throw new InvalidDataException("插件清单实际内容超过 32 KiB。");
            manifestBytes.Write(buffer, 0, read);
        }
        var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestBytes.ToArray(), new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("插件清单为空。");
        if (!ValidId(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128
            || manifest.Author is null || manifest.Author.Length > 128 || manifest.Description is null || manifest.Description.Length > 2048
            || !System.Version.TryParse(manifest.Version, out _) || manifest.ApiVersion != 1
            || manifest.Hosts is null || !manifest.Hosts.Contains(role) || manifest.Hosts.Any(host => host is not ("teacher" or "classroom"))
            || manifest.Platforms is null || !manifest.Platforms.Contains(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")
            || manifest.Platforms.Any(platform => platform is not ("windows" or "linux" or "macos"))
            || manifest.Runner != "assembly" || string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 256 || manifest.Arguments is null
            || manifest.Arguments.Length != 0)
            throw new InvalidDataException("插件清单字段、API 版本、适用端或平台不兼容。");
        var executable = SafePath(manifest.EntryPoint);
        if (!executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || zip.GetEntry(executable) is null)
            throw new InvalidDataException("插件入口文件不存在，或路径大小写不匹配。");
        return new PluginPackage(bytes.ToArray(), manifest);
    }

    internal PluginPackage Revalidate(string role) => Read(_bytes, role);

    internal void Extract(string root)
    {
        using var zip = new ZipArchive(new MemoryStream(_bytes), ZipArchiveMode.Read);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var relative = SafePath(entry.FullName);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件文件超出安装目录。");
            if (relative.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var input = entry.Open();
            var buffer = new byte[8192];
            int read;
            long size = 0;
            while ((read = input.Read(buffer)) > 0)
            {
                if ((size += read) > MaxBytes || (total += read) > 64L * 1024 * 1024)
                    throw new InvalidDataException("插件包实际解压内容超过限制。");
                output.Write(buffer, 0, read);
            }
        }
    }
}
