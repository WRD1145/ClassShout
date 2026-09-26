using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClassShout.Core.Remote;

namespace ClassShout.Design;

/// <summary>
/// 「检查更新」那张卡片的状态与动作。
///
/// 放在设计层而不是各端各写一份：教室端与教师端要的是同一张卡片、同一套设置、
/// 同一份镜像列表 —— 两边都写一遍的话，镜像源这种"三个月就要改一次"的东西
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
    private bool _isTesting;
    private UpdateAsset? _download;
    private string? _pageUrl;
    private string _testSummary = string.Empty;

    // 新增镜像用的输入框
    private string _newLabel = string.Empty;
    private string _newApiBase = string.Empty;
    private string _newRepository = string.Empty;
    private string _newTemplate = string.Empty;
    private bool _newIsGitee;

    public UpdateCardViewModel(HttpClient? http, UpdateSettings settings, Func<string, Task>? openUrl = null)
    {
        _settings = settings.Normalized();

        // http 参数保留只是为了不动各端的构造调用；检查器自己按代理设置建连接，
        // 这样"改了代理立刻生效"，不必让各端重建 HttpClient。
        _ = http;

        _checker = new UpdateChecker(_settings);
        _openUrl = openUrl;

        CheckCommand = new SimpleCommand(async () => await CheckAsync().ConfigureAwait(true), () => !IsBusy);
        TestAllCommand = new SimpleCommand(async () => await TestAllAsync().ConfigureAwait(true), () => !IsTesting);
        OpenDownloadCommand = new SimpleCommand(async () => await OpenDownloadAsync().ConfigureAwait(true), () => CanDownload);
        OpenPageCommand = new SimpleCommand(async () => await OpenPageAsync().ConfigureAwait(true), () => CanOpenPage);
        SaveCommand = new SimpleCommand(Save);
        AddMirrorCommand = new SimpleCommand(AddMirror, () => NewLabel.Trim().Length > 0);

        RefreshMirrors();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前版本号（由调用方从程序集里取，Core 不替谁决定"我是谁"）。</summary>
    public string CurrentVersion { get; set; } = "未知";

    /// <summary>完整的一行构建信息，报问题时贴它。</summary>
    public string BuildDescription { get; set; } = string.Empty;

    /// <summary>当前版本一行字。</summary>
    public string CurrentVersionText => $"当前版本 v{CurrentVersion}";

    /// <summary>要下载哪个附件。留空表示只提示、不直接给下载按钮。</summary>
    public string? PreferredAssetName { get; set; }

    /// <summary>
    /// 按后缀挑附件，例如 <c>.apk</c>。
    ///
    /// 为什么需要它：附件名里带着版本号（<c>classshout-teacher-1.10.0-universal.apk</c>），
    /// 而"我要的是哪个包"这件事跟版本号无关 —— 精确匹配会因为版本号变了就找不到。
    /// </summary>
    public string? PreferredAssetSuffix { get; set; }

    /// <summary>镜像列表（内置的 + 自己加的），界面直接绑它。</summary>
    public ObservableCollection<MirrorRow> Mirrors { get; } = [];

    // ======================== 命令 ========================

    /// <summary>用当前选中的镜像检查更新。</summary>
    public ICommand CheckCommand { get; }

    /// <summary>一键把**所有**镜像并发测一遍。</summary>
    public ICommand TestAllCommand { get; }

    public ICommand OpenDownloadCommand { get; }

    public ICommand OpenPageCommand { get; }

    public ICommand SaveCommand { get; }

    /// <summary>把下面填的那条镜像加进列表。</summary>
    public ICommand AddMirrorCommand { get; }

    // ======================== 新增镜像的表单 ========================

    public string NewLabel
    {
        get => _newLabel;
        set
        {
            if (Set(ref _newLabel, value))
            {
                (AddMirrorCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>新镜像的 API 地址（留空＝用该类型的默认地址）。</summary>
    public string NewApiBase
    {
        get => _newApiBase;
        set => Set(ref _newApiBase, value);
    }

    /// <summary>新镜像上的仓库（留空＝与默认仓库相同）。</summary>
    public string NewRepository
    {
        get => _newRepository;
        set => Set(ref _newRepository, value);
    }

    /// <summary>新镜像的下载地址模板（留空＝用该站原地址）。</summary>
    public string NewTemplate
    {
        get => _newTemplate;
        set => Set(ref _newTemplate, value);
    }

    /// <summary>新镜像是不是 Gitee。</summary>
    public bool NewIsGitee
    {
        get => _newIsGitee;
        set => Set(ref _newIsGitee, value);
    }

    /// <summary>「一键检测」之后的一句话汇总。</summary>
    public string TestSummary
    {
        get => _testSummary;
        private set => Set(ref _testSummary, value);
    }

    // ======================== 代理 ========================

    /// <summary>代理三档：跟随系统 / 不使用 / 自定义。</summary>
    public IReadOnlyList<ProxyModeOption> ProxyModes { get; } =
    [
        new(UpdateProxyMode.System, "跟随系统代理"),
        new(UpdateProxyMode.None, "不使用代理"),
        new(UpdateProxyMode.Custom, "自己填代理地址"),
    ];

    /// <summary>当前选中的代理档位。</summary>
    public ProxyModeOption? SelectedProxyMode
    {
        get => ProxyModes.FirstOrDefault(option => option.Mode == _settings.ProxyMode);
        set
        {
            if (value is null || _settings.ProxyMode == value.Mode)
            {
                return;
            }

            _settings.ProxyMode = value.Mode;
            LocalSettings.SaveUpdate(_settings);
            Raise();
            Raise(nameof(ProxyHintText));
            Raise(nameof(IsCustomProxy));
        }
    }

    /// <summary>自定义代理地址，例如 http://127.0.0.1:7890。</summary>
    public string ProxyUrl
    {
        get => _settings.ProxyUrl;
        set
        {
            if (_settings.ProxyUrl == value)
            {
                return;
            }

            _settings.ProxyUrl = value;
            LocalSettings.SaveUpdate(_settings);
            Raise();
            Raise(nameof(ProxyHintText));
        }
    }

    /// <summary>能不能填代理地址（只有选了"自己填"才显示输入框）。</summary>
    public bool IsCustomProxy => _settings.ProxyMode == UpdateProxyMode.Custom;

    /// <summary>
    /// 当前实际会走什么。
    ///
    /// 这一行是刻意显示的：代理没生效时（系统里挂着代理、应用读不到）它会写
    /// "系统没有配置代理（直连）" —— 比让用户对着"连不上"猜要省事得多。
    /// </summary>
    public string ProxyHintText => _checker.CurrentProxy.Description;

    // ======================== 状态 ========================

    /// <summary>检查中。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                (CheckCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>正在检测所有镜像。</summary>
    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (Set(ref _isTesting, value))
            {
                (TestAllCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>结果或错误的一句话。</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>查到的新版本号（没查到或已是最新时为空）。</summary>
    public string LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (Set(ref _latestVersion, value))
            {
                Raise(nameof(UpdateHintText));
            }
        }
    }

    /// <summary>发行说明原文（只给一小段，全文在发行版页面上）。</summary>
    public string Notes
    {
        get => _notes;
        private set => Set(ref _notes, value);
    }

    /// <summary>有新版本时的一句话。</summary>
    public string UpdateHintText => LatestVersion.Length > 0
        ? $"有新版本 v{LatestVersion}（当前 v{CurrentVersion}）"
        : string.Empty;

    /// <summary>能不能直接下载。</summary>
    public bool CanDownload => _download is not null;

    /// <summary>能不能打开发行版页面。</summary>
    public bool CanOpenPage => !string.IsNullOrWhiteSpace(_pageUrl) && _openUrl is not null;

    /// <summary>有没有可下载的东西（界面据此显示按钮）。</summary>
    public bool HasDownload => CanDownload;

    /// <summary>上次检查时间。</summary>
    public string LastCheckedText => _settings.LastCheckedAt is { } at
        ? $"上次检查：{at.ToLocalTime():MM-dd HH:mm}"
        : "还没检查过。";

    // ======================== 动作 ========================

    /// <summary>用当前选中的镜像查一次，结果记进设置。</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在通过「{_settings.SelectedMirror.Label}」检查…";
        LatestVersion = string.Empty;
        Notes = string.Empty;
        _download = null;
        _pageUrl = null;
        RaiseResults();

        try
        {
            var result = await _checker.CheckAsync(CurrentVersion, cancellationToken).ConfigureAwait(true);

            _settings.LastCheckedAt = DateTimeOffset.Now;
            ApplyMirrorResult(new MirrorTestResult(
                _settings.SelectedMirror.Id,
                _settings.SelectedMirror.Label,
                result.Ok,
                result.LatencyMs,
                result.LatestVersion,
                result.Error));

            if (!result.Ok)
            {
                Status = $"{result.Error}（走的是「{result.MirrorLabel}」，{result.ProxyDescription}）";
                return;
            }

            _settings.LatestVersion = result.LatestVersion;
            _pageUrl = result.PageUrl;

            if (result.HasUpdate)
            {
                LatestVersion = result.LatestVersion ?? string.Empty;
                Notes = Summarize(result.Notes);
                _download = PickAsset(result);

                Status = _download is not null
                    ? $"发现新版本 v{result.LatestVersion}（{result.MirrorLabel} · {result.LatencyMs} ms · {result.ProxyDescription}）"
                    : $"发现新版本 v{result.LatestVersion}（{result.MirrorLabel} · {result.LatencyMs} ms）";
            }
            else
            {
                Status = $"已经是最新的 v{CurrentVersion}（{result.MirrorLabel} · {result.LatencyMs} ms）。";
            }
        }
        finally
        {
            LocalSettings.SaveUpdate(_settings);
            IsBusy = false;
            Raise(nameof(LastCheckedText));
            RaiseResults();
        }
    }

    /// <summary>
    /// 一键检测所有镜像：**并发**发出去。
    ///
    /// 串行检测的话，六条源里有一条不通就要等它超时（10 秒起），
    /// 而用户按下这个按钮想要的正是"立刻知道哪条能用"。
    /// </summary>
    public async Task TestAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsTesting)
        {
            return;
        }

        IsTesting = true;
        TestSummary = $"正在并发检测 {Mirrors.Count} 条镜像…";

        try
        {
            var results = await _checker.TestAllAsync(_settings.Mirrors, cancellationToken).ConfigureAwait(true);

            foreach (var result in results)
            {
                ApplyMirrorResult(result);
            }

            var ok = results.Where(r => r.Ok).OrderBy(r => r.LatencyMs).ToList();

            TestSummary = ok.Count == 0
                ? $"{results.Count} 条镜像全部不通。检查一下代理设置，或者换个网络再试。"
                : $"通 {ok.Count}/{results.Count} 条，最快的是「{ok[0].Label}」（{ok[0].LatencyMs} ms）"
                  + (ok[0].LatestVersion is { Length: > 0 } version ? $"，最新 v{version}" : string.Empty)
                  + "。已经自动切过去了。";

            // 最快的自动选中：检测的目的就是挑一条能用的
            if (ok.Count > 0)
            {
                _settings.SelectedMirrorId = ok[0].MirrorId;
            }
        }
        finally
        {
            LocalSettings.SaveUpdate(_settings);
            RefreshMirrors();
            IsTesting = false;
        }
    }

    /// <summary>切到某条镜像（点「用这个」）。</summary>
    public void SelectMirror(MirrorRow row)
    {
        _settings.SelectedMirrorId = row.Mirror.Id;
        LocalSettings.SaveUpdate(_settings);
        RefreshMirrors();

        Status = $"已切换到「{row.Mirror.Label}」。点「检查更新」试试。";
    }

    /// <summary>删掉某条自定义镜像（内置的删不掉，它们是兜底的）。</summary>
    public void RemoveMirror(MirrorRow row)
    {
        if (row.Mirror.BuiltIn)
        {
            Status = "内置镜像不能删 —— 它们是兜底的那几条。可以改，或者加一条自己的。";
            return;
        }

        _settings.Mirrors.RemoveAll(mirror => string.Equals(mirror.Id, row.Mirror.Id, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(_settings.SelectedMirrorId, row.Mirror.Id, StringComparison.OrdinalIgnoreCase))
        {
            _settings.SelectedMirrorId = _settings.Mirrors.FirstOrDefault()?.Id;
        }

        LocalSettings.SaveUpdate(_settings);
        RefreshMirrors();
        Status = $"已删除「{row.Mirror.Label}」。";
    }

    /// <summary>把表单里填的那条镜像加进来。</summary>
    public void AddMirror()
    {
        var label = NewLabel.Trim();

        if (label.Length == 0)
        {
            Status = "先给这条镜像起个名字。";
            return;
        }

        var mirror = new UpdateMirror
        {
            Label = label,
            Provider = NewIsGitee ? UpdateMirrorProvider.Gitee : UpdateMirrorProvider.GitHub,
            ApiBase = NewApiBase.Trim(),
            Repository = NewRepository.Trim(),
            DownloadTemplate = NewTemplate.Trim(),
            BuiltIn = false,
        }.Normalized();

        _settings.Mirrors.Add(mirror);
        _settings.SelectedMirrorId = mirror.Id;

        LocalSettings.SaveUpdate(_settings);

        NewLabel = string.Empty;
        NewApiBase = string.Empty;
        NewRepository = string.Empty;
        NewTemplate = string.Empty;

        RefreshMirrors();
        Status = $"已添加「{mirror.Label}」并选中。点「一键检测全部镜像」可以试试它通不通。";
    }

    /// <summary>打开下载地址（镜像已在检查时换算好）。</summary>
    public async Task OpenDownloadAsync()
    {
        if (_download is null || _openUrl is null)
        {
            return;
        }

        await _openUrl(_download.Url).ConfigureAwait(true);
        Status = $"已在浏览器里打开 {_download.Name} 的下载地址（走的是「{_settings.SelectedMirror.Label}」）。";
    }

    /// <summary>打开发行版页面。</summary>
    public async Task OpenPageAsync()
    {
        if (string.IsNullOrWhiteSpace(_pageUrl) || _openUrl is null)
        {
            return;
        }

        await _openUrl(_pageUrl).ConfigureAwait(true);
    }

    /// <summary>保存设置。</summary>
    public void Save()
    {
        _settings = _settings.Normalized();
        LocalSettings.SaveUpdate(_settings);
        RefreshMirrors();
        Status = "设置已保存。";
        Raise(nameof(LastCheckedText));
        Raise(nameof(ProxyHintText));
    }

    // ======================== 内部 ========================

    private void RefreshMirrors()
    {
        Mirrors.Clear();

        foreach (var mirror in _settings.Mirrors)
        {
            Mirrors.Add(new MirrorRow(
                mirror,
                isSelected: string.Equals(mirror.Id, _settings.SelectedMirrorId, StringComparison.OrdinalIgnoreCase),
                select: SelectMirror,
                remove: RemoveMirror));
        }

        Raise(nameof(IsCustomProxy));
        Raise(nameof(ProxyHintText));
    }

    private void ApplyMirrorResult(MirrorTestResult result)
    {
        var mirror = _settings.Mirrors
            .FirstOrDefault(m => string.Equals(m.Id, result.MirrorId, StringComparison.OrdinalIgnoreCase));

        if (mirror is null)
        {
            return;
        }

        mirror.LastOk = result.Ok;
        mirror.LastLatencyMs = result.LatencyMs;
        mirror.LastVersion = result.LatestVersion;
        mirror.LastError = result.Error;
        mirror.LastTestedAt = DateTimeOffset.Now;

        Mirrors
            .FirstOrDefault(row => string.Equals(row.Mirror.Id, result.MirrorId, StringComparison.OrdinalIgnoreCase))
            ?.Refresh();
    }

    private UpdateAsset? PickAsset(UpdateCheckResult result)
    {
        var exact = PreferredAssetName is { Length: > 0 }
            ? result.FindAsset(PreferredAssetName)
            : null;

        return exact
               ?? (PreferredAssetSuffix is { Length: > 0 } ? result.FindAssetEndingWith(PreferredAssetSuffix) : null);
    }

    private void RaiseResults()
    {
        Raise(nameof(CanDownload));
        Raise(nameof(CanOpenPage));
        Raise(nameof(HasDownload));
        Raise(nameof(IsCustomProxy));
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

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>代理档位的一项（下拉框用）。</summary>
/// <param name="Mode">档位。</param>
/// <param name="Label">显示名。</param>
public sealed record ProxyModeOption(UpdateProxyMode Mode, string Label);

/// <summary>
/// 镜像列表里的一行。
///
/// 命令挂在行自己身上，XAML 模板里就不必写父级转换绑定
/// （和这个项目里其它列表一个写法）。
/// </summary>
public sealed class MirrorRow : INotifyPropertyChanged
{
    public MirrorRow(UpdateMirror mirror, bool isSelected, Action<MirrorRow> select, Action<MirrorRow> remove)
    {
        Mirror = mirror;
        IsSelected = isSelected;

        SelectCommand = new SimpleCommand(() => select(this));
        RemoveCommand = new SimpleCommand(() => remove(this));

        // 内置的不能删 —— 它们是"一条源都不剩"时的那几条兜底
        CanRemove = !mirror.BuiltIn;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateMirror Mirror { get; }

    public string Label => Mirror.Label;

    /// <summary>当前用的是不是这条。</summary>
    public bool IsSelected { get; }

    /// <summary>不能删的就是内置的那几条。</summary>
    public bool CanRemove { get; }

    /// <summary>「Gitee」/「GitHub」+ 仓库 + 下载方式，一眼看出这条通向哪里。</summary>
    public string DetailText
    {
        get
        {
            var repository = string.IsNullOrWhiteSpace(Mirror.Repository) ? "（与默认仓库相同）" : Mirror.Repository;
            var template = string.IsNullOrWhiteSpace(Mirror.DownloadTemplate) ? "原地址" : Mirror.DownloadTemplate;

            return $"{Mirror.ProviderLabel} · {repository} · 下载：{template}";
        }
    }

    /// <summary>检测结果那一行。</summary>
    public string ResultText => Mirror.ResultText;

    /// <summary>检测结果通不通（界面用它上色）。</summary>
    public bool IsOk => Mirror.LastOk == true;

    public bool IsBad => Mirror.LastOk == false;

    public ICommand SelectCommand { get; }

    public ICommand RemoveCommand { get; }

    /// <summary>检测结果更新之后通知界面重画（对象还是同一个）。</summary>
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOk)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBad)));
    }
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
