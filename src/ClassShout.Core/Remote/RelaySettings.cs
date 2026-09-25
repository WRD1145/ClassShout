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
    /// 已经绑定过的教室。老师可能同时教好几个班，绑定过的都留在这里，切过去就能喊。
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

    /// <summary>
    /// 保存的教室最多留几个。
    ///
    /// 不是随便定的：一位老师一学期的任课班级很少超过十来个，
    /// 而留得太多会让列表长到需要滚动才能找到当前那间 ——
    /// 这个列表是拿来"一眼选中下一节课的教室"的，不是一个档案库。
    /// </summary>
    public const int MaxSavedClassrooms = 12;

    /// <summary>
    /// 把一间教室写进保存列表：同一间只留一条（新的排到最前），超出上限丢最旧的那条。
    ///
    /// 做成静态方法是为了能直接测。这段逻辑没有界面、也不发请求，
    /// 却决定了"老师换班上课时列表里还剩哪几间" —— 去重和上限写错，
    /// 表现是列表里出现两条同名教室、或者刚绑过的那间被挤掉，
    /// 而这两种都要等人用上一阵子才会发现。
    /// </summary>
    /// <param name="classrooms">要更新的列表（就是自己那份配置里的那一项）。</param>
    /// <param name="record">这次要记下的教室。</param>
    public static void Remember(IList<BoundClassroom> classrooms, BoundClassroom record)
    {
        // 按 UUID 判同一间，且不区分大小写：不同来源的 UUID 大小写可能不一样，
        // 按序数比较会让同一间教室在列表里出现两条。
        for (var i = classrooms.Count - 1; i >= 0; i--)
        {
            if (string.Equals(classrooms[i].Uuid, record.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                classrooms.RemoveAt(i);
            }
        }

        classrooms.Insert(0, record);

        while (classrooms.Count > MaxSavedClassrooms)
        {
            classrooms.RemoveAt(classrooms.Count - 1);
        }
    }
}

/// <summary>
/// 教师端保存下来的一间教室。
///
/// **这里保存口令是有意为之。** 原来刻意不存，理由是"口令留在老师脑子里更安全"。
/// 但那个取舍在"一个老师教好几个班"面前站不住：口令是绑定的唯一凭据，
/// 不存它，每次换班上课都要重新找管理员要一遍口令，这个功能就等于没做。
///
/// 代价说清楚：这台设备被拿走，就等于能绑定这几间教室并朝它们喊话。
/// 不过同一个文件里本来就存着登录令牌（那同样是能喊话的凭据），
/// 所以新增的暴露面其实很小。不想要哪一间，在列表里移除即可，口令一并删掉。
/// </summary>
/// <param name="Uuid">教室 UUID。它在服务器上定位这间教室。</param>
/// <param name="Name">教室名，绑定成功后由服务器回传。</param>
/// <param name="LastBoundAt">最近一次绑定时间。</param>
/// <param name="ServerUrl">绑定它时用的中继服务器地址。换服务器之后仍能切回去。</param>
/// <param name="Secret">教室口令。为空表示这间是靠"管理员授权"绑定的，不需要口令。</param>
public sealed record BoundClassroom(
    string Uuid,
    string Name,
    DateTimeOffset LastBoundAt,
    string? ServerUrl = null,
    string? Secret = null);

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

    /// <summary>
    /// 语音转文字（ASR）的设置。
    ///
    /// 存在教室端而不是教师端：密钥该由管理员在教室里那台固定机器上配一次，
    /// 而不是让每个老师在各自手机上都填一遍。转写也发生在教室端 ——
    /// 那里是语音真正到达、也是需要把文字显示出来的地方。
    /// </summary>
    public static SttSettings LoadStt() => Load("classroom-stt.json", static () => new SttSettings());

    public static bool SaveStt(SttSettings settings) => Save("classroom-stt.json", settings);

    public static AppearanceSettings LoadAppearance() => Load("appearance.json", static () => new AppearanceSettings());

    public static bool SaveAppearance(AppearanceSettings settings) => Save("appearance.json", settings);

    public static DeveloperSettings LoadDeveloper() => Load("developer.json", static () => new DeveloperSettings());

    public static bool SaveDeveloper(DeveloperSettings settings) => Save("developer.json", settings);

    /// <summary>教师端上次用过的展示参数（展示方式 / 字号 / 停留时长 / 是否朗读）。</summary>
    public static TeacherDisplaySettings LoadTeacherDisplay()
        => Load("teacher-display.json", static () => new TeacherDisplaySettings());

    public static bool SaveTeacherDisplay(TeacherDisplaySettings settings)
        => Save("teacher-display.json", settings);

    /// <summary>定时喊话（还没发的任务 + 最近处理完的记录）。</summary>
    public static TeacherScheduleSettings LoadSchedule()
        => Load("teacher-schedule.json", static () => new TeacherScheduleSettings());

    public static bool SaveSchedule(TeacherScheduleSettings settings)
        => Save("teacher-schedule.json", settings);

    /// <summary>学生名单（可以有好几份）与"我正在叫谁"。</summary>
    public static TeacherRosterSettings LoadRosters()
        => Load("teacher-rosters.json", static () => new TeacherRosterSettings());

    public static bool SaveRosters(TeacherRosterSettings settings)
        => Save("teacher-rosters.json", settings);

    /// <summary>呼叫模板（组件拼装出来的那几套）。</summary>
    public static TeacherCallSettings LoadCalls()
        => Load("teacher-calls.json", static () => new TeacherCallSettings { Templates = [TeacherCallSettings.DefaultTemplate()] });

    public static bool SaveCalls(TeacherCallSettings settings)
        => Save("teacher-calls.json", settings);

    /// <summary>文字页上那一排常用语（老师自己增删改）。</summary>
    public static TeacherPhraseSettings LoadPhrases()
        => Load("teacher-phrases.json", TeacherPhraseSettings.WithDefaults);

    public static bool SavePhrases(TeacherPhraseSettings settings)
        => Save("teacher-phrases.json", settings);
}
