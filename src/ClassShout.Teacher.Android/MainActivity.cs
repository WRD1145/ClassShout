using Android.App;
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
