using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using ClassShout.Teacher.Android.Services;
using ClassShout.Teacher.Services;

namespace ClassShout.Teacher.Android;

[Activity(
    Label = "ClassShout 教师端",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
// 让浏览器里的"用教师端打开"按钮能叫起本应用。
// 这必须声明在 Activity 上（而不是只写清单文件）：清单里的 Activity 是构建系统
// 按这个特性生成的，手写一份会和它打架。
[IntentFilter(
    [global::Android.Content.Intent.ActionView],
    Categories = [global::Android.Content.Intent.CategoryDefault, global::Android.Content.Intent.CategoryBrowsable],
    DataSchemes = [ClassShout.Core.Remote.ShareLink.Scheme],
    DataHosts = ["claim"])]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int RecordAudioRequestCode = 1001;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // 与桌面头做的是同一件事：在 Avalonia 启动前把平台能力注入共享 UI 层。
        // 区别只在于这里用的是 Android 的 AudioRecord。
        TeacherPlatform.DeviceName = $"{Build.Model}（手机）";
        TeacherPlatform.RegisterAudioRecorder(() => new AndroidAudioRecorder(this));

        // "在浏览器里打开下载地址"：Android 上桌面那套 Process.Start 用不了，
        // 得走一个 ACTION_VIEW 的 Intent。不注册的话，检查更新查到了新版本、
        // 点下载却什么都不会发生。
        ClassShout.Design.PlatformLinks.Register(url =>
        {
            // 注意 global:: —— 本文件的命名空间是 ClassShout.Teacher.Android，
            // 直接写 Android.Net.Uri 会被解析成"这个命名空间下的 Net"。
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
            return Task.CompletedTask;
        });

        // 选一份名单文件（.csv / .xlsx）走 SAF：桌面端是 StorageProvider，
        // 安卓端则要一个 ActivityResult —— 这就是"选文件"必须放在平台层的原因。
        TeacherPlatform.RegisterRosterFilePicker(PickRosterFileAsync);

        return base.CustomizeAppBuilder(builder);
    }

    private const int PickRosterFileRequestCode = 1002;

    /// <summary>等待文件选择结果的那次调用。</summary>
    private TaskCompletionSource<RosterFilePickResult?>? _rosterPick;

    /// <summary>
    /// 用 SAF 让老师挑一个名单文件。
    ///
    /// 用 ACTION_OPEN_DOCUMENT（而不是 GET_CONTENT）：它能拿到一个可长期读取的 Uri，
    /// 而且"最近"列表里会记住位置 —— 老师每学期导一次名单，不该每次都从头翻目录。
    /// </summary>
    private Task<RosterFilePickResult?> PickRosterFileAsync()
    {
        // 上一次还没结束就再点一次：把上一次当作取消，避免两次选择互相覆盖
        _rosterPick?.TrySetResult(null);

        var pick = new TaskCompletionSource<RosterFilePickResult?>();
        _rosterPick = pick;

        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[]
        {
            "text/csv",
            "text/comma-separated-values",
            "text/plain",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-excel",
        });

        try
        {
            StartActivityForResult(intent, PickRosterFileRequestCode);
        }
        catch (ActivityNotFoundException ex)
        {
            // 设备上没有任何文件管理器：如实回报，别让界面永远停在"正在选文件"
            _rosterPick = null;
            global::Android.Util.Log.Warn("ClassShout", $"没有可用的文件选择器：{ex.Message}");
            return Task.FromResult<RosterFilePickResult?>(null);
        }

        return pick.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != PickRosterFileRequestCode)
        {
            return;
        }

        var pick = _rosterPick;
        _rosterPick = null;

        if (pick is null)
        {
            return;
        }

        if (resultCode != Result.Ok || data?.Data is not { } uri)
        {
            // 用户取消：不是错误，界面不该弹提示
            pick.TrySetResult(null);
            return;
        }

        try
        {
            // 先把内容整个读进内存再交出去：Excel 需要可 Seek 的流，
            // 而 SAF 给的 ContentStream 通常不支持 Seek；顺带也让调用方
            // 不必担心这个 Uri 的读取权限什么时候过期。
            var buffer = new MemoryStream();

            using (var stream = ContentResolver?.OpenInputStream(uri))
            {
                if (stream is null)
                {
                    pick.TrySetResult(null);
                    return;
                }

                stream.CopyTo(buffer);
            }

            buffer.Position = 0;

            pick.TrySetResult(new RosterFilePickResult(QueryDisplayName(uri), buffer));
        }
        catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.SecurityException or UnauthorizedAccessException)
        {
            global::Android.Util.Log.Warn("ClassShout", $"读选中的文件失败：{ex.Message}");
            pick.TrySetResult(null);
        }
    }

    /// <summary>从 SAF 的 Uri 里问出显示名（扩展名决定用哪种读法）。</summary>
    private string QueryDisplayName(global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(uri, null, null, null, null);

            if (cursor is not null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
                if (index >= 0)
                {
                    var name = cursor.GetString(index);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name!;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            // 问不到名字就退回 Uri 的最后一段；扩展名认不出时 RosterFile 会按文本读
            global::Android.Util.Log.Warn("ClassShout", $"取文件名失败：{ex.Message}");
        }

        return uri.LastPathSegment ?? "名单文件";
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 麦克风属于危险权限，Android 6 起必须运行时申请，
        // 只写进清单是不够的 —— 不申请的话 AudioRecord 会直接抛异常。
        EnsureRecordAudioPermission();

        // 应用被一条 classshout:// 链接启动
        HandleShareIntent(Intent);
    }

    /// <summary>
    /// 应用已在前台时又点了一条链接。
    ///
    /// 这一条最容易被漏掉：LaunchMode.SingleTop 下系统不会新建 Activity，
    /// 只把新的 Intent 交给已有的那个 —— 不在这里处理的话，
    /// 表现就是"第二次点链接没反应"，而第一次是好的。
    /// </summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        // 基类会用新 Intent 更新 Activity.Intent，这里显式设一遍以免依赖基类细节
        if (intent is not null)
        {
            Intent = intent;
        }

        HandleShareIntent(intent);
    }

    /// <summary>把 Intent 里的分享链接交给共享 UI 层。</summary>
    private static void HandleShareIntent(Intent? intent)
    {
        if (intent?.Data is not { } data)
        {
            return;
        }

        if (!string.Equals(data.Scheme, ClassShout.Core.Remote.ShareLink.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TeacherPlatform.NotifyShareLink(data.ToString() ?? string.Empty);
    }

    protected override void OnStop()
    {
        // 切后台 / 锁屏 / 被系统回收都会走到这里。
        // 必须在这里结束进行中的录音：否则麦克风一直被占着，
        // 而且这段时间采集到的声音会继续当成一次正常喊话发到教室里。
        TeacherPlatform.NotifyBackgrounded();

        base.OnStop();
    }

    private void EnsureRecordAudioPermission()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            {
                RequestPermissions([global::Android.Manifest.Permission.RecordAudio], RecordAudioRequestCode);
            }
        }
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == RecordAudioRequestCode && grantResults.Length > 0 && grantResults[0] != Permission.Granted)
        {
            // 拒绝也不影响文字喊话，语音页在开始录音时会提示缺少权限
            global::Android.Util.Log.Warn("ClassShout", "用户拒绝了麦克风权限，语音喊话将不可用。");
        }
    }
}
