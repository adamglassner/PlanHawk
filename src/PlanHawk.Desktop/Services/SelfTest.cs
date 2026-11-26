using System.IO;
using System.Windows;
using System.Windows.Threading;
using PlanHawk.Models;
using PlanHawk.Views;

namespace PlanHawk.Services;

/// <summary>
/// <c>PlanHawk.exe --self-test &lt;file&gt;</c>: opens every page in light and dark mode, every sample project and task, writes PASS/FAIL lines to the file and exits with the number of failures. Used by CI.
/// </summary>
public static class SelfTest
{
    public static string? OutputFile { get; set; }
    public static bool IsRunning => OutputFile != null;

    private static readonly List<string> Lines = [];
    private static int _failures;

    public static void RecordUnhandled(Exception ex) => Fail("unhandled", ex);

    private static void Pass(string what) => Lines.Add($"PASS  {what}");

    private static void Fail(string what, Exception ex)
    {
        _failures++;
        Lines.Add($"FAIL  {what}: {ex.GetType().Name}: {ex.Message}");
    }

    private static async Task Check(string what, Func<Task> action)
    {
        try { await action(); Pass(what); }
        catch (Exception ex) { Fail(what, ex); }
    }

    public static Task Idle() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    public static async Task<int> RunAsync(MainWindow window)
    {
        Lines.Add($"PlanHawk {App.VersionText} self-test, {DateTime.Now:yyyy-MM-dd h:mm tt}");
        foreach (var mode in new[] { AppTheme.Light, AppTheme.Dark })
        {
            App.ApplyTheme(mode);
            foreach (var module in ModuleCatalog.All)
            {
                await Check($"{mode}: {module.Title}", async () =>
                {
                    window.GoTo(module.Key, force: true);
                    await Idle();
                    if (window.CurrentPage == null) throw new InvalidOperationException("no page shown");
                });
            }
            foreach (var project in SampleData.Projects)
            {
                await Check($"{mode}: Gantt chart for {project.Name}", async () =>
                {
                    window.OpenProject(project, "gantt");
                    await Idle();
                    var view = window.CurrentPage as GanttView ?? throw new InvalidOperationException("Gantt chart not shown");
                    if (view.Project != project) throw new InvalidOperationException("wrong project");
                    foreach (var task in project.Tasks) { view.Select(task); if (view.Selected != task) throw new InvalidOperationException($"could not select {task.Name}"); }
                    await Idle();
                    foreach (var summary in project.Tasks.Where(t => t.IsSummary))
                    {
                        int before = view.VisibleTasks.Count;
                        view.ToggleCollapse(summary);
                        if (view.VisibleTasks.Count != before - summary.Descendants.Count()) throw new InvalidOperationException($"collapsing {summary.Name} hid the wrong rows");
                        view.ToggleCollapse(summary);
                        if (view.VisibleTasks.Count != before) throw new InvalidOperationException($"expanding {summary.Name} didn't bring its rows back");
                    }
                    foreach (var scale in Enum.GetValues<TimeScale>()) { view.SetScale(scale); await Idle(); }
                    view.ToggleCritical();
                    await Idle();
                    if (!project.Leaves.Any(t => t.IsCritical)) throw new InvalidOperationException("no critical path");
                    view.ToggleCritical();
                    view.ScrollToToday();
                });
                await Check($"{mode}: schedule for {project.Name} follows its dependencies", () =>
                {
                    foreach (var (from, to, link) in project.Links)
                    {
                        int need = (link.Type == LinkType.SS ? from.StartIndex : from.EndIndex) + link.Lag;
                        if (to.StartIndex < need) throw new InvalidOperationException($"{to.Name} starts before {from.Name} allows");
                    }
                    foreach (var t in project.Tasks)
                        if (WorkCalendar.IsWorkday(t.Start) == false || t.Finish < t.Start) throw new InvalidOperationException($"bad dates on {t.Name}");
                    return Task.CompletedTask;
                });
                await Check($"{mode}: Dependencies for {project.Name}", async () =>
                {
                    window.OpenProject(project, "dependencies");
                    await Idle();
                    var view = window.CurrentPage as NetworkView ?? throw new InvalidOperationException("Dependencies page not shown");
                    if (view.NodeCount != project.Leaves.Length) throw new InvalidOperationException($"{view.NodeCount} boxes for {project.Leaves.Length} tasks");
                    foreach (var task in project.Leaves) view.Select(task);
                    view.ToggleCritical();
                    view.Select(null);
                    await Idle();
                });
            }
            await Check($"{mode}: Tasks filters", async () =>
            {
                window.GoTo("tasks", force: true);
                await Idle();
                var view = window.CurrentPage as TasksView ?? throw new InvalidOperationException("Tasks page not shown");
                if (view.RowCount != SampleData.AllLeaves.Count()) throw new InvalidOperationException($"{view.RowCount} rows, expected {SampleData.AllLeaves.Count()}");
                foreach (var p in SampleData.Projects)
                {
                    view.SetProjectFilter(p);
                    if (view.RowCount != p.Leaves.Length) throw new InvalidOperationException($"filter {p.Name}: {view.RowCount} rows");
                }
                view.SetProjectFilter(null);
                foreach (var state in Enum.GetValues<TaskState>()) view.SetStateFilter(state);
                view.SetStateFilter(null);
                await Idle();
            });
            await Check($"{mode}: open a task from Tasks", async () =>
            {
                var task = SampleData.Projects[1].Leaves[3];
                window.OpenTask(task);
                await Idle();
                if ((window.CurrentPage as GanttView)?.Selected != task) throw new InvalidOperationException("task not selected on the Gantt chart");
            });
            await Check($"{mode}: toast", async () => { Toaster.Soon("Self-test"); await Idle(); });
        }
        Lines.Add(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        try { File.WriteAllLines(OutputFile!, Lines); } catch { /* nothing else we can do */ }
        return _failures;
    }
}
