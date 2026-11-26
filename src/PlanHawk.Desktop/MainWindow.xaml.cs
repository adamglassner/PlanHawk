using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PlanHawk.Models;
using PlanHawk.Services;
using PlanHawk.Views;

namespace PlanHawk;

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private Project? _pendingProject;
    private ProjectTask? _pendingTask;

    /// <summary>The project the Gantt chart, Tasks and Dependencies pages show.</summary>
    public Project CurrentProject { get; private set; } = SampleData.Projects[0];

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };

        NavList.ItemsSource = ModuleCatalog.Sidebar;
        BuildProjectList();

        App.ThemeChanged += OnThemeChanged;
        SourceInitialized += (_, _) => SystemTheme.ApplyTitleBar(this, App.IsDark);
        UpdateThemeButton();
        RestorePlacement();

        Loaded += async (_, _) =>
        {
            if (SelfTest.IsRunning) { Application.Current.Shutdown(await SelfTest.RunAsync(this)); return; }
            if (Screenshots.IsRunning) { Application.Current.Shutdown(await Screenshots.RunAsync(this)); return; }
        };

        GoTo(App.StartPage ?? "home");
    }

    // ----- Navigation -----

    private string? _currentKey;
    private bool _selecting;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_selecting) return; // GoTo is choosing the page itself
        if (sender is FrameworkElement { DataContext: ModuleInfo module } && module.Key != _currentKey)
            Show(module);
    }

    /// <summary>Selects a sidebar section (or Settings) by key. <paramref name="force"/> rebuilds the page even if it's showing.</summary>
    public void GoTo(string key, bool force = false)
    {
        var module = ModuleCatalog.Find(key) ?? ModuleCatalog.Home;
        // Selecting the sidebar item raises Nav_Checked; don't let that build the page a second time
        // (it would use up a pending project/task and then show the default one).
        _selecting = true;
        try
        {
            if (module == ModuleCatalog.Settings)
                foreach (var m in ModuleCatalog.Sidebar) m.IsSelected = false;
            else
                module.IsSelected = true;
        }
        finally { _selecting = false; }
        if (force || _currentKey != module.Key) Show(module);
    }

    private void Show(ModuleInfo module)
    {
        _currentKey = module.Key;
        if (_pendingProject != null) CurrentProject = _pendingProject;
        var task = _pendingTask;
        _pendingProject = null;
        _pendingTask = null;
        FrameworkElement page = module.Key switch
        {
            "home" => new HomeView(),
            "gantt" => new GanttView(CurrentProject, task),
            "tasks" => new TasksView(),
            "dependencies" => new NetworkView(CurrentProject, task),
            _ => new ComingSoonView(module),
        };
        SetPage(page);
        BuildProjectList();
    }

    /// <summary>Opens a project on the Gantt chart (or on the Dependencies page when that's showing).</summary>
    public void OpenProject(Project project, string? page = null)
    {
        page ??= _currentKey is "dependencies" ? "dependencies" : "gantt";
        _pendingProject = project;
        GoTo(page, force: true);
    }

    /// <summary>Opens a project with one task selected (Home, Tasks).</summary>
    public void OpenTask(ProjectTask task, string page = "gantt")
    {
        _pendingProject = task.Project;
        _pendingTask = task;
        GoTo(page, force: true);
    }

    /// <summary>Remembers which project a page switched to, so the other pages follow.</summary>
    public void SetCurrentProject(Project project)
    {
        CurrentProject = project;
        BuildProjectList();
    }

    public object? CurrentPage => PageHost.Content is ScrollViewer { Content: ContentControl { Content: var inner } } ? inner : PageHost.Content;

    private void SetPage(FrameworkElement page)
    {
        // The Gantt chart and the network diagram fill the window; every other page scrolls with the usual Hawkline margins.
        FrameworkElement host = page is GanttView or NetworkView
            ? page
            : new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(40, 32, 40, 36),
                Focusable = false,
                Content = new ContentControl { MaxWidth = 1240, Content = page, Focusable = false },
            };
        PageHost.Content = host;
        if (SelfTest.IsRunning || Screenshots.IsRunning) return;

        // A short fade-and-rise so page changes feel smooth.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void BuildProjectList()
    {
        ProjectList.Children.Clear();
        foreach (var project in SampleData.Projects)
        {
            bool current = project == CurrentProject && _currentKey is "gantt" or "dependencies";
            var row = new DockPanel();
            var pct = new TextBlock { Text = $"{project.PercentComplete:0}%", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            pct.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SidebarMuted");
            DockPanel.SetDock(pct, Dock.Right);
            row.Children.Add(pct);
            row.Children.Add(new Border { Width = 18, Margin = new Thickness(0, 0, 12, 0), Child = Ui.Dot(project.Color, 10).Also(d => d.HorizontalAlignment = HorizontalAlignment.Center) });
            row.Children.Add(new TextBlock { Text = project.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
            var b = new Button
            {
                Style = (Style)FindResource("SidebarButton"), Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(14, 8, 14, 8),
                ToolTip = $"{project.Category} · {project.Tasks.Length} tasks · {WorkCalendar.Short(project.StartDate)} to {WorkCalendar.Short(project.FinishDate)}",
            };
            if (current) b.SetResourceReference(BackgroundProperty, "Brush.SidebarSelected");
            System.Windows.Automation.AutomationProperties.SetName(b, project.Name);
            b.Click += (_, _) => OpenProject(project);
            ProjectList.Children.Add(b);
        }
    }

    // ----- Sidebar buttons -----

    private void NewTask_Click(object sender, RoutedEventArgs e) => Toaster.Soon("Adding tasks");
    private void NewProject_Click(object sender, RoutedEventArgs e) => Toaster.Soon("New projects");
    private void Search_Click(object sender, MouseButtonEventArgs e) => Toaster.Soon("Search");
    private void Settings_Click(object sender, RoutedEventArgs e) => GoTo("settings");
    private void Theme_Click(object sender, RoutedEventArgs e) => App.SetThemePreference(App.IsDark ? AppTheme.Light : AppTheme.Dark);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N: Toaster.Soon("Adding tasks"); e.Handled = true; break;
                case Key.F: Toaster.Soon("Search"); e.Handled = true; break;
                case Key.OemComma: GoTo("settings"); e.Handled = true; break;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        UpdateThemeButton();
        SystemTheme.ApplyTitleBar(this, App.IsDark);
    }

    private void UpdateThemeButton()
    {
        // Sun when dark (click for light), moon when light (click for dark).
        ThemeGlyph.Text = App.IsDark ? Ic.Sun : Ic.Moon;
        ThemeLabel.Text = App.IsDark ? "Light mode" : "Dark mode";
        ThemeButton.ToolTip = App.IsDark ? "Switch to light mode" : "Switch to dark mode";
    }

    // ----- Toast -----

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        ToastShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (Toast.Opacity == 0) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    // ----- Window placement -----

    private void RestorePlacement()
    {
        var s = App.Settings;
        if (s.WindowWidth is double w && s.WindowHeight is double h && s.WindowLeft is double l && s.WindowTop is double t)
        {
            // Only restore if it's still on screen
            if (l + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && t >= SystemParameters.VirtualScreenTop - 10 &&
                l + w > SystemParameters.VirtualScreenLeft + 100 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t; Width = Math.Max(w, MinWidth); Height = Math.Max(h, MinHeight);
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var s = App.Settings;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.WindowLeft = r.Left; s.WindowTop = r.Top; s.WindowWidth = r.Width; s.WindowHeight = r.Height;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.Save();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
