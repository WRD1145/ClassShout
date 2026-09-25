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

        return base.CustomizeAppBuilder(builder);
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
