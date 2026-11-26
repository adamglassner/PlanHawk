using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using PlanHawk.Models;
using PlanHawk.Services;

namespace PlanHawk.Views;

/// <summary>Home: greeting with quick actions, project cards with mini Gantt charts, upcoming milestones, work due this
/// week, tasks that are behind and a timeline of every project. Uses the sample projects.</summary>
public sealed class HomeView : UserControl
{
    public HomeView()
    {
        var root = new StackPanel();
        root.Children.Add(Hero());

        root.Children.Add(SectionHeader("Projects", "Open the Gantt chart", () => MainWindow.Instance?.GoTo("gantt")));
        var cards = new UniformGrid { Columns = SampleData.Projects.Length, Margin = new Thickness(0, 0, -14, 22) };
        foreach (var p in SampleData.Projects)
            cards.Children.Add(ProjectCard(p).Also(c => c.Margin = new Thickness(0, 0, 14, 0)));
        root.Children.Add(cards);

        var mid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        mid.ColumnDefinitions.Add(new ColumnDefinition());
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        mid.ColumnDefinitions.Add(new ColumnDefinition());
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        mid.ColumnDefinitions.Add(new ColumnDefinition());
        mid.Children.Add(Milestones().At(0));
        mid.Children.Add(DueThisWeek().At(2));
        mid.Children.Add(Behind().At(4));
        root.Children.Add(mid);

        root.Children.Add(Portfolio());
        Content = root;
    }

    private static FrameworkElement Hero()
    {
        var hero = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(30, 26, 30, 26), Margin = new Thickness(0, 0, 0, 26) };
        hero.SetResourceReference(Border.BackgroundProperty, "Brush.Hero");
        var dock = new DockPanel();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(HeroAction(Ic.Briefcase, "New project", "New projects"));
        actions.Children.Add(HeroAction(Ic.Add, "New task", "Adding tasks"));
        actions.Children.Add(HeroAction(Ic.FolderOpen, "Open .mpp file", "Opening Microsoft Project files"));
        actions.Children.Add(HeroAction(Ic.Template, "From template", "Project templates"));
        DockPanel.SetDock(actions, Dock.Right);
        dock.Children.Add(actions);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        int hour = DateTime.Now.Hour;
        string greeting = hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
        var title = new TextBlock { Text = greeting, FontSize = 30, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.HeroText");
        text.Children.Add(title);
        int tasks = SampleData.AllLeaves.Count(t => !t.IsMilestone);
        int behind = SampleData.AllLeaves.Count(t => t.State == TaskState.Late);
        text.Children.Add(Ui.Text($"{DateTime.Now:dddd, MMMM d}  ·  {SampleData.Projects.Length} sample projects, {tasks} tasks, {behind} behind schedule", size: 14, brushKey: "Brush.HeroMuted").Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
        dock.Children.Add(text);
        hero.Child = dock;
        return hero;
    }

    private static Button HeroAction(string glyph, string label, string feature)
    {
        var b = new Button { Style = Ui.S("BaseButton"), Tag = feature, Width = 108, Padding = new Thickness(8, 12, 8, 10), Margin = new Thickness(8, 0, 0, 0) };
        b.SetResourceReference(BackgroundProperty, "Brush.SidebarCard");
        var sp = new StackPanel();
        sp.Children.Add(Ui.Icon(glyph, 22, "Brush.Highlight").Also(i => i.HorizontalAlignment = HorizontalAlignment.Center));
        sp.Children.Add(Ui.Text(label, size: 12.5, brushKey: "Brush.HeroText").Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 7, 0, 0); }));
        b.Content = sp;
        b.Click += (s, _) => Toaster.Soon(s);
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        return b;
    }

    private static DockPanel SectionHeader(string title, string? link, Action? onLink)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (link != null)
        {
            var b = new Button { Style = Ui.S("LinkButton"), Content = link };
            b.Click += (_, _) => onLink?.Invoke();
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        dock.Children.Add(new TextBlock { Text = title, Style = Ui.S("CardTitle"), VerticalAlignment = VerticalAlignment.Center });
        return dock;
    }

    private static FrameworkElement ProjectCard(Project p)
    {
        var sp = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(Ui.Dot(p.Color, 9).Also(d => { d.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(d, Dock.Left); }));
        top.Children.Add(Ui.Text($"{p.PercentComplete:0}%", size: 13, weight: FontWeights.SemiBold, brushKey: "Brush.Accent").Also(t => DockPanel.SetDock(t, Dock.Right)));
        top.Children.Add(Ui.Text(p.Category, muted: true, size: 11.5));
        sp.Children.Add(top);
        sp.Children.Add(Ui.Text(p.Name, size: 16, weight: FontWeights.SemiBold));
        var desc = Ui.Wrap(p.Description, muted: true, size: 12.5, lineHeight: 18);
        desc.Height = 36;
        desc.TextTrimming = TextTrimming.CharacterEllipsis;
        desc.Margin = new Thickness(0, 5, 0, 12);
        sp.Children.Add(desc);
        sp.Children.Add(MiniGantt(p));
        sp.Children.Add(GanttView.Progress(p.PercentComplete).Also(x => x.Margin = new Thickness(0, 12, 0, 10)));
        var foot = new DockPanel();
        int late = p.LateCount;
        foot.Children.Add(Ui.Text(late > 0 ? $"{late} behind" : "On track", size: 11.5, brushKey: late > 0 ? "Brush.Warn" : "Brush.Positive").Also(t => DockPanel.SetDock(t, Dock.Right)));
        foot.Children.Add(Ui.Text($"{WorkCalendar.Short(p.StartDate)} → {WorkCalendar.Short(p.FinishDate)}  ·  {p.Leaves.Length} tasks", muted: true, size: 11.5));
        sp.Children.Add(foot);

        var button = new Button { Style = Ui.S("CardButton"), Content = new Border { Style = Ui.S("Card"), Padding = new Thickness(18, 16, 18, 16), Child = sp }, ToolTip = "Open on the Gantt chart" };
        button.Click += (_, _) => MainWindow.Instance?.OpenProject(p, "gantt");
        System.Windows.Automation.AutomationProperties.SetName(button, p.Name);
        return button;
    }

    /// <summary>A tiny Gantt chart of the project's top-level tasks, with a today line.</summary>
    private static FrameworkElement MiniGantt(Project p)
    {
        var rows = p.Tasks.Where(t => t.Parent == null).ToList();
        if (rows.Count > 7) rows = rows.Where(t => !t.IsMilestone).Take(7).ToList();
        const double rowH = 9;
        var canvas = new Canvas { Height = rows.Count * rowH, ClipToBounds = true, IsHitTestVisible = false };
        canvas.SizeChanged += (_, _) =>
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double days = Math.Max(1, (p.FinishDate - p.StartDate).TotalDays + 1);
            double X(DateTime d) => (d - p.StartDate).TotalDays / days * w;
            for (int i = 0; i < rows.Count; i++)
            {
                var t = rows[i];
                double x0 = X(t.Start), x1 = X(t.Finish.AddDays(1));
                if (t.IsMilestone)
                {
                    var dia = new Rectangle { Width = 6, Height = 6, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45) };
                    dia.SetResourceReference(Shape.FillProperty, "Brush.Milestone");
                    Canvas.SetLeft(dia, Math.Min(w - 6, x1 - 3));
                    Canvas.SetTop(dia, i * rowH + 1);
                    canvas.Children.Add(dia);
                    continue;
                }
                var bar = new Border { Width = Math.Max(3, x1 - x0), Height = 5, CornerRadius = new CornerRadius(2.5) };
                bar.SetResourceReference(Border.BackgroundProperty, t.State == TaskState.Complete ? "Brush.BarProgress" : t.State == TaskState.Late ? "Brush.Warn" : "Brush.Bar");
                Canvas.SetLeft(bar, x0);
                Canvas.SetTop(bar, i * rowH + 2);
                canvas.Children.Add(bar);
            }
            double today = X(DateTime.Today);
            if (today > 0 && today < w)
            {
                var line = new Rectangle { Width = 1.5, Height = canvas.Height };
                line.SetResourceReference(Shape.FillProperty, "Brush.Today");
                Canvas.SetLeft(line, today);
                canvas.Children.Add(line);
            }
        };
        return canvas;
    }

    private static Button TaskRow(ProjectTask t, string glyph, string glyphBrush, string right, string? rightBrush = null)
    {
        var b = new Button { Style = Ui.S("BaseButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(-10, 0, -10, 0), ToolTip = "Open on the Gantt chart" };
        var row = new DockPanel();
        row.Children.Add(Ui.Icon(glyph, 15, glyphBrush, filled: glyph == Ic.Milestone).Also(i => { i.Margin = new Thickness(0, 0, 11, 0); DockPanel.SetDock(i, Dock.Left); }));
        row.Children.Add(Ui.Text(right, muted: rightBrush == null, size: 12, brushKey: rightBrush).Also(x => { x.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(x, Dock.Right); }));
        var texts = new StackPanel();
        texts.Children.Add(Ui.Text(t.Name, size: 13.5));
        texts.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Dot(t.Project.Color, 6).Also(d => d.Margin = new Thickness(0, 0, 6, 0)), Ui.Text(t.Project.Name, muted: true, size: 11.5) } });
        row.Children.Add(texts);
        b.Content = row;
        b.Click += (_, _) => MainWindow.Instance?.OpenTask(t);
        System.Windows.Automation.AutomationProperties.SetName(b, t.Name);
        return b;
    }

    private static string DaysAway(DateTime d)
    {
        int days = (d.Date - DateTime.Today).Days;
        return days switch { 0 => "Today", 1 => "Tomorrow", < 0 => $"{-days} days ago", < 7 => d.ToString("dddd"), _ => d.ToString("MMM d") };
    }

    private static FrameworkElement Empty(string text) => Ui.Text(text, muted: true, size: 13).Also(t => t.Margin = new Thickness(0, 6, 0, 6));

    private static FrameworkElement Milestones()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.CardHeader(Ic.Milestone, "Upcoming milestones"));
        var list = SampleData.AllLeaves.Where(t => t.IsMilestone && t.Finish >= DateTime.Today).OrderBy(t => t.Finish).Take(5).ToList();
        if (list.Count == 0) sp.Children.Add(Empty("No milestones coming up."));
        foreach (var t in list) sp.Children.Add(TaskRow(t, Ic.Milestone, "Brush.Milestone", DaysAway(t.Finish)));
        return Ui.Card(sp);
    }

    private static FrameworkElement DueThisWeek()
    {
        var sp = new StackPanel();
        var end = DateTime.Today.AddDays(7);
        sp.Children.Add(Ui.CardHeader(Ic.Calendar, "Due in the next 7 days", null, new Button { Style = Ui.S("LinkButton"), Content = "All tasks" }
            .Also(b => b.Click += (_, _) => MainWindow.Instance?.GoTo("tasks"))));
        var list = SampleData.AllLeaves.Where(t => !t.IsMilestone && t.State != TaskState.Complete && t.Finish >= DateTime.Today && t.Finish <= end)
            .OrderBy(t => t.Finish).Take(5).ToList();
        if (list.Count == 0) sp.Children.Add(Empty("Nothing due this week."));
        foreach (var t in list) sp.Children.Add(TaskRow(t, Ic.Unchecked, "Brush.TextMuted", DaysAway(t.Finish)));
        return Ui.Card(sp);
    }

    private static FrameworkElement Behind()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.CardHeader(Ic.Warning, "Needs attention"));
        var list = SampleData.AllLeaves.Where(t => t.State == TaskState.Late).OrderBy(t => t.Finish).Take(5).ToList();
        if (list.Count == 0) sp.Children.Add(Empty("Everything is on track."));
        foreach (var t in list) sp.Children.Add(TaskRow(t, Ic.Warning, "Brush.Warn", $"{t.PercentComplete:0}% done", "Brush.Warn"));
        if (list.Count > 0)
            sp.Children.Add(Ui.Wrap("Behind means the task has done less work than its dates say it should have by now.", muted: true, size: 12, lineHeight: 17).Also(w => w.Margin = new Thickness(0, 8, 0, 0)));
        return Ui.Card(sp);
    }

    /// <summary>Every project as one bar on a shared month scale.</summary>
    private static FrameworkElement Portfolio()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.CardHeader(Ic.Timeline, "All projects", "PREVIEW"));
        var start = new DateTime(SampleData.Projects.Min(p => p.StartDate).Year, SampleData.Projects.Min(p => p.StartDate).Month, 1);
        var lastFinish = SampleData.Projects.Max(p => p.FinishDate);
        var end = new DateTime(lastFinish.Year, lastFinish.Month, 1).AddMonths(1);
        const double labelW = 170, rowH = 34;
        var canvas = new Canvas { Height = 26 + SampleData.Projects.Length * rowH, ClipToBounds = true };
        canvas.SizeChanged += (_, _) =>
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth - labelW;
            double days = (end - start).TotalDays;
            double X(DateTime d) => labelW + (d - start).TotalDays / days * w;
            for (var m = start; m < end; m = m.AddMonths(1))
            {
                var line = new Rectangle { Width = 1, Height = canvas.Height };
                line.SetResourceReference(Shape.FillProperty, "Brush.GridLine");
                Canvas.SetLeft(line, X(m));
                canvas.Children.Add(line);
                var label = Ui.Text(m.ToString(m.Month == 1 || m == start ? "MMM yyyy" : "MMM"), muted: true, size: 11.5);
                Canvas.SetLeft(label, X(m) + 6);
                Canvas.SetTop(label, 2);
                canvas.Children.Add(label);
            }
            for (int i = 0; i < SampleData.Projects.Length; i++)
            {
                var p = SampleData.Projects[i];
                double y = 26 + i * rowH;
                var name = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Dot(p.Color, 9).Also(d => d.Margin = new Thickness(0, 0, 8, 0)), Ui.Text(p.Name, size: 13) } };
                Canvas.SetTop(name, y + 7);
                canvas.Children.Add(name);
                double x0 = X(p.StartDate), x1 = X(p.FinishDate.AddDays(1));
                var bar = new Grid { Width = Math.Max(6, x1 - x0), Height = 18, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = $"{p.Name}: {WorkCalendar.Short(p.StartDate)} to {WorkCalendar.Short(p.FinishDate)}, {p.PercentComplete:0}% complete" };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Ui.Hex(p.Color), Opacity = 0.35 });
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Ui.Hex(p.Color), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(0, (x1 - x0) * p.PercentComplete / 100) });
                bar.MouseLeftButtonUp += (_, _) => MainWindow.Instance?.OpenProject(p, "gantt");
                Canvas.SetLeft(bar, x0);
                Canvas.SetTop(bar, y + 8);
                canvas.Children.Add(bar);
            }
            double today = X(DateTime.Today);
            var todayLine = new Rectangle { Width = 2, Height = canvas.Height - 20 };
            todayLine.SetResourceReference(Shape.FillProperty, "Brush.Today");
            Canvas.SetLeft(todayLine, today);
            Canvas.SetTop(todayLine, 20);
            canvas.Children.Add(todayLine);
        };
        sp.Children.Add(canvas);
        return Ui.Card(sp);
    }
}
