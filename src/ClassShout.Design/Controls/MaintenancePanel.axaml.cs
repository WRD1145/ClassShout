using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassShout.Core.Remote;

namespace ClassShout.Design.Controls;

public partial class MaintenancePanel : UserControl
{
    private DiagnosticReport? _report;
    private PortableConfiguration? _pending;
    private bool _pendingClassroom;
    private CancellationTokenSource? _cancellation;
    public MaintenancePanel()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => _cancellation?.Cancel();
    }
    private void Busy(bool busy)
    {
        Test.IsEnabled = Export.IsEnabled = Import.IsEnabled = !busy;
        Report.IsEnabled = !busy && _report is not null;
        Confirmation.IsEnabled = !busy;
    }
    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IMaintenanceContext context) return;
        _cancellation?.Dispose();
        _cancellation = new();
        Busy(true);
        _report = null;
        Results.Text = "正在逐项测试…";
        try
        {
            var items = await context.DiagnoseAsync(_cancellation.Token);
            _report = new(1, DateTimeOffset.UtcNow, context.IsClassroomMaintenance ? "classroom" : "teacher", items);
            Results.Text = string.Join(Environment.NewLine, items.Select(item => $"{item.Component}：{item.Status} — {item.Detail}（{item.ElapsedMs} ms）"));
        }
        catch (OperationCanceledException) { Results.Text = "诊断已取消。"; }
        catch (Exception) { Results.Text = "诊断未完成；请检查应用状态后重试。"; }
        finally { Busy(false); }
    }
    private async Task SaveAsync(byte[] bytes, string prefix)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存 ClassShout 文件", SuggestedFileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            DefaultExtension = "json", ShowOverwritePrompt = true,
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        Status.Text = "文件已保存。";
    }
    private async void OnReport(object? sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        Busy(true);
        try { await SaveAsync(JsonSerializer.SerializeToUtf8Bytes(_report, new JsonSerializerOptions { WriteIndented = true }), "ClassShout-diagnostics"); }
        catch (Exception) { Status.Text = "报告保存失败，请检查目标文件权限。"; }
        finally { Busy(false); }
    }
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IMaintenanceContext context) return;
        Busy(true);
        try { await SaveAsync(ConfigurationTransfer.Export(context.IsClassroomMaintenance), "ClassShout-backup"); }
        catch (Exception) { Status.Text = "备份失败，请检查配置格式、大小和文件权限。"; }
        finally { Busy(false); }
    }
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IMaintenanceContext context || TopLevel.GetTopLevel(this) is not { } top) return;
        Busy(true);
        _pending = null;
        Confirmation.IsVisible = false;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "选择 ClassShout 配置备份", AllowMultiple = false });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(bytes);
                if (count == 0) break;
                if (buffer.Length + count > ConfigurationTransfer.MaxBytes) throw new InvalidDataException();
                buffer.Write(bytes, 0, count);
            }
            PrepareImport(buffer.ToArray());
        }
        catch (Exception) { Status.Text = "备份无效、版本不支持或文件超过 16 MiB；没有修改配置。"; }
        finally { Busy(false); }
    }
    /// <summary>先展示覆盖预览，只有用户点击确认才写入；供文件选择和其他导入入口共用。</summary>
    public void PrepareImport(byte[] bytes)
    {
        if (DataContext is not IMaintenanceContext context) throw new InvalidOperationException("当前端尚未就绪。");
        _pending = null;
        Confirmation.IsVisible = false;
        if (!context.CanImportConfiguration) { Status.Text = "请先解锁受保护的朗读设置，再导入配置。"; return; }
        var backup = ConfigurationTransfer.Parse(bytes);
        _pendingClassroom = context.IsClassroomMaintenance;
        _pending = backup;
        Preview.Text = ConfigurationTransfer.Describe(backup, _pendingClassroom);
        Confirmation.IsVisible = true;
    }
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_pending is null || DataContext is not IMaintenanceContext context || context.IsClassroomMaintenance != _pendingClassroom) return;
        if (!context.CanImportConfiguration)
        {
            _pending = null; Confirmation.IsVisible = false;
            Status.Text = "请先解锁受保护的朗读设置，再导入配置。";
            return;
        }
        try
        {
            ConfigurationTransfer.Import(_pending, _pendingClassroom);
            Status.Text = "配置已导入。请重启应用后使用；服务地址、密钥和登录信息保留本机值。";
        }
        catch (AggregateException) { Status.Text = "导入失败且部分配置回滚失败，请检查配置目录权限后恢复原配置。"; }
        catch (Exception) { Status.Text = "导入失败，已尝试恢复原配置；请检查配置目录权限。"; }
        finally { _pending = null; Confirmation.IsVisible = false; }
    }
    private void OnCancel(object? sender, RoutedEventArgs e) { _pending = null; Confirmation.IsVisible = false; }
}
