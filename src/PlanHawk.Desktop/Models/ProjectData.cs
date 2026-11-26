namespace PlanHawk.Models;

/// <summary>How a task depends on its predecessor (the same names Microsoft Project uses).</summary>
public enum LinkType
{
    /// <summary>Finish-to-start: the task starts after the predecessor finishes.</summary>
    FS,
    /// <summary>Start-to-start: the task starts once the predecessor has started.</summary>
    SS,
}

/// <summary>A dependency on another task in the same project. <see cref="Lag"/> is in working days.</summary>
public sealed record Link(int PredecessorId, LinkType Type = LinkType.FS, int Lag = 0)
{
    /// <summary>"3", "6SS", "3FS+15d", like Project's Predecessors column.</summary>
    public string Code => PredecessorId + (Type == LinkType.FS && Lag == 0 ? "" : Type.ToString()) + (Lag == 0 ? "" : (Lag > 0 ? "+" : "") + Lag + "d");

    public string TypeName => Type switch
    {
        LinkType.SS => "Start-to-start",
        _ => "Finish-to-start",
    };
}

public enum TaskState { NotStarted, InProgress, Late, Complete }

/// <summary>One row of a project. Durations are in working days; 0 means a milestone.</summary>
public sealed class ProjectTask
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Outline level: 0 is top level, 1 is indented once and so on.</summary>
    public int Level { get; init; }
    public int Duration { get; init; }
    public Link[] Predecessors { get; init; } = [];
    public string[] Resources { get; init; } = [];
    /// <summary>Sample data only: this task is behind schedule.</summary>
    public bool Behind { get; init; }
    public string Notes { get; init; } = "";

    // ----- Filled in by the scheduler -----
    public Project Project { get; internal set; } = null!;
    public ProjectTask? Parent { get; internal set; }
    public List<ProjectTask> Children { get; } = [];
    public List<(ProjectTask Task, Link Link)> Successors { get; } = [];
    /// <summary>Working-day index of the start, counted from the project start.</summary>
    public int StartIndex { get; internal set; }
    /// <summary>Working-day index just after the last day of work (equals StartIndex for milestones).</summary>
    public int EndIndex { get; internal set; }
    public int TotalSlack { get; internal set; }
    public double PercentComplete { get; internal set; }

    public bool IsSummary => Children.Count > 0;
    public bool IsMilestone => Duration == 0 && !IsSummary;
    public bool IsCritical => !IsSummary && TotalSlack <= 0;
    public int WorkDays => EndIndex - StartIndex;
    /// <summary>Milestones sit at the end of the working day before their index, like in Microsoft Project.</summary>
    public DateTime Start => IsMilestone && StartIndex > 0 ? Project.DateOf(StartIndex - 1) : Project.DateOf(StartIndex);
    public DateTime Finish => IsMilestone ? Start : Project.DateOf(EndIndex - 1);
    public string PredecessorText => string.Join(",", Predecessors.Select(p => p.Code));
    public string ResourceText => string.Join(", ", Resources);
    public string DurationText => IsMilestone ? "0 days" : WorkDays == 1 ? "1 day" : $"{WorkDays} days";

    public TaskState State
    {
        get
        {
            if (PercentComplete >= 100) return TaskState.Complete;
            var today = DateTime.Today;
            if (Behind && today >= Start) return TaskState.Late;
            if (Finish < today) return TaskState.Late;
            return PercentComplete > 0 ? TaskState.InProgress : TaskState.NotStarted;
        }
    }

    public string StateText => State switch
    {
        TaskState.Complete => "Complete",
        TaskState.InProgress => "In progress",
        TaskState.Late => "Behind",
        _ => "Not started",
    };

    /// <summary>"Planning › Write the spec"</summary>
    public string Path => Parent == null ? Name : $"{Parent.Name} › {Name}";

    public IEnumerable<ProjectTask> Descendants => Children.SelectMany(c => c.Descendants.Prepend(c));
}

/// <summary>A project: an ordered task list on a Monday-to-Friday calendar.</summary>
public sealed class Project
{
    public required string Name { get; init; }
    public required string Color { get; init; }
    public string Category { get; init; } = "";
    public string Manager { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>First working day (always a weekday).</summary>
    public DateTime StartDate { get; init; }
    public required ProjectTask[] Tasks { get; init; }

    public DateTime FinishDate => Tasks.Max(t => t.Finish);
    public ProjectTask[] Leaves => Tasks.Where(t => !t.IsSummary).ToArray();
    public ProjectTask[] Milestones => Tasks.Where(t => t.IsMilestone).ToArray();
    public double PercentComplete { get; private set; }
    public int WorkDays => Tasks.Max(t => t.EndIndex);
    public int LateCount => Leaves.Count(t => t.State == TaskState.Late);
    public IEnumerable<(ProjectTask From, ProjectTask To, Link Link)> Links =>
        from t in Tasks from l in t.Predecessors select (Find(l.PredecessorId), t, l);

    public ProjectTask Find(int id) => Tasks.First(t => t.Id == id);

    public DateTime DateOf(int workdayIndex) => WorkCalendar.AddWorkdays(StartDate, workdayIndex);

    /// <summary>Works out outline, dates, critical path and progress. Called once by <see cref="SampleData"/>.</summary>
    internal Project Schedule()
    {
        // Outline: a task is the parent of the indented tasks under it.
        var stack = new Stack<ProjectTask>();
        foreach (var t in Tasks)
        {
            t.Project = this;
            while (stack.Count > 0 && stack.Peek().Level >= t.Level) stack.Pop();
            if (stack.Count > 0) { t.Parent = stack.Peek(); t.Parent.Children.Add(t); }
            stack.Push(t);
        }
        foreach (var t in Tasks)
            foreach (var l in t.Predecessors) Find(l.PredecessorId).Successors.Add((t, l));

        var leaves = Leaves;
        // Forward pass (repeat until nothing moves, so the order of the list doesn't matter).
        for (int pass = 0; pass < leaves.Length + 1; pass++)
        {
            bool moved = false;
            foreach (var t in leaves)
            {
                int start = 0;
                foreach (var l in AllLinks(t))
                {
                    var p = Find(l.PredecessorId);
                    start = Math.Max(start, (l.Type == LinkType.SS ? p.StartIndex : p.EndIndex) + l.Lag);
                }
                if (start != t.StartIndex || t.EndIndex != start + t.Duration) moved = true;
                t.StartIndex = start;
                t.EndIndex = start + t.Duration;
            }
            UpdateSummaries();
            if (!moved) break;
        }

        // Backward pass for total slack (critical path = no slack).
        int end = leaves.Max(t => t.EndIndex);
        var lateStart = leaves.ToDictionary(t => t, t => end - t.Duration);
        for (int pass = 0; pass < leaves.Length + 1; pass++)
        {
            bool moved = false;
            foreach (var t in Enumerable.Reverse(leaves))
            {
                int lateEnd = end;
                foreach (var (succ, l) in SuccessorLeaves(t))
                    lateEnd = Math.Min(lateEnd, l.Type == LinkType.SS ? lateStart[succ] - l.Lag + t.Duration : lateStart[succ] - l.Lag);
                if (lateStart[t] != lateEnd - t.Duration) { lateStart[t] = lateEnd - t.Duration; moved = true; }
            }
            if (!moved) break;
        }
        foreach (var t in leaves) t.TotalSlack = lateStart[t] - t.StartIndex;

        // Progress from today's date, so the sample always looks current.
        int todayIndex = WorkCalendar.WorkdaysBetween(StartDate, DateTime.Today);
        foreach (var t in leaves)
        {
            double expected = t.Duration == 0
                ? (Math.Max(0, t.StartIndex - 1) < todayIndex ? 100 : 0)
                : Math.Clamp((todayIndex - t.StartIndex) / (double)t.Duration, 0, 1) * 100;
            if (t.Behind && expected < 100) expected *= 0.5;
            else if (t.Behind) expected = 60;
            t.PercentComplete = Math.Round(expected / 5) * 5;
        }
        foreach (var s in Tasks.Where(t => t.IsSummary).Reverse())
            s.PercentComplete = Weighted(s.Descendants.Where(d => !d.IsSummary));
        PercentComplete = Weighted(leaves);
        return this;
    }

    private static double Weighted(IEnumerable<ProjectTask> tasks)
    {
        var list = tasks.Where(t => t.Duration > 0).ToList();
        double days = list.Sum(t => t.Duration);
        return days == 0 ? 0 : Math.Round(list.Sum(t => t.PercentComplete * t.Duration) / days);
    }

    /// <summary>A task's own links plus its summary tasks' links (a summary's predecessor holds back everything under it).</summary>
    private static IEnumerable<Link> AllLinks(ProjectTask t)
    {
        for (var x = t; x != null; x = x.Parent)
            foreach (var l in x.Predecessors) yield return l;
    }

    private IEnumerable<(ProjectTask, Link)> SuccessorLeaves(ProjectTask t)
    {
        foreach (var (s, l) in t.Successors)
        {
            if (!s.IsSummary) yield return (s, l);
            else foreach (var d in s.Descendants.Where(d => !d.IsSummary)) yield return (d, l);
        }
    }

    private void UpdateSummaries()
    {
        foreach (var s in Tasks.Where(t => t.IsSummary).Reverse())
        {
            s.StartIndex = s.Children.Min(c => c.StartIndex);
            s.EndIndex = s.Children.Max(c => c.EndIndex);
        }
    }
}

/// <summary>Monday-to-Friday working calendar (holidays are coming soon).</summary>
public static class WorkCalendar
{
    public static bool IsWorkday(DateTime d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public static DateTime NextWorkday(DateTime d)
    {
        d = d.Date;
        while (!IsWorkday(d)) d = d.AddDays(1);
        return d;
    }

    public static DateTime AddWorkdays(DateTime start, int days)
    {
        var d = NextWorkday(start);
        int step = days < 0 ? -1 : 1;
        for (int i = 0; i < Math.Abs(days); i++)
        {
            do d = d.AddDays(step); while (!IsWorkday(d));
        }
        return d;
    }

    /// <summary>Number of working days from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public static int WorkdaysBetween(DateTime from, DateTime to)
    {
        from = from.Date; to = to.Date;
        int sign = 1;
        if (to < from) { (from, to) = (to, from); sign = -1; }
        int n = 0;
        for (var d = from; d < to; d = d.AddDays(1)) if (IsWorkday(d)) n++;
        return n * sign;
    }

    public static DateTime MondayOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>"Mon 12/1", the way Project shows dates in the task sheet.</summary>
    public static string Short(DateTime d) => d.ToString("ddd M/d");
    public static string Long(DateTime d) => d.ToString("dddd, MMMM d, yyyy");
    /// <summary>"Mon, Nov 23, 2026"</summary>
    public static string Medium(DateTime d) => d.ToString("ddd, MMM d, yyyy");
}

/// <summary>
/// Sample projects shown while PlanHawk is a shell, so the layout can be judged with realistic content.
/// Dates are relative to today; nothing is saved.
/// </summary>
public static class SampleData
{
    private static ProjectTask T(int id, string name, int level, int days, string preds = "", string res = "", bool behind = false, string notes = "") => new()
    {
        Id = id, Name = name, Level = level, Duration = days, Predecessors = ParseLinks(preds),
        Resources = res.Length == 0 ? [] : res.Split(',', StringSplitOptions.TrimEntries), Behind = behind, Notes = notes,
    };

    private static ProjectTask S(int id, string name, int level = 0, string preds = "") => T(id, name, level, 0, preds);

    /// <summary>"3, 6SS+2, 4FS+15" → links.</summary>
    private static Link[] ParseLinks(string text) => text.Length == 0 ? [] : text.Split(',', StringSplitOptions.TrimEntries).Select(code =>
    {
        int i = 0;
        while (i < code.Length && char.IsDigit(code[i])) i++;
        int id = int.Parse(code[..i]);
        var rest = code[i..];
        var type = rest.StartsWith("SS") ? LinkType.SS : LinkType.FS;
        if (rest.StartsWith("SS") || rest.StartsWith("FS")) rest = rest[2..];
        return new Link(id, type, rest.Length == 0 ? 0 : int.Parse(rest));
    }).ToArray();

    private static DateTime WeeksAgo(int weeks) => WorkCalendar.MondayOf(DateTime.Today).AddDays(-7 * weeks);

    public static readonly Project[] Projects =
    [
        new Project
        {
            Name = "PlanHawk 1.0", Color = "#2BB59E", Category = "Bytehawk Studio", Manager = "Maya Chen",
            Description = "From the shell to the first full release: task sheet, Gantt chart, dependencies and saving projects.",
            StartDate = WeeksAgo(5),
            Tasks =
            [
                S(1, "Planning"),
                T(2, "Gather the feature list", 1, 3, "", "Maya Chen", notes: "Everything Microsoft Project does that people actually use."),
                T(3, "Write the spec", 1, 5, "2", "Maya Chen, Leo Park"),
                T(4, "Spec signed off", 1, 0, "3"),
                S(5, "Design"),
                T(6, "Hawkline mockups", 1, 6, "4", "Priya Shah"),
                T(7, "Icon and branding", 1, 3, "6SS+2", "Priya Shah"),
                T(8, "Design review", 1, 0, "6, 7"),
                S(9, "Build"),
                T(10, "Shell and sidebar", 1, 5, "8", "Leo Park"),
                T(11, "Gantt chart", 1, 10, "10", "Leo Park", behind: true, notes: "Zoom, timescale, bars, milestones, today line."),
                T(12, "Task sheet", 1, 8, "10SS+3", "Sam Ortiz"),
                T(13, "Dependencies and scheduling", 1, 9, "11SS+4, 12", "Leo Park, Sam Ortiz", notes: "Finish-to-start, start-to-start, lag, critical path."),
                T(14, "Save and open projects", 1, 7, "12", "Sam Ortiz"),
                T(15, "Feature complete", 1, 0, "13, 14"),
                S(16, "Test and release"),
                T(17, "Self-test and screenshots", 1, 4, "15", "Sam Ortiz"),
                T(18, "Beta on the family PCs", 1, 5, "17", "Maya Chen"),
                T(19, "Fix beta bugs", 1, 4, "18", "Leo Park, Sam Ortiz"),
                T(20, "Installer and launcher entry", 1, 2, "15", "Leo Park"),
                T(21, "PlanHawk 1.0 released", 1, 0, "19, 20"),
            ],
        }.Schedule(),
        new Project
        {
            Name = "Kitchen remodel", Color = "#F2A93B", Category = "Home", Manager = "Contractor",
            Description = "New cabinets, countertops and backsplash, with plumbing and electrical updated along the way.",
            StartDate = WeeksAgo(2),
            Tasks =
            [
                S(1, "Prep"),
                T(2, "Finalize the layout", 1, 2, "", "Family, Contractor"),
                T(3, "Order cabinets", 1, 1, "2", "Family"),
                T(4, "Cabinets delivered", 1, 0, "3FS+15", notes: "About three weeks after ordering."),
                T(5, "Demolition", 1, 3, "2FS+5", "Contractor"),
                S(6, "Rough-in"),
                T(7, "Plumbing rough-in", 1, 3, "5", "Plumber"),
                T(8, "Electrical rough-in", 1, 3, "5SS+1, 5", "Electrician", behind: true),
                T(9, "City inspection passed", 1, 0, "7, 8", "City inspector"),
                S(10, "Finish"),
                T(11, "Drywall and paint", 1, 5, "9", "Contractor"),
                T(12, "Install cabinets", 1, 3, "4, 11", "Contractor"),
                T(13, "Countertop template", 1, 1, "12", "Countertop shop"),
                T(14, "Countertops installed", 1, 1, "13FS+7", "Countertop shop"),
                T(15, "Backsplash tile", 1, 2, "14", "Contractor"),
                T(16, "Hook up appliances", 1, 1, "14", "Plumber, Electrician"),
                T(17, "Kitchen done!", 1, 0, "15, 16"),
            ],
        }.Schedule(),
        new Project
        {
            Name = "Family reunion", Color = "#7C5CFF", Category = "Personal", Manager = "Family",
            Description = "A big family get-together: date, venue, invitations, food, games and a photo slideshow.",
            StartDate = WeeksAgo(1),
            Tasks =
            [
                T(1, "Pick a date", 0, 3, "", "Family"),
                T(2, "Date chosen", 0, 0, "1"),
                T(3, "Book the venue", 0, 2, "2", "Family"),
                T(4, "Send invitations", 0, 2, "3", "Family"),
                T(5, "RSVPs due", 0, 0, "4FS+15"),
                T(6, "Plan the menu", 0, 3, "2FS+3", "Family"),
                T(7, "Order food", 0, 1, "5, 6", "Family"),
                T(8, "Games and activities", 0, 4, "4", "Family"),
                T(9, "Photo slideshow", 0, 5, "4SS+5", "Family"),
                T(10, "Reunion day", 0, 0, "7, 8, 9FS+3"),
            ],
        }.Schedule(),
    ];

    public static IEnumerable<ProjectTask> AllTasks => Projects.SelectMany(p => p.Tasks);

    public static IEnumerable<ProjectTask> AllLeaves => Projects.SelectMany(p => p.Leaves);
}
