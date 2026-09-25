using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClassShout.Core.Remote;

namespace ClassShout.Design;

/// <summary>
/// 「检查更新」那张卡片的状态与动作。
///
/// 放在设计层而不是各端各写一份：教室端与教师端要的是同一张卡片、同一套设置、
/// 同一个镜像列表 —— 两边都写一遍的话，镜像源这种"三个月就要改一次"的东西
/// 迟早会只改一边。
///
/// 检查**只在用户按下按钮时发生**：后台定时去问"有没有新版本"，
/// 等于每次开应用都向外面报一次到，而放在教室里的那台机器没有这个必要。
/// </summary>
public sealed class UpdateCardViewModel : INotifyPropertyChanged
{
    private readonly UpdateChecker _checker;
    private readonly Func<string, Task>? _openUrl;

    private UpdateSettings _settings;
    private string _status = string.Empty;
    private string _latestVersion = string.Empty;
    private string _notes = string.Empty;
    private bool _isBusy;
    private UpdateAsset? _download;
    private string? _pageUrl;

    public UpdateCardViewModel(HttpClient http, UpdateSettings settings, Func<string, Task>? openUrl = null)
    {
        _settings = settings.Normalized();
        _checker = new UpdateChecker(http, _settings);
        _openUrl = openUrl;

        CheckCommand = new SimpleCommand(async () => await CheckAsync().ConfigureAwait(true), () => !IsBusy);
        OpenDownloadCommand = new SimpleCommand(async () => await OpenDownloadAsync().ConfigureAwait(true), () => CanDownload);
        OpenPageCommand = new SimpleCommand(async () => await OpenPageAsync().ConfigureAwait(true), () => CanOpenPage);
        SaveCommand = new SimpleCommand(Save);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前版本号（由调用方从程序集里取，Core 不替谁决定"我是谁"）。</summary>
    public string CurrentVersion { get; set; } = "未知";

    /// <summary>完整的一行构建信息，报问题时贴它。</summary>
    public string BuildDescription { get; set; } = string.Empty;

    /// <summary>当前版本一行字。</summary>
    public string CurrentVersionText => $"当前版本 v{CurrentVersion}";

    /// <summary>可选的镜像（含"直连 GitHub"）。</summary>
    public IReadOnlyList<UpdateMirror> Mirrors => UpdateSettings.Presets;

    /// <summary>
    /// 要下载哪个附件。桌面端填 <c>ClassShout.Teacher.Desktop.exe</c> 之类；
    /// 留空表示只提示、不直接给下载按钮。
    /// </summary>
    public string? PreferredAssetName { get; set; }

    /// <summary>
    /// 按后缀挑附件，例如 <c>.apk</c>。
    ///
    /// 为什么需要它：附件名里带着版本号（<c>classshout-teacher-1.8.0-universal.apk</c>），
    /// 而"我要的是哪个包"这件事跟版本号无关 —— 精确匹配会因为版本号变了就找不到。
    /// </summary>
    public string? PreferredAssetSuffix { get; set; }

    /// <summary>检查更新。绑到按钮上。</summary>
    public ICommand CheckCommand { get; }

    /// <summary>打开下载地址（镜像已经在检查时换算好了）。</summary>
    public ICommand OpenDownloadCommand { get; }

    /// <summary>打开发行版页面。</summary>
    public ICommand OpenPageCommand { get; }

    /// <summary>把改过的镜像设置存下来。</summary>
    public ICommand SaveCommand { get; }

    /// <summary>仓库，例如 WRD1145/ClassShout。</summary>
    public string Repository
    {
        get => _settings.Repository;
        set
        {
            if (_settings.Repository == value)
            {
                return;
            }

            _settings.Repository = value;
            Raise();
        }
    }

    /// <summary>GitHub API 基址（镜像自带 API 时填镜像的）。</summary>
    public string ApiBase
    {
        get => _settings.ApiBase;
        set
        {
            if (_settings.ApiBase == value)
            {
                return;
            }

            _settings.ApiBase = value;
            Raise();
        }
    }

    /// <summary>下载地址模板，支持 {url} 与 {path} 两个占位符。</summary>
    public string DownloadTemplate
    {
        get => _settings.DownloadTemplate;
        set
        {
            if (_settings.DownloadTemplate == value)
            {
                return;
            }

            _settings.DownloadTemplate = value;
            Raise();
        }
    }

    /// <summary>当前选中的镜像（按 API 基址 + 模板匹配预设；对不上就是"自定义"）。</summary>
    public UpdateMirror? SelectedMirror
    {
        get => UpdateSettings.Presets.FirstOrDefault(mirror =>
            string.Equals(mirror.ApiBase, ApiBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(mirror.DownloadTemplate, DownloadTemplate.Trim(), StringComparison.OrdinalIgnoreCase));

        set
        {
            if (value is not { } mirror)
            {
                return;
            }

            ApiBase = mirror.ApiBase;
            DownloadTemplate = mirror.DownloadTemplate;
            Status = $"已选「{mirror.Label}」。点「检查更新」试试这条源通不通。";
            Raise();
        }
    }

    /// <summary>检查中。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            Raise();
        }
    }

    /// <summary>结果或错误的一句话。</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            Raise();
        }
    }

    /// <summary>查到的新版本号（没查到或已是最新时为空）。</summary>
    public string LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (_latestVersion == value)
            {
                return;
            }

            _latestVersion = value;
            Raise();
            Raise(nameof(UpdateHintText));
        }
    }

    /// <summary>发行说明原文（只给一小段，全文在发行版页面上）。</summary>
    public string Notes
    {
        get => _notes;
        private set
        {
            if (_notes == value)
            {
                return;
            }

            _notes = value;
            Raise();
        }
    }

    /// <summary>有新版本时的一句话。</summary>
    public string UpdateHintText => LatestVersion.Length > 0
        ? $"有新版本 v{LatestVersion}（当前 v{CurrentVersion}）"
        : string.Empty;

    /// <summary>能不能直接下载（查到新版本、且这次要求直接给附件）。</summary>
    public bool CanDownload => _download is not null;

    /// <summary>能不能打开发行版页面。</summary>
    public bool CanOpenPage => !string.IsNullOrWhiteSpace(_pageUrl) && _openUrl is not null;

    /// <summary>有没有可下载的东西（界面据此显示按钮）。</summary>
    public bool HasDownload => CanDownload;

    /// <summary>上次检查时间（取自设置，供界面显示）。</summary>
    public string LastCheckedText => _settings.LastCheckedAt is { } at
        ? $"上次检查：{at.ToLocalTime():MM-dd HH:mm}"
        : "还没检查过。";

    /// <summary>
    /// 先按当前设置查一次，查完把结果记进设置（含"上次检查时间"）。
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "正在检查…";
        LatestVersion = string.Empty;
        Notes = string.Empty;
        _download = null;
        _pageUrl = null;
        Raise(nameof(CanDownload));
        Raise(nameof(CanOpenPage));
        Raise(nameof(HasDownload));

        try
        {
            var result = await _checker.CheckAsync(CurrentVersion, cancellationToken).ConfigureAwait(true);

            _settings.LastCheckedAt = DateTimeOffset.Now;

            if (!result.Ok)
            {
                Status = result.Error ?? "检查失败。";
                return;
            }

            _settings.LatestVersion = result.LatestVersion;
            _pageUrl = result.PageUrl;

            if (result.HasUpdate)
            {
                LatestVersion = result.LatestVersion ?? string.Empty;
                Notes = Summarize(result.Notes);

                _download = PreferredAssetName is { Length: > 0 }
                    ? result.FindAsset(PreferredAssetName)
                    : null;

                _download ??= PreferredAssetSuffix is { Length: > 0 }
                    ? result.FindAssetEndingWith(PreferredAssetSuffix)
                    : null;

                Status = _download is not null
                    ? $"发现新版本 v{result.LatestVersion}，可以直接下载。"
                    : $"发现新版本 v{result.LatestVersion}。";
            }
            else
            {
                Status = $"已经是最新的 v{CurrentVersion}。";
            }
        }
        finally
        {
            LocalSettings.SaveUpdate(_settings);
            IsBusy = false;
            Raise(nameof(LastCheckedText));
            Raise(nameof(CanDownload));
            Raise(nameof(CanOpenPage));
            Raise(nameof(HasDownload));
        }
    }

    /// <summary>打开下载地址（镜像已在检查时换好）。</summary>
    public async Task OpenDownloadAsync()
    {
        if (_download is null || _openUrl is null)
        {
            return;
        }

        await _openUrl(_download.Url).ConfigureAwait(true);
        Status = $"已在浏览器里打开 {_download.Name} 的下载地址。";
    }

    /// <summary>打开发行版页面（看完整的更新说明）。</summary>
    public async Task OpenPageAsync()
    {
        if (string.IsNullOrWhiteSpace(_pageUrl) || _openUrl is null)
        {
            return;
        }

        await _openUrl(_pageUrl).ConfigureAwait(true);
    }

    /// <summary>保存这次改的镜像设置。检查本身也会保存，这里给"只想存下来"的人一个按钮。</summary>
    public void Save()
    {
        LocalSettings.SaveUpdate(_settings);
        Status = "设置已保存。";
        Raise(nameof(LastCheckedText));
    }

    /// <summary>把发行说明裁成一小段：整篇 Markdown 贴在手机上是灾难。</summary>
    private static string Summarize(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        var text = notes.ReplaceLineEndings("\n").Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一个够用的命令实现。
///
/// 设计层不引 MVVM 框架（它只管控件与令牌），而这张卡片又需要几个按钮 ——
/// 为此拉一个包进来不划算，二十行就够了。
/// </summary>
internal sealed class SimpleCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public SimpleCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public SimpleCommand(Action execute, Func<bool>? canExecute = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // 命令是从界面按下去的，异常不能让它带着进程走
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 「在浏览器里打开一个地址」。
///
/// Windows / Linux 有默认实现，Android 没有 —— 那边要靠平台头注册一个 Intent 版本
/// （见 Android 头的 MainActivity）。做成注册制而不是在各处写平台判断：
/// 界面代码里出现 <c>if (Android)</c> 正是这个项目一直在避免的东西。
/// </summary>
public static class PlatformLinks
{
    private static Func<string, Task>? _opener;

    /// <summary>由平台头调用，注册本平台"打开网址"的实现。</summary>
    public static void Register(Func<string, Task> opener) => _opener = opener;

    /// <summary>打开一个网址。打不开时返回 false（而不是抛异常）。</summary>
    public static async Task<bool> OpenAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (_opener is not null)
        {
            try
            {
                await _opener(url).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
            {
                return false;
            }
        }

        return OpenWithShell(url);
    }

    private static bool OpenWithShell(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });

                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", url);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
