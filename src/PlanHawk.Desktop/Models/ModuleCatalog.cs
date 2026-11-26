namespace PlanHawk.Models;

/// <summary>
/// Every section of PlanHawk. Home, Gantt chart, Tasks and Dependencies have their own preview pages; everything else
/// uses the generic ComingSoonView built from this data. Add a module here and it appears in the sidebar automatically.
/// </summary>
public static class ModuleCatalog
{
    public static readonly ModuleInfo Home = new()
    {
        Key = "home", Title = "Home", Glyph = Ic.Home, IsComingSoon = false,
        Tagline = "Your projects, upcoming milestones and this week's work.",
    };

    public static readonly ModuleInfo Gantt = new()
    {
        Key = "gantt", Title = "Gantt chart", Glyph = Ic.Gantt, IsComingSoon = false,
        Tagline = "The task sheet and the timeline side by side.",
    };

    public static readonly ModuleInfo Tasks = new()
    {
        Key = "tasks", Title = "Tasks", Glyph = Ic.Task, IsComingSoon = false,
        Tagline = "Every task in every project, with progress and status.",
    };

    public static readonly ModuleInfo Dependencies = new()
    {
        Key = "dependencies", Title = "Dependencies", Glyph = Ic.Routing, IsComingSoon = false,
        Tagline = "How tasks link together, as a network diagram.",
    };

    public static readonly IReadOnlyList<ModuleInfo> Sidebar =
    [
        Home,
        Gantt,
        Tasks,
        Dependencies,
        new()
        {
            Key = "timeline", Title = "Timeline", Glyph = Ic.Timeline,
            Tagline = "A one-line summary of the project to share with others.",
            Description = "Pick the tasks and milestones that matter and PlanHawk draws them on a clean timeline above the Gantt chart. Copy it into an email, a WriteHawk document or a SlideHawk presentation.",
            PrimaryAction = "Add to timeline",
            Kpis = ["Tasks on timeline", "Milestones", "Callouts"],
            Columns = ["Task", "Start", "Finish", "Shown as"],
            Features =
            [
                "Add any task or milestone to the timeline",
                "Bars or callouts",
                "Several timelines per project",
                "Copy as a picture",
                "Export to PDF",
                "Custom date range",
            ],
        },
        new()
        {
            Key = "board", Title = "Board", Glyph = Ic.Board,
            Tagline = "Move tasks across columns like a kanban board.",
            Description = "See the project's tasks as cards in columns such as Not started, In progress and Done, and drag them along as work moves. The same tasks as the Gantt chart, just another view.",
            PrimaryAction = "New card",
            Kpis = ["Not started", "In progress", "Done", "Blocked"],
            Columns = ["Card", "Column", "Assigned to", "Due"],
            Features =
            [
                "Columns by status or by your own buckets",
                "Drag cards between columns",
                "Cards show assignees, dates and progress",
                "Sprints for agile projects",
                "Swimlanes by resource",
                "Sync with Boardwalk boards",
            ],
        },
        new()
        {
            Key = "calendar", Title = "Calendar", Glyph = Ic.Calendar,
            Tagline = "Tasks on a month, week or day calendar.",
            Description = "Lay the schedule over a calendar to see what happens when. Set working days, hours and holidays for the project and for each person.",
            PrimaryAction = "Change working time",
            Kpis = ["Working days", "Holidays", "Exceptions"],
            Columns = ["Date", "Task", "Project", "Resource"],
            Features =
            [
                "Month, week and day views",
                "Project calendar (working days and hours)",
                "Holidays and exceptions",
                "A calendar for each resource",
                "Task calendars",
                "Show several projects at once",
            ],
        },
        new()
        {
            Key = "resources", Title = "Resources", Glyph = Ic.Resources,
            Tagline = "People, equipment and materials, and how busy they are.",
            Description = "List the people and things a project needs, assign them to tasks and see who is overbooked. Level the schedule automatically so nobody has two jobs on the same day.",
            PrimaryAction = "Add resource",
            Kpis = ["People", "Overallocated", "Equipment", "Materials"],
            Columns = ["Resource", "Type", "Max units", "Rate", "Assigned work"],
            Features =
            [
                "Work, material and cost resources",
                "Assign several resources to a task",
                "Resource usage and Team Planner views",
                "Find and fix overallocations",
                "Automatic resource leveling",
                "Standard and overtime rates",
                "Shared resource pool across projects",
                "Resource calendars and vacations",
            ],
        },
        new()
        {
            Key = "reports", Title = "Reports", Glyph = Ic.Report,
            Tagline = "Progress, costs, burndown and baselines at a glance.",
            Description = "Ready-made dashboards for project overview, critical tasks, late tasks, costs and burndown. Save a baseline and compare the plan with what really happened.",
            PrimaryAction = "New report",
            Kpis = ["% complete", "Late tasks", "Cost", "Variance"],
            Columns = ["Report", "Type", "Last opened"],
            Features =
            [
                "Project overview dashboard",
                "Critical and late tasks",
                "Burndown and earned value",
                "Costs and budget",
                "Baselines and variance",
                "Print and export to PDF",
                "Copy charts into GridHawk",
            ],
        },
        new()
        {
            Key = "templates", Title = "Templates", Glyph = Ic.Template,
            Tagline = "Start new projects from ready-made plans.",
            Description = "Templates for software releases, home renovations, events, moves and more, or save any project as your own template with its tasks, links and resources.",
            PrimaryAction = "New template",
            Kpis = ["Templates", "My templates", "Used this month"],
            Columns = ["Template", "Category", "Tasks", "Last used"],
            Features =
            [
                "Built-in templates",
                "Save a project as a template",
                "Template gallery with previews",
                "Keep links, durations and resources",
            ],
        },
    ];

    public static readonly ModuleInfo Settings = new()
    {
        Key = "settings", Title = "Settings", Glyph = Ic.Settings,
        Tagline = "Appearance, calendars, scheduling options and import.",
        Description = "Choose light or dark mode, set your default working days and hours, decide how new tasks are scheduled, and open files from Microsoft Project, Excel and other planning tools.",
        PrimaryAction = "Save",
        Columns = ["Setting", "Value"],
        Features =
        [
            "Light, dark or match Windows",
            "Default working days and hours",
            "Auto or manual scheduling for new tasks",
            "Open Microsoft Project files (.mpp, .xml)",
            "Import and export Excel and CSV",
            "PlanHawk Server sync and sharing (self-hosted)",
            "Date and currency formats",
            "Keyboard shortcuts",
        ],
    };

    public static IEnumerable<ModuleInfo> All => Sidebar.Append(Settings);

    public static ModuleInfo? Find(string key) => All.FirstOrDefault(m => m.Key == key);
}
