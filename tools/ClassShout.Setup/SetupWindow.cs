using System.Text;

namespace ClassShout.Setup;

internal sealed record SetupChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

internal sealed class SetupWindow : Form
{
    private readonly TextBox _target = new() { Dock = DockStyle.Fill };
    private readonly ListBox _action = new() { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28 };
    private readonly Label _detected = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _hint = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "请选择操作。" };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 246, 252) };
    private readonly CheckBox _launch = new() { Text = "完成后启动 ClassShout", Checked = true, AutoSize = true };
    private readonly Button _go = new() { AutoSize = true, Padding = new Padding(18, 6, 18, 6), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(103, 80, 164), ForeColor = Color.White };
    private readonly Button _browse = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _close = new() { Text = "关闭", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 10, Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly Func<string, bool> _confirm;
    private readonly string _requested;
    private bool _refreshing;
    internal bool Busy { get; private set; }
    internal int ResultCode { get; private set; }
    internal IReadOnlyList<string> AvailableActions => _action.Items.Cast<SetupChoice>().Select(x => x.Key).ToArray();
    internal string TargetPath => _target.Text;
    internal bool CanExecute => _go.Enabled;
    internal void SetLaunchAfter(bool value) => _launch.Checked = value;

    internal SetupWindow(string target, string requested = "install", Func<string, bool>? confirm = null)
    {
        _requested = requested;
        _confirm = confirm ?? (message => MessageBox.Show(this, message, "确认操作", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
        Text = Program.CurrentInfo.Title + " · 安装与维护";
        ClientSize = new Size(690, 610);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = Color.White;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
        for (var i = 0; i < 9; i++) root.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.AutoSize, i == 6 ? 100 : 0));
        root.Controls.Add(new Label { Text = Program.CurrentInfo.Title, AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold), ForeColor = Color.FromArgb(79, 55, 139), Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        root.Controls.Add(new Label { Text = "安装包版本：" + Program.CurrentInfo.Version + "　·　仅操作当前用户，无需管理员权限", AutoSize = true, Margin = new Padding(0, 0, 0, 14) }, 0, 1);
        root.Controls.Add(_detected, 0, 2);
        var directory = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 12, 0, 12) };
        directory.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); directory.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        directory.Controls.Add(_target, 0, 0); directory.Controls.Add(_browse, 1, 0); root.Controls.Add(directory, 0, 3);
        root.Controls.Add(_action, 0, 4); _hint.Margin = new Padding(0, 10, 0, 12); root.Controls.Add(_hint, 0, 5);
        root.Controls.Add(_log, 0, 6);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 3, Margin = new Padding(0, 10, 0, 0) };
        footer.Controls.Add(_progress, 0, 0); footer.Controls.Add(_status, 0, 1); footer.Controls.Add(_launch, 0, 2); root.Controls.Add(footer, 0, 7);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_go); buttons.Controls.Add(_close); root.Controls.Add(buttons, 0, 8);
        Controls.Add(root);
        _action.DrawItem += (_, e) =>
        {
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? Color.FromArgb(103, 80, 164) : Color.White);
            e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index >= 0 && e.Index < _action.Items.Count) TextRenderer.DrawText(e.Graphics, ((SetupChoice)_action.Items[e.Index]).Label, Font, e.Bounds, selected ? Color.White : Color.Black, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            e.DrawFocusRectangle();
        };
        _target.Text = target;
        _target.TextChanged += (_, _) => RefreshState();
        _action.SelectedIndexChanged += (_, _) => UpdateAction();
        _browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择空目录或此应用已有的安装目录", SelectedPath = _target.Text, UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) _target.Text = dialog.SelectedPath; };
        _go.Click += (_, _) => BeginOperation(); _close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (Busy) { e.Cancel = true; _status.Text = "操作正在进行，请等待完成后关闭。"; } };
        RefreshState();
    }

    internal void SelectAction(string key)
    {
        _action.SelectedItem = _action.Items.Cast<SetupChoice>().Single(item => item.Key == key);
    }
    internal void ClickAction() => _go.PerformClick();
    internal void RefreshState()
    {
        if (Busy) return;
        try
        {
            var installed = Program.ReadManagedVersion(_target.Text);
            var old = (_action.SelectedItem as SetupChoice)?.Key;
            _refreshing = true; _action.Items.Clear();
            if (installed is null)
            {
                _action.Items.Add(new SetupChoice("install", "安装"));
                if (_requested == "uninstall") _action.Items.Add(new SetupChoice("uninstall", "卸载 / 清理安装登记"));
                _detected.Text = "尚未安装。请选择程序安装目录。";
            }
            else
            {
                _action.Items.Add(new SetupChoice("update", "更新"));
                if (Program.SameVersion(installed, Program.CurrentInfo.Version)) _action.Items.Add(new SetupChoice("repair", "修复"));
                _action.Items.Add(new SetupChoice("uninstall", "卸载"));
                _detected.Text = $"已安装版本：{installed}。请选择更新、卸载；同版本也可以修复。";
            }
            var keys = AvailableActions;
            _action.Height = _action.Items.Count * _action.ItemHeight + 6;
            var preferred = old is not null && keys.Contains(old) ? old : keys.Contains(_requested) ? _requested : keys.Contains("repair") ? "repair" : keys[0];
            SelectAction(preferred);
            _target.ReadOnly = installed is not null; _browse.Enabled = installed is null;
            _refreshing = false; UpdateAction();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { _refreshing = false; _detected.Text = ex.Message; _go.Enabled = false; }
    }

    private void UpdateAction()
    {
        if (_refreshing || Busy || _action.SelectedItem is not SetupChoice choice) return;
        _go.Text = choice.Label;
        _launch.Enabled = choice.Key != "uninstall";
        _hint.Text = choice.Key switch
        {
            "repair" => "重新写入本版程序文件并恢复快捷方式，保留配置和日志。操作前会关闭此目录中的程序。",
            "uninstall" => "仅移除安装器管理的程序文件；保留配置、日志、个人文件和维护安装包。",
            _ => "将安装此包版本；更新会先关闭此目录中的程序。配置和日志保留。",
        };
        var downgrade = choice.Key != "uninstall" && Program.IsDowngrade(Program.ReadManagedVersion(_target.Text));
        _go.Enabled = Program.HasPayload && !downgrade;
        if (downgrade) _hint.Text = "已安装较新版本，不能用旧包覆盖。请使用较新安装包或选择卸载。";
        if (!Program.HasPayload) _hint.Text = "此构建没有嵌入应用，仅供开发查看，不能执行安装。";
    }

    private void BeginOperation()
    {
        if (Busy || _action.SelectedItem is not SetupChoice choice) return;
        var target = _target.Text;
        if (choice.Key != "install" && !_confirm($"确认{choice.Label} {Program.CurrentInfo.Title}？\n目录：{target}\n正在运行的此目录程序将关闭；配置和日志保留。")) return;
        Busy = true; _go.Enabled = _browse.Enabled = _action.Enabled = _close.Enabled = _target.Enabled = _launch.Enabled = false;
        _progress.Visible = true; _status.ForeColor = Color.Black; _status.Text = "正在" + choice.Label + "，请稍候…";
        var launch = choice.Key != "uninstall" && _launch.Checked;
        var worker = new Thread(() =>
        {
            var code = 1;
            try { Program.RequestedTarget = target; Program.GuiMode = true; Program.LaunchRequested = launch; code = Program.ExecuteAction(choice.Key, true); }
            catch (Exception ex) { Console.WriteLine("操作未完成：" + ex.Message); }
            var result = code;
            Post(() =>
            {
                ResultCode = result; Busy = false; _progress.Visible = false;
                _action.Enabled = _close.Enabled = _target.Enabled = true;
                _status.Text = result == 0 ? choice.Label + "完成。详细结果见上方。" : "操作未完成，请查看详情并重试。";
                _status.ForeColor = result == 0 ? Color.FromArgb(37, 94, 56) : Color.Firebrick;
                RefreshState();
            });
        }) { IsBackground = true, Name = "安装维护" };
        // 快捷方式使用 COM，工作线程同样保持 STA；界面线程始终可响应。
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    internal void AppendLog(string value) => Post(() => { if (_log.TextLength > 200000) _log.Clear(); _log.AppendText(value); });
    internal void CaptureView(string path) { using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
}

internal sealed class GuiLogWriter(Action<string> append, TextWriter original) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(string? value) { if (value is not null) { append(value); original.Write(value); } }
    public override void WriteLine(string? value) => Write(value + Environment.NewLine);
    public override void Write(char value) => Write(value.ToString());
}

public static partial class Program
{
    private static int RunGui(string command)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            command = command switch { "setup" or "i" => "install", "u" => "update", "x" or "remove" => "uninstall", "install" or "update" or "repair" or "uninstall" => command, _ => throw new ArgumentException("不认识的维护命令。") };
            if (HasPayload) ValidateInfo();
            using var window = new SetupWindow(SuggestedTarget, command);
            if (SuppressLaunch) window.SetLaunchAfter(false);
            var output = Console.Out;
            try { Console.SetOut(new GuiLogWriter(window.AppendLog, output)); Application.Run(window); }
            finally { Console.SetOut(output); }
            return window.ResultCode;
        }
        catch (Exception ex) { MessageBox.Show("无法打开安装程序：" + ex.Message, "ClassShout", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
