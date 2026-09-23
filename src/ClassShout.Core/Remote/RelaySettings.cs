using ClassShout.Core.Audio;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassShout.Core.Remote;

/// <summary>
/// 教室端的联网配置。
///
/// UUID 在首次启动时生成一次并永久保存 —— 它是这台教室在服务器上的身份，
/// 换了就等于换了一间教室，所有教师端都要重新绑定，因此绝不能每次启动重新生成。
/// </summary>
public sealed class ClassroomRelaySettings
{
    /// <summary>教室的唯一标识，首次启动生成后不再改变。</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>注册口令。注册成功后由服务器回传并保存在本地，后续重连要用它校验。</summary>
    public string? Secret { get; set; }

    /// <summary>中继服务器地址，例如 https://relay.example.com 或 http://192.168.1.10:8080。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>教室名，注册时上报给服务器，也是教师端绑定后看到的名称。</summary>
    public string ClassroomName { get; set; } = "教室";

    /// <summary>是否已配置好联网模式。</summary>
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Uuid);

    /// <summary>确保有 UUID；没有就生成一个。</summary>
    public string EnsureUuid()
    {
        if (string.IsNullOrWhiteSpace(Uuid))
        {
            Uuid = Guid.NewGuid().ToString("D");
        }

        return Uuid;
    }
}

/// <summary>教师端的联网配置。</summary>
public sealed class TeacherRelaySettings
{
    /// <summary>中继服务器地址。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>上次绑定成功的教室 UUID，填入界面省得每次手打。</summary>
    public string? LastUuid { get; set; }

    /// <summary>
    /// 最近成功绑定过的教室。
    /// 刻意不保存口令：口令是敏感信息，让它留在老师脑子里比留在磁盘上安全。
    /// </summary>
    public List<BoundClassroom> RecentClassrooms { get; set; } = [];

    // —— 登录状态 ——

    /// <summary>
    /// 登录令牌，用于"下次打开仍在登录状态"。
    /// 这是有意为之的取舍：不存令牌就得每次开 App 都登录，课堂上很不友好；
    /// 存了则等同于"这台设备已授权"。退出登录会清掉它。
    /// </summary>
    public string? AuthToken { get; set; }

    public string? UserId { get; set; }

    /// <summary>老师姓名。教室端弹窗与喊话来源显示的都是它。</summary>
    public string? DisplayName { get; set; }

    public string? Username { get; set; }

    public string? Email { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl);

    [JsonIgnore]
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(AuthToken) && !string.IsNullOrWhiteSpace(DisplayName);
}

/// <summary>教师端记住的一个教室（不含口令）。</summary>
/// <param name="Uuid">教室 UUID。</param>
/// <param name="Name">教室名，绑定成功后由服务器回传。</param>
/// <param name="LastBoundAt">最近一次绑定时间。</param>
public sealed record BoundClassroom(string Uuid, string Name, DateTimeOffset LastBoundAt);

/// <summary>
/// 本地设置文件的读写。
///
/// 放在用户数据目录而不是程序目录：教室端可能被安装在 Program Files 这类只读位置，
/// 而且同一台机器上不同 Windows 账户应当有各自独立的身份。
/// </summary>
public static class LocalSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>设置文件所在目录，不存在会自动创建。</summary>
    public static string Directory
    {
        get
        {
            // 允许用环境变量指定数据目录。两个用处：
            //   · 自检可以在临时目录里跑，不碰使用者真实的 teacher.json / classroom.json；
            //   · 便携安装（放 U 盘、放只读盘旁的可写目录）能自己决定数据放哪。
            // 与中继服务器的 CLASSSHOUT_RELAY_STATE 是同一套思路。
            var overridden = Environment.GetEnvironmentVariable("CLASSSHOUT_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                System.IO.Directory.CreateDirectory(overridden);
                return overridden;
            }

            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                // 少数受限环境（如某些 Android 容器）取不到标准目录，退回到当前目录
                root = AppContext.BaseDirectory;
            }

            var path = Path.Combine(root, "ClassShout");
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
    }

    public static T Load<T>(string fileName, Func<T> fallback)
        where T : class
    {
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            return fallback();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置损坏不该让应用起不来，退回默认值即可
            return fallback();
        }
    }

    /// <summary>保存设置。返回是否成功 —— 保存失败要能让界面提示，而不是静默丢配置。</summary>
    public static bool Save<T>(string fileName, T value)
        where T : class
    {
        try
        {
            var path = Path.Combine(Directory, fileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ClassroomRelaySettings LoadClassroom() => Load("classroom.json", static () => new ClassroomRelaySettings());

    public static bool SaveClassroom(ClassroomRelaySettings settings) => Save("classroom.json", settings);

    public static TeacherRelaySettings LoadTeacher() => Load("teacher.json", static () => new TeacherRelaySettings());

    public static bool SaveTeacher(TeacherRelaySettings settings) => Save("teacher.json", settings);

    public static ClassroomSpeechSettings LoadSpeech() => Load("classroom-speech.json", static () => new ClassroomSpeechSettings());

    public static bool SaveSpeech(ClassroomSpeechSettings settings) => Save("classroom-speech.json", settings);

    public static SttSettings LoadStt() => Load("teacher-stt.json", static () => new SttSettings());

    public static bool SaveStt(SttSettings settings) => Save("teacher-stt.json", settings);

    public static AppearanceSettings LoadAppearance() => Load("appearance.json", static () => new AppearanceSettings());

    public static bool SaveAppearance(AppearanceSettings settings) => Save("appearance.json", settings);
}
