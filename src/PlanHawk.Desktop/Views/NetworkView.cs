using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PlanHawk.Models;
using PlanHawk.Services;

namespace PlanHawk.Views;

/// <summary>
/// Dependencies: a network diagram (like Microsoft Project's) where every task is a box, placed in columns by how long its
/// chain of predecessors is, with arrows for each link. Selecting a task highlights what it waits for and what waits on it.
/// The side panel lists every link. Adding and removing links is coming soon.
/// </summary>
public sealed class NetworkView : UserControl
{
    private const double NodeW = 210, NodeH = 88, ColGap = 74, RowGap = 22, Pad = 28;

    public Project Project { get; private set; }
    public ProjectTask? Selected { get; private set; }
    public bool ShowCritical { get; private set; } = true;
    public int NodeCount => _nodes.Count;

    private readonly Dictionary<ProjectTask, Rect> _nodes = [];
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly StackPanel _side = new();
    private readonly Button _projectButton = new() { Style = Ui.S("BaseButton"), Padding = new Thickness(8, 6, 10, 6), Margin = new Thickness(0, 0, 18, 0) };
    private readonly Button _criticalButton = new() { Style = Ui.S("GhostButton"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Show the critical path" };

    public NetworkView(Project project, ProjectTask? select = null)
    {
        Project = project;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(BuildToolbar().At(0, 0));

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        _scroll.Content = _canvas;
        _scroll.SetResourceReference(BackgroundProperty, "Brush.Bg");
        body.Children.Add(_scroll.At(0));
        body.Children.Add(BuildLegend().At(0));
        var side = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        side.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        side.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        side.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Padding = new Thickness(20, 18, 20, 18), Content = _side };
        body.Children.Add(side.At(1));
        root.Children.Add(body.At(0, 1));
        Content = root;

        _canvas.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _canvas) Select(null); };
        Loaded += (_, _) => { if (Selected != null) Select(Selected); };
        ShowProject(project);
        if (select != null) Select(select.IsSummary ? select.Descendants.FirstOrDefault(d => !d.IsSummary) : select);
    }

    public void ShowProject(Project project)
    {
        Project = project;
        Selected = null;
        MainWindow.Instance?.SetCurrentProject(project);
        UpdateProjectButton();
        LayoutNodes();
        Draw();
        BuildSide();
        _scroll.ScrollToHome();
    }

    public void Select(ProjectTask? task)
    {
        Selected = task != null && _nodes.ContainsKey(task) ? task : null;
        Draw();
        BuildSide();
        if (Selected != null && IsLoaded)
        {
            var r = _nodes[Selected];
            if (r.Left < _scroll.HorizontalOffset || r.Right > _scroll.HorizontalOffset + _scroll.ViewportWidth)
                _scroll.ScrollToHorizontalOffset(Math.Max(0, r.Left - 120));
            if (r.Top < _scroll.VerticalOffset || r.Bottom > _scroll.VerticalOffset + _scroll.ViewportHeight)
                _scroll.ScrollToVerticalOffset(Math.Max(0, r.Top - 80));
        }
    }

    public void ToggleCritical()
    {
        ShowCritical = !ShowCritical;
        UpdateCriticalButton();
        Draw();
    }

    // ===== Toolbar =====

    private FrameworkElement BuildToolbar()
    {
        var bar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 12, 20, 12) };
        bar.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        bar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var dock = new DockPanel();

        _projectButton.Click += (_, _) => ShowProjectMenu();
        System.Windows.Automation.AutomationProperties.SetName(_projectButton, "Switch project");
        dock.Children.Add(_projectButton.Also(b => DockPanel.SetDock(b, Dock.Left)));

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(Ui.SoonButton("Linking tasks", Ic.Link, "Link tasks").Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
        tools.Children.Add(Ui.SoonButton("Unlinking tasks", Ic.Unlink, "Unlink").Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
        _criticalButton.Click += (_, _) => ToggleCritical();
        System.Windows.Automation.AutomationProperties.SetName(_criticalButton, "Critical path");
        UpdateCriticalButton();
        tools.Children.Add(_criticalButton);
        tools.Children.Add(Ui.ToolButton("Changing the diagram layout", Ic.Grid, "Layout options"));
        dock.Children.Add(tools);
        bar.Child = dock;
        return bar;
    }

    /// <summary>Color key, floating at the bottom left of the diagram.</summary>
    private static FrameworkElement BuildLegend()
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        legend.Children.Add(Legend("Brush.Bar", "Task"));
        legend.Children.Add(Legend("Brush.Critical", "Critical"));
        legend.Children.Add(Legend("Brush.Milestone", "Milestone"));
        legend.Children.Add(Legend("Brush.Positive", "Complete"));
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(2, 7, 14, 7), Margin = new Thickness(16, 0, 0, 22), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = legend, IsHitTestVisible = false };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return card;
    }

    private static FrameworkElement Legend(string brush, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) };
        swatch.SetResourceReference(Border.BackgroundProperty, brush);
        sp.Children.Add(swatch);
        sp.Children.Add(Ui.Text(text, muted: true, size: 12));
        return sp;
    }

    private void UpdateProjectButton()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Dot(Project.Color, 12).Also(d => d.Margin = new Thickness(0, 0, 9, 0)));
        row.Children.Add(Ui.Text(Project.Name, size: 16, weight: FontWeights.Bold).Also(t => t.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont")));
        row.Children.Add(Ui.Icon(Ic.ChevronDown, 12, "Brush.TextMuted").Also(i => i.Margin = new Thickness(8, 2, 0, 0)));
        _projectButton.Content = row;
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

    // ===== Layout + drawing =====

    private void LayoutNodes()
    {
        _nodes.Clear();
        var leaves = Project.Leaves;
        var level = new Dictionary<ProjectTask, int>();
        int LevelOf(ProjectTask t)
        {
            if (level.TryGetValue(t, out int l)) return l;
            level[t] = 0; // guards against loops
            int best = 0;
            foreach (var link in t.Predecessors)
            {
                var p = Project.Find(link.PredecessorId);
                foreach (var leaf in p.IsSummary ? p.Descendants.Where(d => !d.IsSummary) : new[] { p })
                    best = Math.Max(best, LevelOf(leaf) + 1);
            }
            return level[t] = best;
        }
        foreach (var t in leaves) LevelOf(t);
        foreach (var column in leaves.GroupBy(t => level[t]).OrderBy(g => g.Key))
        {
            int row = 0;
            foreach (var t in column.OrderBy(t => t.StartIndex).ThenBy(t => t.Id))
            {
                _nodes[t] = new Rect(Pad + column.Key * (NodeW + ColGap), Pad + row * (NodeH + RowGap), NodeW, NodeH);
                row++;
            }
        }
        _canvas.Width = _nodes.Values.Max(r => r.Right) + Pad;
        _canvas.Height = _nodes.Values.Max(r => r.Bottom) + Pad;
    }

    private bool Related(ProjectTask t) => Selected != null && (t == Selected
        || Selected.Predecessors.Any(l => l.PredecessorId == t.Id)
        || Selected.Successors.Any(s => s.Task == t));

    private void Draw()
    {
        _canvas.Children.Clear();
        // Links first, so boxes sit on top of them.
        foreach (var (from, to, link) in Project.Links)
        {
            if (!_nodes.TryGetValue(from, out var a) || !_nodes.TryGetValue(to, out var b)) continue;
            bool hot = Selected != null && (from == Selected || to == Selected);
            bool crit = ShowCritical && from.IsCritical && to.IsCritical;
            string brush = hot ? "Brush.Accent" : crit ? "Brush.Critical" : "Brush.Arrow";
            double x1 = a.Right, y1 = a.Top + a.Height / 2, x2 = b.Left, y2 = b.Top + b.Height / 2;
            double xm = x2 - ColGap / 2;
            var line = new Polyline
            {
                Points = [new Point(x1, y1), new Point(xm, y1), new Point(xm, y2), new Point(x2 - 6, y2)],
                StrokeThickness = hot ? 2.2 : crit ? 1.8 : 1.3, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
                Opacity = Selected != null && !hot ? 0.35 : 1,
            };
            line.SetResourceReference(Shape.StrokeProperty, brush);
            _canvas.Children.Add(line);
            var head = new Polygon { Points = [new Point(x2, y2), new Point(x2 - 8, y2 - 5), new Point(x2 - 8, y2 + 5)], IsHitTestVisible = false, Opacity = line.Opacity };
            head.SetResourceReference(Shape.FillProperty, brush);
            _canvas.Children.Add(head);
            if (link.Type != LinkType.FS || link.Lag != 0)
            {
                var tag = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 1, 5, 1), Opacity = line.Opacity, IsHitTestVisible = false, Child = Ui.Text(link.Type + (link.Lag != 0 ? $"+{link.Lag}d" : ""), size: 10.5, weight: FontWeights.SemiBold, brushKey: hot ? "Brush.AccentText" : "Brush.Text") };
                tag.SetResourceReference(Border.BackgroundProperty, hot ? "Brush.Accent" : "Brush.SurfaceAlt");
                Canvas.SetLeft(tag, xm - 22);
                Canvas.SetTop(tag, (y1 + y2) / 2 - 9);
                _canvas.Children.Add(tag);
            }
        }
        foreach (var (t, r) in _nodes) _canvas.Children.Add(Node(t, r));
    }

    private FrameworkElement Node(ProjectTask t, Rect r)
    {
        bool crit = ShowCritical && t.IsCritical;
        string stripe = t.IsMilestone ? "Brush.Milestone" : t.State == TaskState.Complete ? "Brush.Positive" : crit ? "Brush.Critical" : "Brush.Bar";
        var card = new Border { Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(t == Selected ? 2 : 1), Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, t == Selected ? "Brush.Accent" : Related(t) ? "Brush.Highlight" : crit ? "Brush.Critical" : "Brush.Border");
        if (Selected != null && !Related(t)) card.Opacity = 0.5;
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.08 };

        var dock = new DockPanel();
        var bar = new Border { Width = 5, CornerRadius = new CornerRadius(11, 0, 0, 11) };
        bar.SetResourceReference(Border.BackgroundProperty, stripe);
        DockPanel.SetDock(bar, Dock.Left);
        dock.Children.Add(bar);

        var sp = new StackPanel { Margin = new Thickness(11, 9, 11, 8) };
        var top = new DockPanel();
        top.Children.Add(Ui.Text(t.Id.ToString(), muted: true, size: 11.5).Also(x => DockPanel.SetDock(x, Dock.Right)));
        if (t.IsMilestone) top.Children.Add(Ui.Icon(Ic.Milestone, 12, "Brush.Milestone", filled: true).Also(i => { i.Margin = new Thickness(0, 0, 6, 0); DockPanel.SetDock(i, Dock.Left); }));
        top.Children.Add(Ui.Text(t.Name, size: 13, weight: FontWeights.SemiBold));
        sp.Children.Add(top);
        sp.Children.Add(Ui.Text(t.IsMilestone ? WorkCalendar.Short(t.Finish) : $"{WorkCalendar.Short(t.Start)} → {WorkCalendar.Short(t.Finish)}", muted: true, size: 11.5).Also(x => x.Margin = new Thickness(0, 4, 0, 0)));
        sp.Children.Add(Ui.Text(t.IsMilestone ? "Milestone" : $"{t.DurationText} · {t.PercentComplete:0}% done", muted: true, size: 11.5));
        sp.Children.Add(Ui.Text(t.Resources.Length == 0 ? " " : t.ResourceText, size: 11.5, brushKey: "Brush.Accent"));
        dock.Children.Add(sp);
        card.Child = dock;
        card.ToolTip = $"{t.Path}\n{(t.IsCritical ? "On the critical path" : $"{t.TotalSlack} day(s) of slack")}";
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) MainWindow.Instance?.OpenTask(t);
            else Select(t == Selected ? null : t);
        };
        System.Windows.Automation.AutomationProperties.SetName(card, t.Name);
        Canvas.SetLeft(card, r.Left);
        Canvas.SetTop(card, r.Top);
        return card;
    }

    // ===== Side panel =====

    private void BuildSide()
    {
        _side.Children.Clear();
        var t = Selected;
        if (t != null)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var close = new Button { Style = Ui.S("BaseButton"), Padding = new Thickness(7), MinWidth = 32, MinHeight = 32, ToolTip = "Clear selection", Content = Ui.Icon(Ic.Dismiss, 14), VerticalAlignment = VerticalAlignment.Top };
            close.SetResourceReference(ForegroundProperty, "Brush.Text");
            System.Windows.Automation.AutomationProperties.SetName(close, "Clear selection");
            close.Click += (_, _) => Select(null);
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(new TextBlock { Text = t.Name, Style = Ui.S("CardTitle"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            _side.Children.Add(head);
            var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            meta.Children.Add(GanttView.StatePill(t).Also(p => p.Margin = new Thickness(0, 0, 8, 4)));
            meta.Children.Add(Ui.Chip(t.IsCritical ? "Critical" : $"{t.TotalSlack} days of slack", Ic.Branch).Also(c => c.Margin = new Thickness(0, 0, 8, 4)));
            _side.Children.Add(meta);

            _side.Children.Add(new TextBlock { Text = "WAITS FOR", Style = Ui.S("SectionLabel") });
            if (t.Predecessors.Length == 0) _side.Children.Add(Ui.Text("Nothing. It can start right away.", muted: true, size: 12.5).Also(x => x.Margin = new Thickness(0, 0, 0, 12)));
            foreach (var l in t.Predecessors) _side.Children.Add(LinkRow(Project.Find(l.PredecessorId), t, l, showFrom: true));
            _side.Children.Add(new TextBlock { Text = "THEN", Style = Ui.S("SectionLabel"), Margin = new Thickness(0, 12, 0, 8) });
            if (t.Successors.Count == 0) _side.Children.Add(Ui.Text("Nothing waits on this task.", muted: true, size: 12.5));
            foreach (var (s, l) in t.Successors) _side.Children.Add(LinkRow(t, s, l, showFrom: false));

            var open = new Button { Style = Ui.S("GhostButton"), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            open.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Icon(Ic.Gantt, 15).Also(i => i.Margin = new Thickness(0, 0, 7, 0)), new TextBlock { Text = "Show on the Gantt chart", VerticalAlignment = VerticalAlignment.Center } } };
            open.Click += (_, _) => MainWindow.Instance?.OpenTask(t);
            _side.Children.Add(open);
            _side.Children.Add(Ui.Separator(vertical: false).Also(s => s.Margin = new Thickness(0, 18, 0, 16)));
        }

        var links = Project.Links.ToList();
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        title.Children.Add(Ui.Text($"{links.Count}", muted: true).Also(x => DockPanel.SetDock(x, Dock.Right)));
        title.Children.Add(new TextBlock { Text = "All dependencies", Style = Ui.S("CardTitle") });
        _side.Children.Add(title);
        _side.Children.Add(Ui.Wrap(t == null ? "Click a task to see what it waits for and what waits on it. Double-click to open it on the Gantt chart." : "Every link in this project.", muted: true, size: 12.5).Also(w => w.Margin = new Thickness(0, 0, 0, 12)));
        foreach (var (from, to, link) in links) _side.Children.Add(LinkRow(from, to, link, showFrom: null));
    }

    /// <summary>One dependency. showFrom: true = show the predecessor, false = the successor, null = both.</summary>
    private FrameworkElement LinkRow(ProjectTask from, ProjectTask to, Link link, bool? showFrom)
    {
        var b = new Button { Style = Ui.S("BaseButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(-8, 0, -8, 2) };
        var row = new DockPanel();
        var type = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = link.TypeName + (link.Lag != 0 ? $", {link.Lag} working day lag" : ""), Child = Ui.Text(link.Type + (link.Lag != 0 ? $"+{link.Lag}d" : ""), size: 11, weight: FontWeights.SemiBold, brushKey: "Brush.Accent") };
        type.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        DockPanel.SetDock(type, Dock.Right);
        row.Children.Add(type);
        if (showFrom == null)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text($"{from.Id}  {from.Name}", size: 12.5));
            sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Icon(Ic.ArrowRight, 11, "Brush.TextMuted").Also(i => i.Margin = new Thickness(0, 0, 6, 0)), Ui.Text($"{to.Id}  {to.Name}", size: 12.5, weight: FontWeights.SemiBold) } });
            row.Children.Add(sp);
        }
        else
        {
            var task = showFrom.Value ? from : to;
            row.Children.Add(Ui.Text(task.Id.ToString(), muted: true, size: 12).Also(x => { x.Width = 26; DockPanel.SetDock(x, Dock.Left); }));
            row.Children.Add(Ui.Text(task.Name, size: 12.5));
        }
        b.Content = row;
        var target = showFrom == true ? from : to;
        b.Click += (_, _) => Select(target);
        b.ToolTip = $"{from.Name} → {to.Name}\n{link.TypeName}{(link.Lag != 0 ? $" with {link.Lag} working day(s) of lag" : "")}";
        System.Windows.Automation.AutomationProperties.SetName(b, $"{from.Name} to {to.Name}");
        return b;
    }
}
