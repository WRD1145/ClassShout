using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using ClassShout.Core.Audio;
using ClassShout.Teacher.Services;

namespace ClassShout.Teacher.Android.Services;

/// <summary>
/// 安卓端的通知与语音回复播放。
///
/// 通知那条：Android 8 起必须挂在一条**渠道**上（没有渠道的通知会被系统直接丢掉，
/// 而且不会有任何错误）；Android 13 起还要 POST_NOTIFICATIONS 运行时权限。
/// 两者缺一个，老师就会觉得"教室里明明回了，我这边什么也没有"。
///
/// 播放那条：AudioTrack 直接喂 PCM（WAV 头剥掉），不落临时文件 ——
/// 回复只有十几秒，写文件再解码是白花一次 I/O，还多一个"文件没删掉"的可能。
/// </summary>
public static class AndroidNotifier
{
    internal const string ChannelId = "classshout.replies";
    private const int NotificationId = 4201;

    /// <summary>注册到平台层。调用方是 MainActivity（它才有 Context）。</summary>
    public static void Register(Context context)
    {
        EnsureChannel(context);

        var appContext = context.ApplicationContext ?? context;
        TeacherPlatform.RegisterNotifier((title, body) => Show(appContext, title, body));
        TeacherPlatform.RegisterWavPlayer(PlayWav);

        // Android 13+：申请通知权限。拒绝了也不影响应用内提示，所以不阻塞启动。
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
               != global::Android.Content.PM.Permission.Granted
            && context is Activity activity)
        {
            activity.RequestPermissions(
                [global::Android.Manifest.Permission.PostNotifications],
                NotificationPermissionRequestCode);
        }
    }

    /// <summary>权限申请码。与 MainActivity 里麦克风那个错开。</summary>
    public const int NotificationPermissionRequestCode = 1003;

    internal static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        try
        {
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
            {
                return;
            }

            var channel = new NotificationChannel(ChannelId, "教室回复", NotificationImportance.Default)
            {
                Description = "教室里有人回复你的时候提醒一下",
            };

            manager.CreateNotificationChannel(channel);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            global::Android.Util.Log.Warn("ClassShout", $"建不了通知渠道：{ex.Message}");
        }
    }

    internal static void Show(Context context, string title, string body)
    {
        try
        {
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (manager is null)
            {
                return;
            }

            // 用 NotificationCompat 的等价物（这里手写 builder，避免为一行字引入 AndroidX）
            var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(context, ChannelId)
                : new Notification.Builder(context);

            var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
            if (intent is not null)
            {
                intent.AddFlags(ActivityFlags.SingleTop);

                // PendingIntent 的 flags 在 Android 12 之后必须显式写可变性
                var flags = PendingIntentFlags.UpdateCurrent;
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    flags |= PendingIntentFlags.Immutable;
                }

                builder.SetContentIntent(PendingIntent.GetActivity(context, 0, intent, flags));
            }

            var notification = builder
                .SetContentTitle(title)
                .SetContentText(body)
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .SetAutoCancel(true)
                .Build();

            manager.Notify(NotificationId, notification);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            global::Android.Util.Log.Warn("ClassShout", $"弹不出通知：{ex.Message}");
        }
    }

    private static void PlayWav(byte[] wav)
    {
        if (!WavCodec.TryDecode(wav, out var format, out var pcm))
        {
            throw new NotSupportedException("这段音频不是可识别的 WAV。");
        }

        using var attributesBuilder = new AudioAttributes.Builder();
        attributesBuilder.SetUsage(AudioUsageKind.Media);
        attributesBuilder.SetContentType(AudioContentType.Speech);
        using var attributes = attributesBuilder.Build() ?? throw new InvalidOperationException("无法创建音频属性。");
        using var formatBuilder = new global::Android.Media.AudioFormat.Builder();
        formatBuilder.SetEncoding(global::Android.Media.Encoding.Pcm16bit);
        formatBuilder.SetSampleRate(format.SampleRate);
        formatBuilder.SetChannelMask(format.Channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo);
        using var audioFormat = formatBuilder.Build() ?? throw new InvalidOperationException("无法创建播放格式。");
        var track = new AudioTrack.Builder()
            .SetAudioAttributes(attributes)
            .SetAudioFormat(audioFormat)
            .SetTransferMode(AudioTrackMode.Static)
            .SetBufferSizeInBytes(pcm.Length)
            .Build();

        track.Write(pcm, 0, pcm.Length);
        track.Play();

        // 播完就把轨道放掉：一条回复几秒，攒着不放会占住音频焦点
        track.SetNotificationMarkerPosition(pcm.Length / (format.Channels * (format.BitsPerSample / 8)));
        track.MarkerReached += (_, _) =>
        {
            try
            {
                track.Stop();
                track.Release();
                track.Dispose();
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or ObjectDisposedException)
            {
                // 已经放掉了
            }
        };
    }
}
