using System.Text.Json;
using System.Text.Json.Serialization;
using ClassShout.Core.Audio;

namespace ClassShout.Core.Remote;

/// <summary>白名单配置迁移；不包含账户、服务器身份、IPC 令牌和插件执行清单。</summary>
public sealed class PortableConfiguration
{
    public int FormatVersion { get; set; } = 1;
    public BellSettings? Bells { get; set; }
    public TeacherPhraseSettings? Phrases { get; set; }
    public ClassroomReplySettings? Replies { get; set; }
    public PortableSpeech? Speech { get; set; }
}

public sealed class PortableSpeech
{
    public int Volume { get; set; } = 100;
    public int Rate { get; set; } = 1;
    public string? SystemVoice { get; set; }
    public SpeechEngine Engine { get; set; }
    public string? EdgeVoice { get; set; }
    public string HttpVoice { get; set; } = "";
    public bool FallbackToSystem { get; set; } = true;
}

public static class ConfigurationTransfer
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] Export(bool classroom)
    {
        var speech = classroom ? LocalSettings.LoadSpeech() : null;
        var backup = new PortableConfiguration
        {
            Bells = classroom ? LocalSettings.LoadBells() : null,
            Replies = classroom ? LocalSettings.LoadReply() : null,
            Phrases = classroom ? null : LocalSettings.LoadPhrases(),
            // 地址可能把密钥藏在查询、路径或用户信息中，因此也不导出服务地址。
            Speech = speech is null ? null : new PortableSpeech
            {
                Engine = speech.Engine, EdgeVoice = speech.EdgeVoice, Volume = speech.Volume, Rate = speech.Rate, SystemVoice = speech.SystemVoice,
                HttpVoice = speech.Http?.Voice ?? "", FallbackToSystem = speech.Http?.FallbackToSystem ?? true,
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("备份超过 16 MiB，请减少自定义铃声音频后再导出。");
        return bytes;
    }

    public static PortableConfiguration Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("备份超过 16 MiB。");
        var backup = JsonSerializer.Deserialize<PortableConfiguration>(bytes, Json)
            ?? throw new InvalidDataException("备份内容为空。");
        if (backup.FormatVersion != 1) throw new InvalidDataException("不支持此备份格式版本。");
        if (backup.Bells?.Bells is { Count: > 256 }) throw new InvalidDataException("备份铃声不能超过 256 条。");
        if (backup.Speech is { } speech && !Enum.IsDefined(speech.Engine)) throw new InvalidDataException("备份朗读引擎无效。");
        if (backup.Replies is { } replies)
        {
            if (replies.QuickReplies is null || replies.AcknowledgeComponents is null
                || replies.QuickReplies.Any(reply => reply is null || reply.Components is null || reply.Components.Any(component => component is null))
                || replies.AcknowledgeComponents.Any(component => component is null))
                throw new InvalidDataException("备份快捷回复组件无效。");
        }
        return backup;
    }

    public static string Describe(PortableConfiguration backup, bool classroom) => classroom
        ? $"将覆盖：铃声 {(backup.Bells is null ? "不变" : backup.Bells.Bells?.Count.ToString() ?? "0")} 条；快捷回复 {(backup.Replies is null ? "不变" : "替换")}；TTS {(backup.Speech is null ? "不变" : "替换音色和引擎")}。服务地址、密钥和登录信息保留本机值。导入后请重启应用。"
        : $"将覆盖：常用语 {(backup.Phrases is null ? "不变" : "替换")}。登录、服务器和绑定教室不变。导入后请重启应用。";

    public static void Import(PortableConfiguration backup, bool classroom)
    {
        // 重新验证调用方提供的对象，所有目标文件名由程序固定，备份不能选择写入路径。
        backup = Parse(JsonSerializer.SerializeToUtf8Bytes(backup, Json));
        var writes = new Dictionary<string, byte[]>();
        void Add<T>(string file, T value) => writes[file] = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (classroom)
        {
            if (backup.Bells is { } bells) Add("bells.json", bells.Normalized());
            if (backup.Replies is { } replies) Add("classroom-reply.json", replies.Normalized());
            if (backup.Speech is { } incoming)
            {
                var speech = LocalSettings.LoadSpeech();
                speech.Http ??= new();
                speech.Engine = incoming.Engine;
                speech.Volume = Math.Clamp(incoming.Volume, 0, 100);
                speech.Rate = Math.Clamp(incoming.Rate, -6, 6);
                speech.SystemVoice = incoming.SystemVoice;
                speech.EdgeVoice = incoming.EdgeVoice;
                speech.Http.Voice = incoming.HttpVoice ?? "";
                speech.Http.FallbackToSystem = incoming.FallbackToSystem;
                Add("classroom-speech.json", speech);
            }
        }
        else if (backup.Phrases is { } phrases) Add("teacher-phrases.json", phrases.Normalized());
        if (writes.Count == 0) throw new InvalidDataException("备份没有适用于当前端的配置。");
        var root = LocalSettings.Directory;
        var old = writes.Keys.ToDictionary(file => file, file => File.Exists(Path.Combine(root, file)) ? File.ReadAllBytes(Path.Combine(root, file)) : null);
        var committed = new List<string>();
        try
        {
            foreach (var entry in writes) { WriteAtomic(Path.Combine(root, entry.Key), entry.Value); committed.Add(entry.Key); }
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            foreach (var file in committed.AsEnumerable().Reverse())
            {
                try
                {
                    var target = Path.Combine(root, file);
                    if (old[file] is not { } bytes) File.Delete(target); else WriteAtomic(target, bytes);
                }
                catch (Exception restore) { failures.Add(restore); }
            }
            if (failures.Count > 1) throw new AggregateException("导入失败且部分回滚失败，请检查配置目录权限。", failures);
            throw;
        }
    }

    private static void WriteAtomic(string target, byte[] bytes)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
