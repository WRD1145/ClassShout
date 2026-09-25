using Avalonia;
using ClassShout.Teacher.Desktop.Services;
using ClassShout.Teacher.Services;

namespace ClassShout.Teacher.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 在 Avalonia 起来之前注册平台能力。
        // 这是共享 UI 层与桌面平台之间唯一的耦合点，Android 头做的是同一件事。
        TeacherPlatform.DeviceName = $"{Environment.MachineName}（PC）";
        TeacherPlatform.RegisterAudioRecorder(() => new NAudioRecorder());

        // 把 classshout:// 注册到本用户，让网页上的"用教师端打开"按钮能叫起这个程序。
        // 注册不上不影响使用：设备页里粘贴链接那条路一直可用。
        UrlSchemeRegistrar.TryRegister(out _);

        // 应用可能是被一条分享链接启动的（点链接 → Windows 调起本程序）。
        // 这时要把链接交给界面层 —— 直接给事件会太早（订阅者还没建起来），
        // 所以先存下来，等 App 起来之后再补发。
        var link = TeacherPlatform.FindShareLink(args);
        if (link is not null)
        {
            ClassShout.Teacher.App.PendingShareLink = link;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }


    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ClassShout.Teacher.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
