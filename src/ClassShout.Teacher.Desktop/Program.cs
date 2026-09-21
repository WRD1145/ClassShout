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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ClassShout.Teacher.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
