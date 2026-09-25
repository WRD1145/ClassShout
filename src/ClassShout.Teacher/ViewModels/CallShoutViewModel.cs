using System.Collections.ObjectModel;
using ClassShout.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Teacher.ViewModels;

/// <summary>
/// 拼装区里的一个组件。
///
/// 可观察对象而不是直接绑 <see cref="MessageComponent"/>：文字组件的内容要能就地编辑，
/// 而"改完立刻回写模板"这件事得有个地方挂。
/// </summary>
public sealed partial class ComponentRow : ObservableObject
{
    private readonly Action _changed;

    public ComponentRow(MessageComponent component, Action changed)
    {
        Component = component;
        _changed = changed;
        _text = component.Text ?? string.Empty;
    }

    public MessageComponent Component { get; }

    public string Label => MessageComponentKinds.Label(Component.Kind);

    /// <summary>只有"文字"组件能编辑内容，其余都是取值的占位符。</summary>
    public bool IsText => Component.Kind == MessageComponentKinds.Text;

    [ObservableProperty]
    private string _text;

    partial void OnTextChanged(string value)
    {
        Component.Text = value;
        _changed();
    }

    public IRelayCommand? RemoveCommand { get; set; }

    public IRelayCommand? MoveLeftCommand { get; set; }

    public IRelayCommand? MoveRightCommand { get; set; }
}

/// <summary>
/// 学生选择列表里的一行。
///
/// 「显示什么」与「能不能选」分开：列表可以按学号显示，而没填学号的学生就不该出现在
/// 这个列表的可选项里 —— 否则老师会选到一个显示为空的条目，
/// 发出去一条没有学号的消息，而他要到教室里才发现。
/// </summary>
public sealed partial class StudentPickRow : ObservableObject
{
    private readonly Action<StudentPickRow> _changed;

    public StudentPickRow(Student student, string labelStyle, bool isSelected, Action<StudentPickRow> changed)
    {
        Student = student;
        LabelStyle = labelStyle;
        _changed = changed;
        _isSelected = isSelected && IsSelectable;

        Display = labelStyle switch
        {
            StudentLabelStyles.StudentNo => student.StudentNo ?? string.Empty,
            StudentLabelStyles.ShortName => student.ShortName ?? string.Empty,
            _ => student.Name,
        };

        Detail = StudentLabel.Format(student.Name, student.StudentNo, student.ShortName, student.Group);
    }

    public Student Student { get; }

    public string LabelStyle { get; }

    /// <summary>按当前样式显示的内容。</summary>
    public string Display { get; }

    /// <summary>这个样式下有没有可显示的内容。没填的字段不该出现在这个列表里。</summary>
    public bool IsSelectable => Display.Length > 0;

    public bool IsUnavailable => !IsSelectable;

    /// <summary>完整标识，作为副标题显示 —— 只看学号认不出是谁。</summary>
    public string Detail { get; }

    public string UnavailableHint => IsSelectable ? string.Empty : $"没有{StudentLabelStyles.Label(LabelStyle)}";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed(this);
}

/// <summary>模板下拉里的一项。名字为 null 的那一项表示"当前这份还没保存的改动"。</summary>
public sealed class TemplateChoice
{
    public TemplateChoice(CallTemplate? template, string label)
    {
        Template = template;
        Label = label;
    }

    public CallTemplate? Template { get; }

    public string Label { get; }
}

/// <summary>
/// 快速呼叫页。
///
/// 它自己不碰网络、也不碰名单文件 —— 那些由外壳注入。这样这一页的逻辑
/// （拼装、选择、模板）可以单独验证，而外壳负责"发给谁、怎么发"。
/// </summary>
public partial class CallShoutViewModel : ObservableObject
{
    private readonly TeacherCallSettings _settings;
    private readonly TeacherRosterSettings _rosterSettings;
    private readonly Func<string> _teacherNameProvider;
    private readonly Func<IReadOnlyList<string>, Task<int>> _sender;

    /// <summary>正在编辑的模板（可能是还没保存进列表的新模板）。</summary>
    private CallTemplate _draft;

    public CallShoutViewModel(
        TeacherCallSettings settings,
        TeacherRosterSettings rosterSettings,
        Func<string> teacherNameProvider,
        Func<IReadOnlyList<string>, Task<int>> sender)
    {
        _settings = settings;
        _rosterSettings = rosterSettings;
        _teacherNameProvider = teacherNameProvider;
        _sender = sender;

        if (_settings.Templates.Count == 0)
        {
            _settings.Templates.Add(TeacherCallSettings.DefaultTemplate());
            _settings.ActiveTemplateId = _settings.Templates[0].Id;
            LocalSettings.SaveCalls(_settings);
        }

        _draft = _settings.Active ?? _settings.Templates[0];
        _labelStyle = StudentLabelStyles.All.Contains(_rosterSettings.LabelStyle)
            ? _rosterSettings.LabelStyle
            : StudentLabelStyles.Name;

        ReloadTemplates();
        LoadFrom(_draft);

        // 名单读进来填进选择列表。少了这一句，页面上"叫谁"那一块会是空的，
        // 而提示行却写着"三年二班 · 4 人"—— 看起来像名单坏了，其实只是没去读它。
        LoadStudents();
    }

    // ======================== 模板 ========================

    public ObservableCollection<TemplateChoice> TemplateChoices { get; } = [];

    private TemplateChoice? _selectedChoice;

    /// <summary>
    /// 当前选中的模板。
    ///
    /// 下拉里额外挂一项「（当前未保存的改动）」：老师改完组件还没保存就切走，
    /// 改动会丢 —— 而"我刚拖进去的那个组件去哪了"是最容易让人以为软件坏了的事。
    /// 所以宁可多一项，也不要把草稿悄悄扔掉。
    /// </summary>
    public TemplateChoice? SelectedTemplate
    {
        get => _selectedChoice;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedChoice))
            {
                return;
            }

            _selectedChoice = value;
            OnPropertyChanged();

            if (value.Template is { } template)
            {
                LoadFrom(template);
            }
        }
    }

    public ObservableCollection<ComponentRow> Components { get; } = [];

    [ObservableProperty]
    private string _templateName = string.Empty;

    public bool HasComponents => Components.Count > 0;

    /// <summary>把当前组件列表存进模板列表（同一个模板则覆盖）。</summary>
    [RelayCommand]
    private void SaveTemplate()
    {
        _draft.Name = string.IsNullOrWhiteSpace(TemplateName) ? "未命名模板" : TemplateName.Trim();
        WriteComponentsBack();

        var existing = _settings.Templates.FirstOrDefault(t => t.Id == _draft.Id);

        if (existing is null)
        {
            _settings.Templates.Add(_draft);
        }
        else
        {
            existing.Name = _draft.Name;
            existing.Components = _draft.Components;
        }

        _settings.ActiveTemplateId = _draft.Id;
        LocalSettings.SaveCalls(_settings);

        ReloadTemplates();
        StatusHint = $"已保存模板「{_draft.Name}」。";
    }

    /// <summary>从空白开始做一个新模板。</summary>
    [RelayCommand]
    private void NewTemplate()
    {
        _draft = new CallTemplate { Name = "新模板", Components = [] };

        TemplateName = _draft.Name;
        LoadFrom(_draft, keepDraft: true);
        ReloadTemplates();

        StatusHint = "新模板：拖几个组件进来，然后点「保存模板」。";
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (_draft.Id.Length == 0)
        {
            return;
        }

        _settings.Templates.RemoveAll(template => template.Id == _draft.Id);
        _settings.ActiveTemplateId = _settings.Templates.FirstOrDefault()?.Id;
        LocalSettings.SaveCalls(_settings);

        _draft = _settings.Active ?? TeacherCallSettings.DefaultTemplate();
        ReloadTemplates();
        LoadFrom(_draft);

        StatusHint = "已删除该模板。";
    }

    private void ReloadTemplates()
    {
        var previous = _selectedChoice;

        TemplateChoices.Clear();

        // 草稿不在列表里（新模板、或刚改过）时，把它作为一个选项挂上去
        var draftInList = _settings.Templates.Any(template => template.Id == _draft.Id);

        foreach (var template in _settings.Templates)
        {
            TemplateChoices.Add(new TemplateChoice(template, template.Name));
        }

        if (!draftInList)
        {
            TemplateChoices.Insert(0, new TemplateChoice(null, $"（未保存）{_draft.Name}"));
        }

        _selectedChoice = TemplateChoices.FirstOrDefault(choice =>
            choice.Template?.Id == _settings.ActiveTemplateId);

        if (_selectedChoice is null)
        {
            _selectedChoice = previous is not null && TemplateChoices.Contains(previous)
                ? previous
                : TemplateChoices.FirstOrDefault();
        }

        OnPropertyChanged(nameof(SelectedTemplate));
    }

    private void LoadFrom(CallTemplate template, bool keepDraft = false)
    {
        if (!keepDraft)
        {
            _draft = template;
            _settings.ActiveTemplateId = template.Id;
            LocalSettings.SaveCalls(_settings);
        }

        TemplateName = _draft.Name;

        Components.Clear();

        foreach (var component in _draft.Components)
        {
            Components.Add(CreateRow(component));
        }

        OnComponentsChanged();
    }

    private ComponentRow CreateRow(MessageComponent component)
    {
        // 先建行、再把命令挂上去：命令要捕获这个行本身，而 lambda 里用到的
        // 局部变量在赋值之前还不能读 —— 所以声明成可空、建好之后再解引用。
        var row = new ComponentRow(component, OnComponentsChanged);

        row.RemoveCommand = new RelayCommand(() => RemoveComponent(row));
        row.MoveLeftCommand = new RelayCommand(() => MoveComponent(row, -1));
        row.MoveRightCommand = new RelayCommand(() => MoveComponent(row, 1));

        return row;
    }

    /// <summary>
    /// 组件面板：可以被点、被拖进拼装区的那几个组件。
    ///
    /// 「文字」排在第一个：它是最常用的那个（空格、标点、"来办公室"都是它），
    /// 而列表顺序就是老师扫一眼的顺序。
    /// </summary>
    public IReadOnlyList<ComponentPaletteItem> ComponentPalette { get; } =
        MessageComponentKinds.All
            .Select(kind => new ComponentPaletteItem(kind, MessageComponentKinds.Label(kind)))
            .ToList();

    // ======================== 组件操作 ========================

    /// <summary>把组件加到拼装区末尾。拖动落下来与点击组件按钮都走这里。</summary>
    [RelayCommand]
    private void AddComponent(string? kind) => AddComponentAt(kind, Components.Count);

    /// <summary>在指定位置插入一个组件。</summary>
    public void AddComponentAt(string? kind, int index)
    {
        if (string.IsNullOrWhiteSpace(kind) || !MessageComponentKinds.All.Contains(kind))
        {
            return;
        }

        // 「文字」组件进来先给一个空串：光标就在那个输入框里，直接打字即可
        var component = MessageComponent.Of(
            kind,
            kind == MessageComponentKinds.Text ? " " : null);

        index = Math.Clamp(index, 0, Components.Count);
        Components.Insert(index, CreateRow(component));
        OnComponentsChanged();
    }

    /// <summary>把已有组件挪到指定位置（拖动排序）。</summary>
    public void MoveComponentTo(ComponentRow row, int index)
    {
        var from = Components.IndexOf(row);

        if (from < 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, Components.Count - 1);

        if (from == index)
        {
            return;
        }

        Components.Move(from, index);
        OnComponentsChanged();
    }

    private void RemoveComponent(ComponentRow row)
    {
        Components.Remove(row);
        OnComponentsChanged();
    }

    private void MoveComponent(ComponentRow row, int offset)
    {
        var index = Components.IndexOf(row);
        var target = index + offset;

        if (index < 0 || target < 0 || target >= Components.Count)
        {
            return;
        }

        Components.Move(index, target);
        OnComponentsChanged();
    }

    /// <summary>组件增删改之后：回写草稿、刷新预览与发送按钮。</summary>
    private void OnComponentsChanged()
    {
        WriteComponentsBack();
        RefreshDerived();
    }

    private void WriteComponentsBack()
        => _draft.Components = Components.Select(row => row.Component).ToList();

    // ======================== 学生 ========================

    public ObservableCollection<StudentPickRow> Students { get; } = [];

    /// <summary>选择列表里用哪一种标识显示（姓名 / 学号 / 简写）。</summary>
    public IReadOnlyList<StudentLabelChoice> LabelStyles { get; } =
        StudentLabelStyles.All.Select(style => new StudentLabelChoice(style, StudentLabelStyles.Label(style))).ToList();

    private string _labelStyle = StudentLabelStyles.Name;

    public StudentLabelChoice SelectedLabelStyle
    {
        get => LabelStyles.First(choice => choice.Value == _labelStyle);
        set
        {
            if (value is null || _labelStyle == value.Value)
            {
                return;
            }

            _labelStyle = value.Value;
            _rosterSettings.LabelStyle = value.Value;
            LocalSettings.SaveRosters(_rosterSettings);

            OnPropertyChanged();
            LoadStudents();
        }
    }

    public IReadOnlyList<Student> PickedStudents =>
        Students.Where(row => row.IsSelected).Select(row => row.Student).ToList();

    public int PickedCount => Students.Count(row => row.IsSelected);

    public string PickedSummary => PickedCount == 0 ? "还没有选学生" : $"已选 {PickedCount} 人";

    public StudentRoster? Roster => _rosterSettings.Active;

    public bool HasRoster => Roster is { Students.Count: > 0 };

    public bool HasNoRoster => !HasRoster;

    public string RosterHint => Roster is { } roster
        ? $"{roster.Name} · {roster.Students.Count} 人"
        : "还没有名单 —— 先去「名单」页导入一份。";

    /// <summary>
    /// 把名单读进选择列表。
    ///
    /// 每次进来都重建，并按保存的勾选恢复 —— 老师切一下显示样式（姓名↔学号），
    /// 不该把已经选好的人清空。
    /// </summary>
    public void LoadStudents()
    {
        var wanted = Students.Where(row => row.IsSelected).Select(row => row.Student.Id)
            .Concat(_rosterSettings.SelectedStudentIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Students.Clear();

        if (Roster is { } roster)
        {
            foreach (var student in roster.Students)
            {
                Students.Add(new StudentPickRow(
                    student,
                    _labelStyle,
                    wanted.Contains(student.Id),
                    OnPickChanged));
            }
        }

        PersistSelection();
        RefreshDerived();
    }

    private void OnPickChanged(StudentPickRow row)
    {
        PersistSelection();
        RefreshDerived();

        // 勾选状态是界面状态：变换后要让预览那一行跟着重算
        OnPropertyChanged(nameof(PickedStudents));
    }

    private void PersistSelection()
    {
        _rosterSettings.SelectedStudentIds = Students.Where(row => row.IsSelected)
            .Select(row => row.Student.Id).ToList();

        LocalSettings.SaveRosters(_rosterSettings);
    }

    /// <summary>按小组勾选整组。</summary>
    [RelayCommand]
    private void PickGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return;
        }

        foreach (var row in Students.Where(row => row.IsSelectable &&
                     string.Equals(row.Student.Group?.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearPicks()
    {
        foreach (var row in Students)
        {
            row.IsSelected = false;
        }
    }

    public IReadOnlyList<string> Groups => Roster?.Groups ?? [];

    public bool HasGroups => Groups.Count > 0;

    // ======================== 预览与发送 ========================

    private string _statusHint = string.Empty;

    public string StatusHint
    {
        get => _statusHint;
        private set => SetProperty(ref _statusHint, value);
    }

    /// <summary>拼出来的第一句（界面上给老师核对）。</summary>
    public string PreviewText
    {
        get
        {
            var messages = ComposeCurrent();

            if (messages.Count == 0)
            {
                return "（还没有可预览的内容 —— 选学生，并让模板里有组件）";
            }

            return messages.Count == 1 ? messages[0] : $"{messages[0]}　…（共 {messages.Count} 条）";
        }
    }

    private IReadOnlyList<string> ComposeCurrent()
        => CallComposer.Compose(_draft, PickedStudents, Roster, _teacherNameProvider());

    private bool CanSend => PickedCount > 0 && Components.Count > 0 && !IsSending;

    /// <summary>把拼出来的句子依次发出去。</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendCallAsync()
    {
        var messages = ComposeCurrent();

        if (messages.Count == 0)
        {
            StatusHint = "还没有可发送的内容。";
            return;
        }

        IsSending = true;

        try
        {
            var sent = await _sender(messages).ConfigureAwait(true);

            StatusHint = sent == messages.Count
                ? $"已发出 {sent} 条。"
                : $"发出 {sent} 条，{messages.Count - sent} 条没送出去。";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            StatusHint = $"发送失败：{ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCallCommand))]
    private bool _isSending;

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(HasComponents));
        OnPropertyChanged(nameof(HasRoster));
        OnPropertyChanged(nameof(HasNoRoster));
        OnPropertyChanged(nameof(RosterHint));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(PickedSummary));
        OnPropertyChanged(nameof(PreviewText));

        SendCallCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>组件面板里的一项。</summary>
/// <param name="Kind">取值见 <see cref="MessageComponentKinds"/>。</param>
/// <param name="Label">界面显示文本。</param>
public sealed record ComponentPaletteItem(string Kind, string Label);

/// <summary>标识样式的下拉项。</summary>
/// <param name="Value">取值见 <see cref="StudentLabelStyles"/>。</param>
/// <param name="Label">界面显示文本。</param>
public sealed record StudentLabelChoice(string Value, string Label);
