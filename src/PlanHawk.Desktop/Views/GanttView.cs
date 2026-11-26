using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PlanHawk.Models;
using PlanHawk.Services;

namespace PlanHawk.Views;

public enum TimeScale { Days, Weeks, Months }

/// <summary>
/// The Gantt chart: a Microsoft Project-style task sheet on the left and the timeline on the right, with summary bars,
/// milestones, progress, dependency arrows, the critical path and a today line. Browsing, zooming, collapsing and
/// selecting work on the sample projects; editing is coming soon.
/// </summary>
public sealed class GanttView : UserControl
{
    private const double RowH = 34, HeaderH = 56, BarH = 16;

    public Project Project { get; private set; }
    public ProjectTask? Selected { get; private set; }
    public TimeScale Scale { get; private set; } = TimeScale.Weeks;
    public bool ShowCritical { get; private set; }
    public IReadOnlyList<ProjectTask> VisibleTasks => _visible;

    private readonly HashSet<ProjectTask> _collapsed = [];
    private List<ProjectTask> _visible = [];
    private readonly Dictionary<ProjectTask, Border> _sheetRows = [];
    private DateTime _rangeStart;
    private double _dayW;

    private readonly StackPanel _sheet = new();
    private readonly Canvas _chart = new();
    private readonly Canvas _timescale = new() { Height = HeaderH, ClipToBounds = true };
    private readonly ScrollViewer _sheetScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly ScrollViewer _chartScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly ScrollViewer _headScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false };
    private readonly Border _sheetSpacer = new();
    private readonly Rectangle _selectionBand = new() { Height = RowH, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly ContentControl _details = new() { Focusable = false };
    private readonly TextBlock _status = Ui.Text("", muted: true, size: 12);
    private readonly Button _projectButton = new() { Style = Ui.S("BaseButton"), Padding = new Thickness(8, 6, 10, 6), Margin = new Thickness(0, 0, 18, 0) };
    private readonly Button _criticalButton = new() { Style = Ui.S("GhostButton"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Show the critical path: the tasks that set the finish date" };
    private readonly Dictionary<TimeScale, RadioButton> _scaleButtons = [];

    public GanttView(Project project, ProjectTask? select = null)
    {
        Project = project;
        Focusable = true;
        FocusVisualStyle = null;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(BuildToolbar().At(0, 0));
        root.Children.Add(BuildBody().At(0, 1));
        root.Children.Add(BuildDetailsHost().At(0, 2));
        root.Children.Add(BuildStatusBar().At(0, 3));
        Content = root;

        _chartScroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0) _sheetScroll.ScrollToVerticalOffset(_chartScroll.VerticalOffset);
            if (e.HorizontalChange != 0 || e.ExtentWidthChange != 0) _headScroll.ScrollToHorizontalOffset(_chartScroll.HorizontalOffset);
            // Keep the sheet the same height as the chart's viewport (the chart has a horizontal scroll bar).
            _sheetSpacer.Height = Math.Max(0, _chartScroll.ActualHeight - _chartScroll.ViewportHeight);
        };
        _sheetScroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) _chartScroll.ScrollToVerticalOffset(_sheetScroll.VerticalOffset); };
        App.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => App.ThemeChanged -= OnThemeChanged;
        Loaded += (_, _) =>
        {
            if (Selected != null) BringIntoView(Selected); else ScrollToToday();
            Focus();
        };

        ShowProject(project);
        if (select != null) Select(select);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => DrawChart();

    // ===== Public actions (also used by the self-test) =====

    public void ShowProject(Project project)
    {
        Project = project;
        Selected = null;
        _collapsed.Clear();
        MainWindow.Instance?.SetCurrentProject(project);
        Rebuild();
        UpdateProjectButton();
        UpdateStatus();
        ShowDetails();
        if (IsLoaded) ScrollToToday();
    }

    public void Select(ProjectTask? task)
    {
        if (task != null && !_visible.Contains(task))
        {
            // Expand its summary tasks so it can be seen.
            for (var p = task.Parent; p != null; p = p.Parent) _collapsed.Remove(p);
            Rebuild();
        }
        Selected = task;
        foreach (var (t, row) in _sheetRows)
            row.SetResourceReference(Border.BackgroundProperty, t == task ? "Brush.RowSelected" : "Brush.Surface");
        int i = task == null ? -1 : _visible.IndexOf(task);
        _selectionBand.Visibility = i < 0 ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetTop(_selectionBand, Math.Max(0, i) * RowH);
        ShowDetails();
        if (task != null && IsLoaded) BringIntoView(task);
    }

    public void ToggleCollapse(ProjectTask summary)
    {
        if (!summary.IsSummary) return;
        if (!_collapsed.Remove(summary)) _collapsed.Add(summary);
        if (Selected != null && !IsVisible(Selected)) Selected = summary;
        Rebuild();
        Select(Selected);
    }

    public void SetScale(TimeScale scale)
    {
        Scale = scale;
        if (_scaleButtons.TryGetValue(scale, out var rb)) rb.IsChecked = true;
        double center = _chartScroll.ViewportWidth > 0 ? DateAt(_chartScroll.HorizontalOffset + _chartScroll.ViewportWidth / 2).Ticks : 0;
        DrawChart();
        if (center > 0) ScrollToDate(new DateTime((long)center), centered: true);
    }

    public void ZoomIn() { if (Scale > TimeScale.Days) SetScale(Scale - 1); }
    public void ZoomOut() { if (Scale < TimeScale.Months) SetScale(Scale + 1); }

    public void ToggleCritical()
    {
        ShowCritical = !ShowCritical;
        UpdateCriticalButton();
        DrawChart();
        UpdateStatus();
    }

    public void ScrollToToday() => ScrollToDate(DateTime.Today, centered: false);

    // ===== Toolbar =====

    private FrameworkElement BuildToolbar()
    {
        var bar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 12, 20, 12) };
        bar.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        bar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var dock = new DockPanel();

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Ui.SearchBox("Search tasks…", 200));
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        _projectButton.Click += (_, _) => ShowProjectMenu();
        System.Windows.Automation.AutomationProperties.SetName(_projectButton, "Switch project");
        dock.Children.Add(_projectButton.Also(b => DockPanel.SetDock(b, Dock.Left)));

        var groups = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        var task = new StackPanel { Orientation = Orientation.Horizontal };
        task.Children.Add(Ui.SoonButton("Adding tasks", Ic.Add, "Task", "PrimaryButton").Also(b => b.Margin = new Thickness(0, 0, 6, 0)));
        task.Children.Add(Ui.ToolButton("Adding milestones", Ic.Milestone, "Insert milestone"));
        task.Children.Add(Ui.ToolButton("Summary tasks", Ic.Tree, "Insert summary task"));
        task.Children.Add(Ui.ToolButton("Outdenting tasks", Ic.Outdent, "Outdent"));
        task.Children.Add(Ui.ToolButton("Indenting tasks", Ic.Indent, "Indent"));
        groups.Children.Add(Group(task));

        var links = new StackPanel { Orientation = Orientation.Horizontal };
        links.Children.Add(Ui.ToolButton("Linking tasks", Ic.Link, "Link the selected tasks"));
        links.Children.Add(Ui.ToolButton("Unlinking tasks", Ic.Unlink, "Unlink the selected tasks"));
        links.Children.Add(Ui.ToolButton("Marking progress", Ic.CheckCircle, "Mark on track"));
        links.Children.Add(Ui.ToolButton("Baselines", Ic.Target, "Set baseline"));
        groups.Children.Add(Group(links));

        var view = new StackPanel { Orientation = Orientation.Horizontal };
        _criticalButton.Click += (_, _) => ToggleCritical();
        System.Windows.Automation.AutomationProperties.SetName(_criticalButton, "Critical path");
        UpdateCriticalButton();
        view.Children.Add(_criticalButton);
        foreach (var scale in Enum.GetValues<TimeScale>())
        {
            var rb = new RadioButton { Style = Ui.S("ChipRadio"), Content = scale.ToString(), GroupName = "GanttScale" + GetHashCode(), Margin = new Thickness(0, 0, 4, 0), IsChecked = scale == Scale };
            rb.Checked += (_, _) => { if (Scale != scale) SetScale(scale); };
            _scaleButtons[scale] = rb;
            view.Children.Add(rb);
        }
        var zoomOut = new Button { Style = Ui.S("BaseButton"), Padding = new Thickness(7), MinWidth = 32, MinHeight = 32, ToolTip = "Zoom out", Content = Ui.Icon(Ic.ZoomOut, 15), Margin = new Thickness(6, 0, 0, 0) };
        zoomOut.Click += (_, _) => ZoomOut();
        var zoomIn = new Button { Style = Ui.S("BaseButton"), Padding = new Thickness(7), MinWidth = 32, MinHeight = 32, ToolTip = "Zoom in", Content = Ui.Icon(Ic.ZoomIn, 15) };
        zoomIn.Click += (_, _) => ZoomIn();
        var today = new Button { Style = Ui.S("BaseButton"), Padding = new Thickness(7), MinWidth = 32, MinHeight = 32, ToolTip = "Scroll to today", Content = Ui.Icon(Ic.Today, 15) };
        today.Click += (_, _) => ScrollToToday();
        foreach (var b in new[] { zoomOut, zoomIn, today }) b.SetResourceReference(ForegroundProperty, "Brush.Text");
        System.Windows.Automation.AutomationProperties.SetName(zoomOut, "Zoom out");
        System.Windows.Automation.AutomationProperties.SetName(zoomIn, "Zoom in");
        System.Windows.Automation.AutomationProperties.SetName(today, "Scroll to today");
        view.Children.Add(zoomOut);
        view.Children.Add(zoomIn);
        view.Children.Add(today);
        groups.Children.Add(view);

        dock.Children.Add(groups);
        bar.Child = dock;
        return bar;
    }

    private static FrameworkElement Group(UIElement content)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(content);
        sp.Children.Add(Ui.Separator().Also(s => { s.Margin = new Thickness(10, 4, 10, 4); s.Height = 22; }));
        return sp;
    }

    private void UpdateProjectButton()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Dot(Project.Color, 12).Also(d => d.Margin = new Thickness(0, 0, 9, 0)));
        row.Children.Add(Ui.Text(Project.Name, size: 16, weight: FontWeights.Bold).Also(t => t.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont")));
        row.Children.Add(Ui.Icon(Ic.ChevronDown, 12, "Brush.TextMuted").Also(i => i.Margin = new Thickness(8, 2, 0, 0)));
        _projectButton.Content = row;
        _projectButton.ToolTip = $"{Project.Category} · managed by {Project.Manager}";
    }

    private void ShowProjectMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _projectButton, Placement = PlacementMode.Bottom };
        foreach (var p in SampleData.Projects)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(Ui.Dot(p.Color, 10).Also(d => d.Margin = new Thickness(0, 0, 10, 0)));
            header.Children.Add(new TextBlock { Text = p.Name, FontWeight = p == Project ? FontWeights.SemiBold : FontWeights.Normal });
            var item = new MenuItem { Header = header };
            var target = p;
            item.Click += (_, _) => ShowProject(target);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var open = new MenuItem { Header = "Open a Microsoft Project file…" };
        open.Click += (_, _) => Toaster.Soon("Opening .mpp and .xml files");
        menu.Items.Add(open);
        menu.IsOpen = true;
    }

    private void UpdateCriticalButton()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Icon(Ic.Branch, 15, ShowCritical ? "Brush.Critical" : null).Also(i => i.Margin = new Thickness(0, 0, 7, 0)));
        row.Children.Add(new TextBlock { Text = "Critical path", VerticalAlignment = VerticalAlignment.Center });
        _criticalButton.Content = row;
        if (ShowCritical) _criticalButton.SetResourceReference(BackgroundProperty, "Brush.AccentSoft");
        else _criticalButton.ClearValue(BackgroundProperty);
    }

    // ===== Sheet + chart =====

    private FrameworkElement BuildBody()
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(610), MinWidth = 300 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        body.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 200 });

        // Sheet
        var sheet = new Grid();
        sheet.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderH) });
        sheet.RowDefinitions.Add(new RowDefinition());
        sheet.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sheet.SetResourceReference(Panel.BackgroundProperty, "Brush.Surface");
        sheet.Children.Add(SheetHeader().At(0, 0));
        _sheetScroll.Content = _sheet;
        sheet.Children.Add(_sheetScroll.At(0, 1));
        _sheetSpacer.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        sheet.Children.Add(_sheetSpacer.At(0, 2));
        body.Children.Add(sheet.At(0));

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = Cursors.SizeWE, ToolTip = "Drag to resize" };
        splitter.SetResourceReference(BackgroundProperty, "Brush.Border");
        body.Children.Add(splitter.At(1));

        // Chart
        var chart = new Grid();
        chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderH) });
        chart.RowDefinitions.Add(new RowDefinition());
        chart.SetResourceReference(Panel.BackgroundProperty, "Brush.Surface");
        _headScroll.Content = _timescale;
        chart.Children.Add(_headScroll.At(0, 0));
        _chartScroll.Content = _chart;
        chart.Children.Add(_chartScroll.At(0, 1));
        body.Children.Add(chart.At(2));
        return body;
    }

    private static readonly (string Title, double Width, bool Right)[] Columns =
    [
        ("", 38, true), ("", 28, false), ("Task name", -1, false), ("Duration", 72, false), ("Start", 84, false), ("Finish", 84, false), ("Predecessors", 92, false),
    ];

    private static Grid ColumnGrid()
    {
        var g = new Grid();
        foreach (var c in Columns)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(c.Width), MinWidth = c.Width < 0 ? 120 : 0 });
        return g;
    }

    private static FrameworkElement SheetHeader()
    {
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var g = ColumnGrid();
        for (int i = 0; i < Columns.Length; i++)
        {
            var c = Columns[i];
            if (i == 1)
            {
                g.Children.Add(Ui.Icon(Ic.Info, 13, "Brush.TextMuted").Also(x => { x.HorizontalAlignment = HorizontalAlignment.Center; x.VerticalAlignment = VerticalAlignment.Bottom; x.Margin = new Thickness(0, 0, 0, 10); x.ToolTip = "Indicators"; }).At(i));
                continue;
            }
            var t = Ui.Text(c.Title.ToUpperInvariant(), muted: true, size: 11, weight: FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Bottom;
            t.Margin = new Thickness(i == 2 ? 26 : 8, 0, 8, 10);
            g.Children.Add(t.At(i));
        }
        border.Child = g;
        return border;
    }

    private bool IsVisible(ProjectTask t)
    {
        for (var p = t.Parent; p != null; p = p.Parent) if (_collapsed.Contains(p)) return false;
        return true;
    }

    private void Rebuild()
    {
        _visible = Project.Tasks.Where(IsVisible).ToList();
        BuildSheet();
        DrawChart();
    }

    private void BuildSheet()
    {
        _sheet.Children.Clear();
        _sheetRows.Clear();
        foreach (var t in _visible)
        {
            var row = new Border { Height = RowH, BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand };
            row.SetResourceReference(Border.BorderBrushProperty, "Brush.GridLine");
            row.SetResourceReference(Border.BackgroundProperty, t == Selected ? "Brush.RowSelected" : "Brush.Surface");
            var g = ColumnGrid();

            g.Children.Add(Ui.Text(t.Id.ToString(), muted: true, size: 12).Also(x => { x.HorizontalAlignment = HorizontalAlignment.Right; x.Margin = new Thickness(0, 0, 8, 0); }).At(0));
            g.Children.Add(Indicator(t).At(1));

            var name = new DockPanel { Margin = new Thickness(6 + t.Level * 18, 0, 8, 0) };
            if (t.IsSummary)
            {
                var toggle = new Button
                {
                    Style = Ui.S("BaseButton"), Padding = new Thickness(3), MinHeight = 20, MinWidth = 20, Margin = new Thickness(0, 0, 2, 0),
                    Content = Ui.Icon(_collapsed.Contains(t) ? Ic.ChevronRight : Ic.ChevronDown, 11, "Brush.TextMuted"),
                    ToolTip = _collapsed.Contains(t) ? "Expand" : "Collapse",
                };
                System.Windows.Automation.AutomationProperties.SetName(toggle, (_collapsed.Contains(t) ? "Expand " : "Collapse ") + t.Name);
                toggle.Click += (_, _) => ToggleCollapse(t);
                DockPanel.SetDock(toggle, Dock.Left);
                name.Children.Add(toggle);
            }
            else name.Children.Add(new Border { Width = 22 }.Also(b => DockPanel.SetDock(b, Dock.Left)));
            name.Children.Add(Ui.Text(t.Name, size: 13, weight: t.IsSummary ? FontWeights.SemiBold : FontWeights.Normal));
            g.Children.Add(name.At(2));

            g.Children.Add(Cell(t.DurationText, t.IsSummary).At(3));
            g.Children.Add(Cell(WorkCalendar.Short(t.Start), t.IsSummary).At(4));
            g.Children.Add(Cell(WorkCalendar.Short(t.Finish), t.IsSummary).At(5));
            g.Children.Add(Cell(t.PredecessorText, false).Also(c => { if (t.Predecessors.Length > 0) c.ToolTip = string.Join("\n", t.Predecessors.Select(l => $"{l.Code}: {l.TypeName} from \"{Project.Find(l.PredecessorId).Name}\"")); }).At(6));

            row.Child = g;
            row.MouseEnter += (_, _) => { if (t != Selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt"); };
            row.MouseLeave += (_, _) => { if (t != Selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.Surface"); };
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) Toaster.Soon("Editing tasks");
                else Select(t);
                Focus();
            };
            System.Windows.Automation.AutomationProperties.SetName(row, t.Name);
            _sheetRows[t] = row;
            _sheet.Children.Add(row);
        }
        _sheet.Children.Add(new Border { Height = RowH * 2 }); // room to scroll past the last task
    }

    private static TextBlock Cell(string text, bool bold)
    {
        var t = Ui.Text(text, size: 12.5, weight: bold ? FontWeights.SemiBold : FontWeights.Normal);
        t.Margin = new Thickness(8, 0, 6, 0);
        return t;
    }

    private static FrameworkElement Indicator(ProjectTask t)
    {
        TextBlock? icon = null;
        string? tip = null;
        if (t.IsMilestone) { icon = Ui.Icon(Ic.Milestone, 13, "Brush.Milestone", filled: true); tip = "Milestone"; }
        else if (!t.IsSummary && t.State == TaskState.Late) { icon = Ui.Icon(Ic.Warning, 13, "Brush.Warn"); tip = "Behind schedule"; }
        else if (!t.IsSummary && t.State == TaskState.Complete) { icon = Ui.Icon(Ic.CheckCircle, 13, "Brush.Positive"); tip = "Complete"; }
        if (t.Notes.Length > 0) tip = (tip == null ? "" : tip + "\n") + "Note: " + t.Notes;
        icon ??= t.Notes.Length > 0 ? Ui.Icon(Ic.Document, 13, "Brush.TextMuted") : null;
        if (icon == null) return new Border();
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.ToolTip = tip;
        return icon;
    }

    // ===== Chart drawing =====

    private double X(DateTime d) => (d.Date - _rangeStart).TotalDays * _dayW;
    private DateTime DateAt(double x) => _rangeStart.AddDays(x / _dayW);
    /// <summary>Left edge of a working-day index.</summary>
    private double StartX(int index) => X(Project.DateOf(index));
    /// <summary>Right edge of the working day before <paramref name="endIndex"/>.</summary>
    private double EndX(int endIndex) => endIndex <= 0 ? StartX(0) : X(Project.DateOf(endIndex - 1)) + _dayW;
    private double MilestoneX(ProjectTask t) => t.StartIndex > 0 ? EndX(t.StartIndex) : StartX(0);
    private double RowMid(int row) => row * RowH + RowH / 2;

    private void DrawChart()
    {
        _dayW = Scale switch { TimeScale.Days => 30, TimeScale.Weeks => 14, _ => 5 };
        _rangeStart = WorkCalendar.MondayOf(Project.StartDate).AddDays(Scale == TimeScale.Months ? -21 : -7);
        var rangeEnd = WorkCalendar.MondayOf(Project.FinishDate).AddDays(Scale == TimeScale.Months ? 49 : 21);
        if (DateTime.Today > rangeEnd) rangeEnd = DateTime.Today.AddDays(14);
        if (DateTime.Today < _rangeStart) _rangeStart = WorkCalendar.MondayOf(DateTime.Today).AddDays(-7);
        double width = X(rangeEnd);
        double height = Math.Max(_visible.Count + 2, 1) * RowH;

        _chart.Children.Clear();
        _chart.Width = width;
        _chart.Height = height;
        _chart.Background = Brushes.Transparent;
        _chart.MouseLeftButtonDown -= Chart_MouseDown;
        _chart.MouseLeftButtonDown += Chart_MouseDown;

        // Weekends and grid lines
        for (var d = _rangeStart; d < rangeEnd; d = d.AddDays(1))
        {
            if (!WorkCalendar.IsWorkday(d) && _dayW >= 10)
                _chart.Children.Add(Box(X(d), 0, _dayW, height, "Brush.Weekend"));
            bool line = Scale switch
            {
                TimeScale.Days => true,
                TimeScale.Weeks => d.DayOfWeek == DayOfWeek.Monday,
                _ => d.Day == 1,
            };
            if (line) _chart.Children.Add(Box(X(d), 0, 1, height, "Brush.GridLine"));
        }
        for (int r = 1; r <= _visible.Count; r++) _chart.Children.Add(Box(0, r * RowH - 1, width, 1, "Brush.GridLine"));

        _selectionBand.Width = width;
        _selectionBand.SetResourceReference(Shape.FillProperty, "Brush.RowSelected");
        _selectionBand.Opacity = 0.7;
        _chart.Children.Add(_selectionBand);

        // Today
        var today = Box(X(DateTime.Today) + _dayW / 2, 0, 2, height, "Brush.Today");
        today.Opacity = 0.8;
        _chart.Children.Add(today);

        // Dependency arrows under the bars
        foreach (var (from, to, link) in Project.Links)
        {
            int a = _visible.IndexOf(from), b = _visible.IndexOf(to);
            if (a < 0 || b < 0) continue;
            DrawLink(from, a, to, b, link);
        }

        // Bars
        for (int i = 0; i < _visible.Count; i++) DrawTask(_visible[i], i);

        DrawTimescale(rangeEnd);
        int sel = Selected == null ? -1 : _visible.IndexOf(Selected);
        _selectionBand.Visibility = sel < 0 ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetTop(_selectionBand, Math.Max(0, sel) * RowH);
    }

    private void Chart_MouseDown(object sender, MouseButtonEventArgs e)
    {
        int row = (int)(e.GetPosition(_chart).Y / RowH);
        if (row >= 0 && row < _visible.Count)
        {
            if (e.ClickCount == 2) Toaster.Soon("Editing tasks");
            else Select(_visible[row]);
        }
        Focus();
    }

    private bool Critical(ProjectTask t) => ShowCritical && t.IsCritical;

    private void DrawTask(ProjectTask t, int row)
    {
        double mid = RowMid(row);
        string tip = $"{t.Name}\n{WorkCalendar.Short(t.Start)} to {WorkCalendar.Short(t.Finish)} · {t.DurationText}" +
                     (t.IsMilestone ? "" : $" · {t.PercentComplete:0}% complete") +
                     (t.IsSummary ? "" : t.TotalSlack <= 0 ? "\nOn the critical path" : $"\n{t.TotalSlack} day(s) of slack");
        double labelX;
        if (t.IsSummary)
        {
            double x0 = StartX(t.StartIndex), x1 = EndX(t.EndIndex);
            var bar = new Border { Width = Math.Max(4, x1 - x0), Height = 8, CornerRadius = new CornerRadius(2), ToolTip = tip };
            bar.SetResourceReference(Border.BackgroundProperty, "Brush.Summary");
            Place(bar, x0, mid - 7);
            // progress tint on the summary bar
            if (t.PercentComplete > 0)
            {
                var prog = new Border { Width = Math.Max(0, (x1 - x0) * t.PercentComplete / 100), Height = 3, CornerRadius = new CornerRadius(1.5), IsHitTestVisible = false };
                prog.SetResourceReference(Border.BackgroundProperty, "Brush.Highlight");
                Place(prog, x0, mid - 4.5);
            }
            foreach (var x in new[] { x0, x1 })
            {
                bool left = x == x0;
                var cap = new Polygon { IsHitTestVisible = false };
                cap.Points = [new Point(left ? x0 : x1 - 7, mid + 1), new Point(left ? x0 + 7 : x1, mid + 1), new Point(left ? x0 : x1, mid + 8)];
                cap.SetResourceReference(Shape.FillProperty, "Brush.Summary");
                _chart.Children.Add(cap);
            }
            labelX = x1 + 8;
        }
        else if (t.IsMilestone)
        {
            double x = MilestoneX(t);
            var diamond = new Rectangle { Width = 13, Height = 13, RadiusX = 2, RadiusY = 2, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45), ToolTip = tip };
            diamond.SetResourceReference(Shape.FillProperty, Critical(t) ? "Brush.Critical" : "Brush.Milestone");
            Place(diamond, x - 6.5, mid - 6.5);
            labelX = x + 13;
            var date = Ui.Text(t.Finish.ToString("M/d"), size: 11.5, weight: FontWeights.SemiBold);
            date.IsHitTestVisible = false;
            Place(date, labelX, mid - 9);
            return;
        }
        else
        {
            double x0 = StartX(t.StartIndex), x1 = EndX(t.EndIndex);
            bool crit = Critical(t);
            var grid = new Grid { Width = Math.Max(4, x1 - x0), Height = BarH, ToolTip = tip, Cursor = Cursors.Hand };
            var back = new Border { CornerRadius = new CornerRadius(5) };
            back.SetResourceReference(Border.BackgroundProperty, crit ? "Brush.Critical" : "Brush.Bar");
            grid.Children.Add(back);
            if (t.PercentComplete > 0)
            {
                double w = grid.Width * t.PercentComplete / 100;
                var prog = new Border
                {
                    Width = w, HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = t.PercentComplete >= 100 ? new CornerRadius(5) : new CornerRadius(5, 0, 0, 5),
                    Margin = new Thickness(0, 5, 0, 5),
                };
                prog.SetResourceReference(Border.BackgroundProperty, crit ? "Brush.CriticalProgress" : "Brush.BarProgress");
                grid.Children.Add(prog);
            }
            if (t.State == TaskState.Late)
            {
                var ring = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5), IsHitTestVisible = false };
                ring.SetResourceReference(Border.BorderBrushProperty, "Brush.Warn");
                grid.Children.Add(ring);
            }
            Place(grid, x0, mid - BarH / 2);
            labelX = x1 + 8;
        }
        if (t.Resources.Length > 0)
        {
            var label = Ui.Text(t.ResourceText, muted: true, size: 11.5);
            label.IsHitTestVisible = false;
            Place(label, labelX, mid - 9);
        }
    }

    private void DrawLink(ProjectTask from, int a, ProjectTask to, int b, Link link)
    {
        bool crit = ShowCritical && from.IsCritical && to.IsCritical;
        string brush = crit ? "Brush.Critical" : "Brush.Arrow";
        double y1 = RowMid(a), y2 = RowMid(b);
        bool down = b > a;
        var pts = new PointCollection();
        Point tip;
        Vector dir;

        double toStart = to.IsMilestone ? MilestoneX(to) : StartX(to.StartIndex);
        double toHalf = to.IsMilestone ? 8 : to.IsSummary ? 6 : BarH / 2;
        if (link.Type == LinkType.SS)
        {
            double x1 = from.IsMilestone ? MilestoneX(from) - 7 : StartX(from.StartIndex);
            double x2 = to.IsMilestone ? toStart - 8 : toStart;
            double xl = Math.Min(x1, x2) - 9;
            pts.Add(new Point(x1, y1));
            pts.Add(new Point(xl, y1));
            pts.Add(new Point(xl, y2));
            tip = new Point(x2, y2);
            pts.Add(new Point(x2 - 5, y2));
            dir = new Vector(1, 0);
        }
        else
        {
            double x1 = from.IsMilestone ? MilestoneX(from) + 7 : EndX(from.EndIndex);
            double xm = Math.Max(toStart + Math.Min(6, Math.Max(2, (EndX(to.EndIndex) - toStart) / 2)), x1 + 7);
            if (to.IsMilestone) xm = Math.Max(toStart, x1 + 4);
            double yEdge = down ? y2 - toHalf : y2 + toHalf;
            if (to.IsMilestone) yEdge = down ? y2 - 9 + Math.Min(7, Math.Abs(xm - toStart)) : y2 + 9 - Math.Min(7, Math.Abs(xm - toStart));
            pts.Add(new Point(x1, y1));
            pts.Add(new Point(xm, y1));
            tip = new Point(xm, yEdge);
            pts.Add(new Point(xm, yEdge + (down ? -5 : 5)));
            dir = new Vector(0, down ? 1 : -1);
        }
        var line = new Polyline { Points = pts, StrokeThickness = crit ? 1.6 : 1.2, IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };
        line.SetResourceReference(Shape.StrokeProperty, brush);
        _chart.Children.Add(line);
        var side = new Vector(-dir.Y, dir.X);
        var head = new Polygon { Points = [tip, tip - dir * 6 + side * 4, tip - dir * 6 - side * 4], IsHitTestVisible = false };
        head.SetResourceReference(Shape.FillProperty, brush);
        _chart.Children.Add(head);
    }

    private void DrawTimescale(DateTime rangeEnd)
    {
        _timescale.Children.Clear();
        _timescale.Width = X(rangeEnd) + 40;
        _timescale.Children.Add(Box(0, 0, _timescale.Width, HeaderH, "Brush.SurfaceAlt"));
        _timescale.Children.Add(Box(0, HeaderH - 1, _timescale.Width, 1, "Brush.Border"));
        _timescale.Children.Add(Box(0, HeaderH / 2, _timescale.Width, 1, "Brush.Border"));

        // Top tier: months (or years when zoomed out)
        var top = Scale == TimeScale.Months ? new DateTime(_rangeStart.Year, 1, 1) : new DateTime(_rangeStart.Year, _rangeStart.Month, 1);
        while (top < rangeEnd)
        {
            var next = Scale == TimeScale.Months ? top.AddYears(1) : top.AddMonths(1);
            double x = Math.Max(0, X(top));
            _timescale.Children.Add(Box(X(top), 0, 1, HeaderH / 2, "Brush.Border"));
            var label = Ui.Text(Scale == TimeScale.Months ? top.ToString("yyyy") : top.ToString("MMMM yyyy"), size: 12, weight: FontWeights.SemiBold);
            Place(label, x + 8, 6, _timescale);
            top = next;
        }

        // Bottom tier: days, weeks or months
        var d = Scale == TimeScale.Months ? new DateTime(_rangeStart.Year, _rangeStart.Month, 1) : _rangeStart;
        while (d < rangeEnd)
        {
            DateTime next;
            string text;
            switch (Scale)
            {
                case TimeScale.Days:
                    next = d.AddDays(1);
                    text = d.ToString("ddd")[..1] + " " + d.Day;
                    break;
                case TimeScale.Weeks:
                    next = d.AddDays(7);
                    text = d.ToString("MMM d");
                    break;
                default:
                    next = d.AddMonths(1);
                    text = d.ToString("MMM");
                    break;
            }
            _timescale.Children.Add(Box(X(d), HeaderH / 2, 1, HeaderH / 2, "Brush.Border"));
            var label = Ui.Text(text, muted: !WorkCalendar.IsWorkday(d) && Scale == TimeScale.Days, size: 11.5);
            Place(label, X(d) + (Scale == TimeScale.Days ? 4 : 6), HeaderH / 2 + 6, _timescale);
            d = next;
        }

        // Today marker
        var pill = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 1, 6, 1), Child = new TextBlock { Text = "Today", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White } };
        pill.SetResourceReference(Border.BackgroundProperty, "Brush.Today");
        Place(pill, X(DateTime.Today) + _dayW / 2 - 20, 4, _timescale);
    }

    private Rectangle Box(double x, double y, double w, double h, string brushKey)
    {
        var r = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), IsHitTestVisible = false };
        r.SetResourceReference(Shape.FillProperty, brushKey);
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    private void Place(UIElement e, double x, double y, Canvas? canvas = null)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        (canvas ?? _chart).Children.Add(e);
    }

    private void ScrollToDate(DateTime date, bool centered)
    {
        if (_dayW <= 0) return;
        double x = X(date);
        _chartScroll.ScrollToHorizontalOffset(Math.Max(0, centered ? x - _chartScroll.ViewportWidth / 2 : x - Math.Min(240, _chartScroll.ViewportWidth / 4)));
    }

    private void BringIntoView(ProjectTask t)
    {
        int i = _visible.IndexOf(t);
        if (i < 0) return;
        double top = i * RowH, view = _chartScroll.ViewportHeight;
        if (view > 0 && (top < _chartScroll.VerticalOffset || top + RowH > _chartScroll.VerticalOffset + view))
            _chartScroll.ScrollToVerticalOffset(Math.Max(0, top - view / 3));
        double x = t.IsMilestone ? MilestoneX(t) : StartX(t.StartIndex);
        double vw = _chartScroll.ViewportWidth;
        if (vw > 0 && (x < _chartScroll.HorizontalOffset || x > _chartScroll.HorizontalOffset + vw - 120))
            _chartScroll.ScrollToHorizontalOffset(Math.Max(0, x - 120));
    }

    // ===== Keyboard =====

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int i = Selected == null ? -1 : _visible.IndexOf(Selected);
        switch (e.Key)
        {
            case Key.Down: Select(_visible[Math.Min(_visible.Count - 1, i + 1)]); e.Handled = true; break;
            case Key.Up: Select(_visible[Math.Max(0, i <= 0 ? 0 : i - 1)]); e.Handled = true; break;
            case Key.Left when Selected is { IsSummary: true } && !_collapsed.Contains(Selected): ToggleCollapse(Selected); e.Handled = true; break;
            case Key.Right when Selected is { IsSummary: true } && _collapsed.Contains(Selected): ToggleCollapse(Selected); e.Handled = true; break;
            case Key.Enter: Toaster.Soon("Editing tasks"); e.Handled = true; break;
            case Key.Insert: Toaster.Soon("Adding tasks"); e.Handled = true; break;
            case Key.Delete: Toaster.Soon("Deleting tasks"); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    // ===== Details + status =====

    private FrameworkElement BuildDetailsHost()
    {
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 14, 22, 14), Child = _details, MinHeight = 128 };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return border;
    }

    private void ShowDetails()
    {
        var t = Selected;
        if (t == null)
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            hint.Children.Add(Ui.Icon(Ic.Info, 16, "Brush.Accent").Also(i => i.Margin = new Thickness(0, 0, 10, 0)));
            hint.Children.Add(Ui.Text($"Select a task to see its dates, progress, predecessors and successors.  {Project.Description}", muted: true));
            _details.Content = hint;
            return;
        }

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

        // Name, state, notes
        var about = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        var titleRow = new DockPanel();
        var edit = Ui.SoonButton("Editing tasks", Ic.Wrench, "Task information", "GhostButton");
        edit.Padding = new Thickness(10, 5, 10, 5);
        DockPanel.SetDock(edit, Dock.Right);
        titleRow.Children.Add(edit);
        var icon = t.IsMilestone ? Ic.Milestone : t.IsSummary ? Ic.Tree : Ic.Task;
        titleRow.Children.Add(Ui.Icon(icon, 16, t.IsMilestone ? "Brush.Milestone" : "Brush.Accent").Also(i => { i.Margin = new Thickness(0, 0, 9, 0); DockPanel.SetDock(i, Dock.Left); }));
        titleRow.Children.Add(new TextBlock { Text = t.Name, Style = Ui.S("CardTitle"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        about.Children.Add(titleRow);
        var meta = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        meta.Children.Add(StatePill(t).Also(p => p.Margin = new Thickness(0, 0, 8, 4)));
        meta.Children.Add(Ui.Chip($"Task {t.Id}", Ic.Task).Also(c => c.Margin = new Thickness(0, 0, 8, 4)));
        if (!t.IsSummary) meta.Children.Add(Ui.Chip(t.IsCritical ? "Critical" : $"{t.TotalSlack} day{(t.TotalSlack == 1 ? "" : "s")} of slack", Ic.Branch).Also(c => c.Margin = new Thickness(0, 0, 8, 4)));
        foreach (var r in t.Resources) meta.Children.Add(Ui.Chip(r, Ic.Person).Also(c => c.Margin = new Thickness(0, 0, 8, 4)));
        about.Children.Add(meta);
        if (t.Notes.Length > 0) about.Children.Add(Ui.Wrap(t.Notes, muted: true, size: 12.5).Also(w => w.Margin = new Thickness(0, 6, 0, 0)));
        else if (t.Parent != null) about.Children.Add(Ui.Text("In " + t.Parent.Name, muted: true, size: 12.5).Also(w => w.Margin = new Thickness(0, 6, 0, 0)));
        g.Children.Add(about.At(0));

        // Dates and progress
        var dates = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        dates.Children.Add(new TextBlock { Text = "SCHEDULE", Style = Ui.S("SectionLabel") });
        dates.Children.Add(Pair("Start", WorkCalendar.Long(t.Start)));
        dates.Children.Add(Pair("Finish", WorkCalendar.Long(t.Finish)));
        dates.Children.Add(Pair("Duration", t.DurationText));
        if (!t.IsMilestone)
        {
            var progress = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            progress.Children.Add(Ui.Text($"{t.PercentComplete:0}%", size: 12.5, weight: FontWeights.SemiBold).Also(x => { x.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(x, Dock.Right); }));
            progress.Children.Add(Progress(t.PercentComplete, Critical(t)));
            dates.Children.Add(progress);
        }
        g.Children.Add(dates.At(1));

        g.Children.Add(LinkList("PREDECESSORS", t.Predecessors.Select(l => (Project.Find(l.PredecessorId), l)), "Nothing has to happen first.").At(2));
        g.Children.Add(LinkList("SUCCESSORS", t.Successors.Select(s => (s.Task, s.Link)), "No tasks wait on this one.").At(3));
        _details.Content = g;
    }

    private static FrameworkElement Pair(string label, string value)
    {
        var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        d.Children.Add(Ui.Text(label, muted: true, size: 12.5).Also(x => { x.Width = 70; DockPanel.SetDock(x, Dock.Left); }));
        d.Children.Add(Ui.Text(value, size: 12.5));
        return d;
    }

    private FrameworkElement LinkList(string title, IEnumerable<(ProjectTask Task, Link Link)> links, string empty)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        sp.Children.Add(new TextBlock { Text = title, Style = Ui.S("SectionLabel") });
        var list = links.ToList();
        if (list.Count == 0) sp.Children.Add(Ui.Text(empty, muted: true, size: 12.5));
        foreach (var (task, link) in list.Take(4))
        {
            var b = new Button { Style = Ui.S("BaseButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(-8, 0, 0, 0), ToolTip = $"{link.TypeName}{(link.Lag != 0 ? $", {link.Lag} day lag" : "")}. Click to select it." };
            var row = new DockPanel();
            var type = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), Child = Ui.Text(link.Type + (link.Lag != 0 ? $"+{link.Lag}d" : ""), size: 11, weight: FontWeights.SemiBold, brushKey: "Brush.Accent") };
            type.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
            DockPanel.SetDock(type, Dock.Right);
            row.Children.Add(type);
            row.Children.Add(Ui.Text(task.Id.ToString(), muted: true, size: 12).Also(x => { x.Width = 24; DockPanel.SetDock(x, Dock.Left); }));
            row.Children.Add(Ui.Text(task.Name, size: 12.5));
            b.Content = row;
            b.Click += (_, _) => Select(task);
            System.Windows.Automation.AutomationProperties.SetName(b, task.Name);
            sp.Children.Add(b);
        }
        if (list.Count > 4) sp.Children.Add(Ui.Text($"and {list.Count - 4} more", muted: true, size: 12));
        return sp;
    }

    internal static Border StatePill(ProjectTask t)
    {
        string brush = t.State switch
        {
            TaskState.Complete => "Brush.Positive",
            TaskState.Late => "Brush.Warn",
            TaskState.InProgress => "Brush.Accent",
            _ => "Brush.TextMuted",
        };
        var text = Ui.Text(t.IsMilestone && t.State == TaskState.Complete ? "Reached" : t.StateText, size: 11.5, weight: FontWeights.SemiBold, brushKey: brush);
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 3), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = text };
        pill.SetResourceReference(Border.BorderBrushProperty, brush);
        return pill;
    }

    internal static FrameworkElement Progress(double percent, bool critical = false, double height = 6)
    {
        var track = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        var back = new Border { CornerRadius = new CornerRadius(height / 2) };
        back.SetResourceReference(Border.BackgroundProperty, "Brush.Track");
        track.Children.Add(back);
        var fill = new Border { CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, critical ? "Brush.Critical" : percent >= 100 ? "Brush.Positive" : "Brush.Accent");
        track.Children.Add(fill);
        track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * Math.Clamp(percent, 0, 100) / 100;
        return track;
    }

    private FrameworkElement BuildStatusBar()
    {
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 7, 22, 7) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var dock = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Ui.Icon(Ic.Lock, 12, "Brush.TextMuted").Also(i => i.Margin = new Thickness(0, 0, 6, 0)));
        right.Children.Add(Ui.Text("Preview: sample project, changes aren't saved", muted: true, size: 12));
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(_status);
        border.Child = dock;
        return border;
    }

    private void UpdateStatus()
    {
        var leaves = Project.Leaves;
        string text = $"{leaves.Length} tasks  ·  {Project.Milestones.Length} milestones  ·  {WorkCalendar.Short(Project.StartDate)} to {WorkCalendar.Short(Project.FinishDate)} ({Project.WorkDays} working days)  ·  {Project.PercentComplete:0}% complete";
        int late = Project.LateCount;
        if (late > 0) text += $"  ·  {late} behind";
        if (ShowCritical) text += $"  ·  {leaves.Count(t => t.IsCritical)} critical";
        _status.Text = text;
    }
}
