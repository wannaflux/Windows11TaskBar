using N = Win11Taskbar.Native.NativeMethods;

namespace Win11Taskbar.Core;

internal readonly record struct TaskbarControls(
    nint TaskList, nint Rebar, nint Start, nint SearchIcon,
    nint SearchBox, nint TaskView, nint Tray, nint Clock)
{
    internal static TaskbarControls Discover(nint bar)
    {
        var children = N.Children(bar);
        var direct = children.Where(handle => N.GetParent(handle) == bar).ToArray();
        nint taskList = Find(children, "MSTaskListWClass");
        nint rebar = Find(children, "ReBarWindow32");
        if (rebar == 0 && taskList != 0)
            rebar = N.GetParent(taskList);

        nint searchBox = Find(direct, "TrayDummySearchControl", "TraySearchBox", "SearchBox");
        nint searchIcon = Find(direct, "SearchButton", "TraySearchButton");
        nint taskView = Find(direct, "TaskViewButton", "TrayTaskViewButton");

        // On Windows 10 22H2 the compact Search button can use the generic TrayButton class.
        // The hidden search-box button retains its translated title even in compact mode.
        nint boxButton = searchBox == 0 ? 0 : Find(N.Children(searchBox), "Button");
        string boxTitle = boxButton == 0 ? "" : N.Title(boxButton);
        foreach (var generic in direct.Where(handle => N.Class(handle) == "TrayButton"))
        {
            string label = N.Title(generic).Trim();
            if (searchIcon == 0 && IsSearchLabel(label, boxTitle))
                searchIcon = generic;
            else if (taskView == 0 && label is "Task View" or "Представление задач")
                taskView = generic;
        }

        return new TaskbarControls(taskList, rebar, Find(direct, "Start"), searchIcon,
            searchBox, taskView, Find(children, "TrayNotifyWnd"), Find(children, "TrayClockWClass", "ClockButton"));
    }

    private static bool IsSearchLabel(string label, string boxTitle)
    {
        if (label is "Search" or "Поиск")
            return true;
        return label.Length > 1 && (boxTitle.Equals(label, StringComparison.OrdinalIgnoreCase)
            || boxTitle.StartsWith(label + " - ", StringComparison.OrdinalIgnoreCase)
            || boxTitle.StartsWith(label + " – ", StringComparison.OrdinalIgnoreCase));
    }

    private static nint Find(IEnumerable<nint> handles, params string[] classes) =>
        handles.FirstOrDefault(handle => classes.Contains(N.Class(handle)));
}
