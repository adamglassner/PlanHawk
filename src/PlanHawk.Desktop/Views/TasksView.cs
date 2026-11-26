using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlanHawk.Models;

namespace PlanHawk.Views;

/// <summary>Every task in every sample project as a table, with project and status filters that work.
/// Clicking a task opens it on the Gantt chart. Adding and editing tasks is coming soon.</summary>
public sealed class TasksView : UserControl
{
    public Project? ProjectFilter { get; private set; }
    public TaskState? StateFilter { get; private set; }
    public int RowCount { get; private set; }

    private readonly UniformGrid _kpis = new() { Rows = 1, Margin = new Thickness(0, 0, -14, 18) };
    private readonly StackPanel _rows = new();
    private readonly Dictionary<object, RadioButton> _chips = [];

    private static readonly (string Title, double Width)[] Columns =
    [
        ("Task", -2.2), ("Start", 88), ("Finish", 88), ("Duration", 80), ("Progress", 140), ("Assigned to", -1.2), ("Status", 112),
    ];

    public TasksView()
    {
        var root = new StackPanel();
        root.Children.Add(Header());
        root.Children.Add(_kpis);
        root.Children.Add(Filters());

        var table = new StackPanel();
        table.Children.Add(TableHeader());
        table.Children.Add(_rows);
        root.Children.Add(new Border { Style = Ui.S("Card"), Padding = new Thickness(0, 0, 0, 6), ClipToBounds = true, Child = table });
        Content = root;
        Refresh();
    }

    public void SetProjectFilter(Project? project)
    {
        ProjectFilter = project;
        if (_chips.TryGetValue((object?)project ?? "all-projects", out var rb)) rb.IsChecked = true;
        Refresh();
    }

    public void SetStateFilter(TaskState? state)
    {
        StateFilter = state;
        if (_chips.TryGetValue((object?)state ?? "all-states", out var rb)) rb.IsChecked = true;
        Refresh();
    }

    private static FrameworkElement Header()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Ui.SoonButton("Grouping and sorting", Ic.Sort, "Group by").Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
        buttons.Children.Add(Ui.SoonButton("Exporting to Excel", Ic.Export, "Export").Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
        buttons.Children.Add(Ui.SoonButton("Adding tasks", Ic.Add, "New task", "PrimaryButton"));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);

        var tile = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 16, 0) };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        tile.Child = Ui.Icon(Ic.Task, 24, "Brush.Accent").Also(i => i.HorizontalAlignment = HorizontalAlignment.Center);
        header.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock { Text = "Tasks", Style = Ui.S("PageTitle") });
        titleRow.Children.Add(Ui.Pill("PREVIEW").Also(p => p.Margin = new Thickness(12, 0, 0, 0)));
        titles.Children.Add(titleRow);
        titles.Children.Add(new TextBlock { Text = "Every task in every project. Click one to see it on the Gantt chart.", Style = Ui.S("Muted"), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) });
        header.Children.Add(titles);
        return header;
    }

    private FrameworkElement Filters()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var projects = new WrapPanel();
        projects.Children.Add(Chip("all-projects", "All projects", null, "TasksProject", true, () => SetProjectFilter(null)));
        foreach (var p in SampleData.Projects)
            projects.Children.Add(Chip(p, p.Name, p.Color, "TasksProject", false, () => SetProjectFilter(p)));
        sp.Children.Add(projects);

        var states = new WrapPanel();
        states.Children.Add(Chip("all-states", "Any status", null, "TasksState", true, () => SetStateFilter(null)));
        foreach (var s in new[] { TaskState.NotStarted, TaskState.InProgress, TaskState.Late, TaskState.Complete })
        {
            string label = s switch { TaskState.NotStarted => "Not started", TaskState.InProgress => "In progress", TaskState.Late => "Behind", _ => "Complete" };
            states.Children.Add(Chip(s, label, null, "TasksState", false, () => SetStateFilter(s)));
        }
        sp.Children.Add(states);
        return sp;
    }

    private RadioButton Chip(object key, string text, string? color, string group, bool isChecked, Action onChecked)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (color != null) content.Children.Add(Ui.Dot(color, 9).Also(d => d.Margin = new Thickness(0, 0, 8, 0)));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var rb = new RadioButton { Style = Ui.S("ChipRadio"), Content = content, GroupName = group + GetHashCode(), IsChecked = isChecked };
        rb.Checked += (_, _) => { if (!_refreshing) onChecked(); };
        System.Windows.Automation.AutomationProperties.SetName(rb, text);
        _chips[key] = rb;
        return rb;
    }

    private bool _refreshing;

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            Project[] projects = ProjectFilter == null ? SampleData.Projects : new[] { ProjectFilter };
            var all = projects.SelectMany(p => p.Leaves).ToList();
            BuildKpis(all);

            _rows.Children.Clear();
            RowCount = 0;
            foreach (var p in projects)
            {
                var tasks = p.Leaves.Where(t => StateFilter == null || t.State == StateFilter).ToList();
                if (tasks.Count == 0) continue;
                _rows.Children.Add(GroupRow(p, tasks.Count));
                foreach (var t in tasks) { _rows.Children.Add(TaskRow(t)); RowCount++; }
            }
            if (RowCount == 0)
            {
                var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 40) };
                empty.Children.Add(Ui.Icon(Ic.CheckCircle, 26, "Brush.Accent").Also(i => i.HorizontalAlignment = HorizontalAlignment.Center));
                empty.Children.Add(Ui.Text("No tasks match these filters", size: 15, weight: FontWeights.SemiBold).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 8, 0, 0); }));
                _rows.Children.Add(empty);
            }
        }
        finally { _refreshing = false; }
    }

    private void BuildKpis(List<ProjectTask> tasks)
    {
        _kpis.Children.Clear();
        var soon = DateTime.Today.AddDays(30);
        Kpi("Tasks", tasks.Count(t => !t.IsMilestone).ToString(), null);
        Kpi("In progress", tasks.Count(t => t.State == TaskState.InProgress).ToString(), "Brush.Accent");
        Kpi("Behind", tasks.Count(t => t.State == TaskState.Late).ToString(), "Brush.Warn");
        Kpi("Complete", tasks.Count(t => t.State == TaskState.Complete && !t.IsMilestone).ToString(), "Brush.Positive");
        Kpi("Milestones in 30 days", tasks.Count(t => t.IsMilestone && t.Finish >= DateTime.Today && t.Finish <= soon).ToString(), "Brush.Milestone");
    }

    private void Kpi(string label, string value, string? brush)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Style = Ui.S("Muted"), FontSize = 13 });
        var number = new TextBlock { Text = value, Style = Ui.S("BigNumber") };
        if (brush != null) number.SetResourceReference(TextBlock.ForegroundProperty, brush);
        sp.Children.Add(number);
        _kpis.Children.Add(Ui.Card(sp).Also(c => c.Margin = new Thickness(0, 0, 14, 0)));
    }

    private static Grid ColumnGrid()
    {
        var g = new Grid();
        foreach (var (_, w) in Columns)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(-w, GridUnitType.Star) : new GridLength(w) });
        return g;
    }

    private static FrameworkElement TableHeader()
    {
        var border = new Border { CornerRadius = new CornerRadius(14, 14, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt");
        var g = ColumnGrid();
        for (int i = 0; i < Columns.Length; i++)
        {
            var t = Ui.Text(Columns[i].Title.ToUpperInvariant(), muted: true, size: 11, weight: FontWeights.SemiBold);
            t.Margin = new Thickness(i == 0 ? 20 : 8, 12, 8, 12);
            g.Children.Add(t.At(i));
        }
        border.Child = g;
        return border;
    }

    private static FrameworkElement GroupRow(Project p, int count)
    {
        var row = new DockPanel { Margin = new Thickness(20, 16, 20, 6) };
        var right = Ui.Text($"{p.PercentComplete:0}% complete · finishes {WorkCalendar.Short(p.FinishDate)}", muted: true, size: 12);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(Ui.Dot(p.Color, 10).Also(d => { d.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(d, Dock.Left); }));
        row.Children.Add(Ui.Text($"{p.Name}  ({count})", size: 14, weight: FontWeights.SemiBold));
        return row;
    }

    private static FrameworkElement TaskRow(ProjectTask t)
    {
        var g = ColumnGrid();
        var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Ui.Icon(t.IsMilestone ? Ic.Milestone : t.State == TaskState.Complete ? Ic.Checked : Ic.Unchecked, 15,
            t.IsMilestone ? "Brush.Milestone" : t.State == TaskState.Complete ? "Brush.Positive" : "Brush.TextMuted", filled: t.IsMilestone).Also(i => i.Margin = new Thickness(0, 0, 10, 0)));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(t.Name, size: 13.5, weight: t.IsCritical ? FontWeights.SemiBold : FontWeights.Normal));
        if (t.Parent != null) texts.Children.Add(Ui.Text(t.Parent.Name, muted: true, size: 11.5));
        name.Children.Add(texts);
        g.Children.Add(name.At(0));

        g.Children.Add(Cell(WorkCalendar.Short(t.Start)).At(1));
        g.Children.Add(Cell(WorkCalendar.Short(t.Finish)).At(2));
        g.Children.Add(Cell(t.DurationText).At(3));

        if (t.IsMilestone) g.Children.Add(Cell("Milestone", muted: true).At(4));
        else
        {
            var progress = new DockPanel { Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            progress.Children.Add(Ui.Text($"{t.PercentComplete:0}%", size: 12).Also(x => { x.Width = 36; x.TextAlignment = TextAlignment.Right; DockPanel.SetDock(x, Dock.Right); }));
            progress.Children.Add(GanttView.Progress(t.PercentComplete).Also(p => p.Margin = new Thickness(0, 0, 4, 0)));
            g.Children.Add(progress.At(4));
        }

        g.Children.Add(Cell(t.Resources.Length == 0 ? "—" : t.ResourceText, muted: t.Resources.Length == 0).At(5));
        g.Children.Add(GanttView.StatePill(t).Also(p => { p.Margin = new Thickness(8, 0, 8, 0); p.VerticalAlignment = VerticalAlignment.Center; }).At(6));

        var b = new Button { Style = Ui.S("RowButton"), Content = g, MinHeight = 52, Padding = new Thickness(0, 6, 0, 6), ToolTip = "Open on the Gantt chart" };
        b.Click += (_, _) => MainWindow.Instance?.OpenTask(t);
        System.Windows.Automation.AutomationProperties.SetName(b, t.Name);
        return b;
    }

    private static TextBlock Cell(string text, bool muted = false) => Ui.Text(text, muted, size: 12.5).Also(t => t.Margin = new Thickness(8, 0, 8, 0));
}
