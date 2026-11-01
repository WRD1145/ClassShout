using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassShout.Core.Remote;

namespace ClassShout.Design.Controls;

public partial class PluginManagerPanel : UserControl
{
    private IPluginManagementContext? _context;
    private PluginManager? _manager;
    private INotifyPropertyChanged? _observed;
    private PluginPackage? _pending;
    private string? _removeId;
    private bool _busy;
    private string? _error;
    public PluginManagerPanel()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Bind();
        DataContextChanged += (_, _) => Bind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }
    private void Unbind()
    {
        if (_manager is not null) _manager.Changed -= OnChanged;
        if (_observed is not null) _observed.PropertyChanged -= OnContextChanged;
        _manager = null; _context = null; _observed = null;
    }
    private void Bind()
    {
        if (_context is not null && ReferenceEquals(DataContext, _context) && _manager is not null) { Refresh(); return; }
        Unbind();
        _pending = null; _removeId = null; _error = null;
        Confirmation.IsVisible = false;
        if (DataContext is not IPluginManagementContext context || !context.SupportsPlugins) { IsVisible = false; return; }
        IsVisible = true;
        _context = context;
        _manager = context.Plugins;
        _manager.Changed += OnChanged;
        _observed = context as INotifyPropertyChanged;
        if (_observed is not null) _observed.PropertyChanged += OnContextChanged;
        Refresh();
    }
    private void OnContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsSpeechLocked" or "HasLockedAreas") Refresh();
    }
    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);
    public void Refresh()
    {
        if (_manager is null || _context is null) return;
        Install.IsEnabled = !_busy && _context.CanManagePlugins;
        Search.IsEnabled = !_busy;
        Confirm.IsEnabled = !_busy && _context.CanManagePlugins;
        Rows.Children.Clear();
        var all = _manager.Items;
        var query = Search.Text?.Trim() ?? "";
        var shown = all.Where(item => (item.Name + " " + item.Author + " " + item.Id).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Summary.Text = $"已安装 {all.Count} 个 · 运行中 {all.Count(item => item.Running)} 个" + (shown.Length == 0 ? " · 没有匹配的插件" : "");
        foreach (var item in shown)
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(new TextBlock { Text = item.Name + "  " + item.Version, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = item.Author + " · " + item.Status, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = item.Description, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            Button Action(string label, Action clicked, bool enabled = true)
            {
                var button = new Button { Content = label, IsEnabled = !_busy && enabled, Margin = new Thickness(0, 0, 6, 4) };
                button.Classes.Add("outlined"); button.Click += (_, _) => clicked(); actions.Children.Add(button); return button;
            }
            Action("详情", () => Details.Text = $"{item.Name} · {item.Id}\n版本：{item.Version} · 作者：{item.Author}\n{item.Description}\n来源：{item.Source}\n插件目录：{_manager.DirectoryPath}");
            Action(item.ToggleLabel, () => Run(() => _manager.SetEnabledAsync(item.Id, !item.Enabled)), item.CanManage);
            if (item.Enabled) Action("重启", () => Run(() => _manager.RestartAsync(item.Id)), item.CanManage);
            Action("卸载", () =>
            {
                try { PrepareUninstall(item.Id); }
                catch (InvalidOperationException ex) { _error = ex.Message; Refresh(); }
            }, item.CanManage);
            body.Children.Add(actions);
            var card = new Md3Card { Content = body }; card.Classes.Add("outlined"); Rows.Children.Add(card);
        }
        Status.Text = _error ?? (_manager.Status + (_context.CanManagePlugins ? "" : " 请先解锁受保护的插件设置。"));
    }
    private async void Run(Func<Task> action)
    {
        if (_busy || _manager is null) return;
        _busy = true; _error = null; Refresh();
        string? failure = null;
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { failure = ex.Message; }
        catch (Exception) { failure = "插件操作失败，请检查插件文件、运行环境和配置目录权限。"; }
        finally { _busy = false; _error = failure; Refresh(); }
    }
    public void PrepareInstall(byte[] bytes)
    {
        if (_manager is null || _context?.CanManagePlugins != true) throw new InvalidOperationException("请先解锁受保护的插件设置。");
        _pending = null; _removeId = null; Confirmation.IsVisible = false;
        var package = PluginPackage.Read(bytes, _manager.Role);
        _pending = package;
        var manifest = package.Manifest;
        Preview.Text = $"{manifest.Name} · {manifest.Version}\n作者：{manifest.Author}\n{manifest.Description}\n标识：{manifest.Id}\n将安装或升级托管插件；升级会释放旧版本 DI 作用域，新版本保持禁用。作者为清单自声明，未校验插件签名。程序集隔离不是安全沙箱，启用后具有当前用户权限，请确认来源可信。";
        Confirm.Content = "确认安装（默认禁用）";
        Confirmation.IsVisible = true;
    }
    public void PrepareUninstall(string id)
    {
        if (_manager is null || _context?.CanManagePlugins != true) throw new InvalidOperationException("请先解锁受保护的插件设置。");
        var item = _manager.Items.Single(item => item.Id == id);
        _pending = null; _removeId = id;
        Preview.Text = $"卸载 {item.Name}？插件会先停止。" + (item.ManagedPackage ? "安装文件将删除，独立 plugin-data 数据目录保留。" : "仅删除注册，不删除旧版外部程序。");
        Confirm.Content = "确认卸载"; Confirmation.IsVisible = true;
    }
    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (_busy || TopLevel.GetTopLevel(this) is not { } top) return;
        _busy = true; _error = null; Refresh();
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "选择 ClassShout 插件包", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ClassShout 插件包") { Patterns = ["*.cspx", "*.zip"] }] });
            if (files.Count == 0) return;
            await using var input = await files[0].OpenReadAsync();
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer)) > 0)
            {
                if (bytes.Length + count > PluginPackage.MaxBytes) throw new InvalidDataException("插件包超过 32 MiB。");
                bytes.Write(buffer, 0, count);
            }
            PrepareInstall(bytes.ToArray());
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { _error = "插件包格式、平台或清单无效，未安装：" + ex.Message; }
        catch (Exception) { _error = "无法读取插件包，请检查文件权限。"; }
        finally { _busy = false; Refresh(); }
    }
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var package = _pending; var id = _removeId;
        _pending = null; _removeId = null; Confirmation.IsVisible = false;
        if (_manager is null) return;
        if (package is not null) Run(() => _manager.InstallAsync(package));
        else if (id is not null) Run(() => _manager.UninstallAsync(id));
    }
    private void OnCancel(object? sender, RoutedEventArgs e) { _pending = null; _removeId = null; Confirmation.IsVisible = false; }
    private void OnSearch(object? sender, TextChangedEventArgs e) { if (_manager is not null) Refresh(); }
    private void OnRefresh(object? sender, RoutedEventArgs e) { if (!_busy) Refresh(); }
}
