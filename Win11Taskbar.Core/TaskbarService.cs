using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using Win11Taskbar.Native;
using N = Win11Taskbar.Native.NativeMethods;

namespace Win11Taskbar.Core;

public sealed class TaskbarService : IDisposable
{
    private const uint ObjectCreate = 0x8000;
    private const uint ObjectReorder = 0x8004;
    private const uint LocationChange = 0x800B;
    private const uint KeepZOrderAndFocus = 0x14;
    private const uint KeepSize = 0x01;
    private static readonly TimeSpan SearchRepairCooldown = TimeSpan.FromMilliseconds(250);

    private readonly Settings settings;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<nint, WindowSnapshot> snapshots = new();
    private readonly Dictionary<nint, Appearance> appearances = new();
    private readonly Dictionary<nint, N.Rect> observedBars = new();
    private readonly Dictionary<nint, Placement> expectedPlacements = new();
    private readonly Dictionary<nint, Motion> animations = new();
    private readonly Dictionary<nint, int> measuredContent = new();
    private readonly Dictionary<nint, SearchPlacement> searchPlacements = new();
    private readonly HashSet<nint> watchedControls = new();
    private readonly List<nint> hooks = new();
    private readonly DispatcherTimer debounce;
    private readonly DispatcherTimer layoutCheck;
    private readonly DispatcherTimer frames;
    private readonly N.WinEventProc callback;
    private uint explorerPid;
    private bool applying;
    private bool disposed;

    public long EventCount { get; private set; }
    public long ApplyCount { get; private set; }
    public long FrameCount { get; private set; }
    public string RecoveryPath { get; }
    public event Action<string>? StatusChanged;

    public TaskbarService(Settings settings, Dispatcher dispatcher, string? recoveryPath = null)
    {
        this.settings = settings;
        this.dispatcher = dispatcher;
        RecoveryPath = recoveryPath ?? RecoveryJournal.DefaultPath;
        debounce = CreateTimer(dispatcher, 260, (_, _) =>
        {
            debounce!.Stop();
            Apply();
        });
        layoutCheck = CreateTimer(dispatcher, 2500, (_, _) => CheckLayout());
        frames = CreateTimer(dispatcher, 16, (_, _) => Animate());
        callback = OnEvent;
        // Attach after rooting the native callback delegate.
        AttachExplorer();
    }

    private static DispatcherTimer CreateTimer(Dispatcher dispatcher, int milliseconds, EventHandler tick)
    {
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background, tick, dispatcher);
        timer.Stop();
        return timer;
    }

    private void AttachExplorer()
    {
        var bar = N.Taskbars().FirstOrDefault();
        if (bar == 0)
            return;
        N.GetWindowThreadProcessId(bar, out var pid);
        if (pid == 0 || pid == explorerPid && hooks.Count > 0)
            return;

        foreach (var hook in hooks)
            N.UnhookWinEvent(hook);
        hooks.Clear();
        explorerPid = pid;
        AddHook(ObjectCreate, ObjectReorder);
        AddHook(LocationChange, LocationChange);
    }

    private void AddHook(uint first, uint last)
    {
        var hook = N.SetWinEventHook(first, last, 0, callback, explorerPid, 0, 2);
        if (hook != 0)
            hooks.Add(hook);
        else
            Log.Write("WARNING", "SetWinEventHook failed: " + Marshal.GetLastWin32Error());
    }

    private void OnEvent(nint hook, uint kind, nint handle, int obj, int child, uint thread, uint time)
    {
        EventCount++;
        if (disposed || applying || !settings.Enabled || handle == 0 || obj < -4)
            return;

        if (kind == LocationChange && observedBars.TryGetValue(handle, out var previousBar) &&
            N.GetWindowRect(handle, out var currentBar) &&
            (currentBar.Width != previousBar.Width || currentBar.Height != previousBar.Height))
        {
            // Never send another horizontal animation frame into a newly vertical Explorer layout.
            frames.Stop();
            animations.Clear();
        }

        if (kind == LocationChange && expectedPlacements.TryGetValue(handle, out var expected))
        {
            // SetWindowPos emits the same events as Explorer. Ignore our own changes, including
            // interpolated frames. A mismatch after an animation finishes is treated as Explorer layout.
            if (animations.ContainsKey(handle) || expected.Matches(handle))
                return;
            if (TryQueueSearchRepair(handle, expected))
                return;
        }
        else if (!watchedControls.Contains(handle))
        {
            var type = N.Class(handle);
            if (type is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "MSTaskListWClass"
                or "TrayDummySearchControl" or "TraySearchBox" or "SearchBox" or "SearchButton" or "TraySearchButton"))
                return;
        }
        Queue();
    }

    private bool TryQueueSearchRepair(nint handle, Placement expected)
    {
        if (!searchPlacements.TryGetValue(handle, out var tracked) ||
            !IsSearchPositionReset(handle, tracked.Parent, expected))
            return false;

        if (tracked.LastRepair != 0 && Stopwatch.GetElapsedTime(tracked.LastRepair) < SearchRepairCooldown)
            return false;

        // Explorer can briefly put Search back in its stock slot when Start opens.
        // Queue above the normal layout pass, outside the native callback.
        searchPlacements[handle] = tracked with { LastRepair = Stopwatch.GetTimestamp() };
        dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!disposed && !applying && settings.Enabled &&
                expectedPlacements.TryGetValue(handle, out var intended) && intended == expected &&
                !animations.ContainsKey(handle) && IsSearchPositionReset(handle, tracked.Parent, expected))
            {
                Position(handle, expected, keepSize: true);
                Log.Write("DEBUG", "Restored Search button position after Explorer reset.");
            }
        });
        return true;
    }

    private bool IsSearchPositionReset(nint handle, nint parent, Placement expected)
    {
        if (!settings.ShowSearch || !searchPlacements.TryGetValue(handle, out var tracked) ||
            tracked.Parent != parent || !snapshots.TryGetValue(handle, out var snapshot) || !snapshot.Valid ||
            N.GetParent(handle) != parent || !N.IsWindowVisible(handle) ||
            !appearances.TryGetValue(parent, out var appearance) || !appearance.HasCurrentSize(parent))
            return false;

        var current = N.RelativeRect(handle);
        return current.Left != expected.X && current.Top == expected.Y &&
            current.Width == expected.Width && current.Height == expected.Height;
    }

    public void Queue()
    {
        // Coalesce a burst of Explorer events without postponing updates indefinitely.
        if (!disposed && !debounce.IsEnabled)
            debounce.Start();
    }

    private void CheckLayout()
    {
        if (applying || disposed)
            return;

        // Geometry-only safety check for Explorer layouts whose WinEvents did not arrive.
        // UI Automation is invoked only when Apply is needed.
        if (observedBars.Count == 0 || observedBars.Any(entry => !N.GetWindowRect(entry.Key, out var current) ||
            current.Left != entry.Value.Left || current.Top != entry.Value.Top ||
            current.Width != entry.Value.Width || current.Height != entry.Value.Height) ||
            expectedPlacements.Any(entry => !animations.ContainsKey(entry.Key) && !entry.Value.Matches(entry.Key)))
            Queue();
    }

    private void Capture(nint handle, bool root = false)
    {
        if (handle == 0 || !N.IsWindow(handle))
            return;
        if (snapshots.TryGetValue(handle, out var previous) && previous.Valid)
            return;
        snapshots[handle] = WindowSnapshot.Capture(handle, root);
        Persist();
    }

    private void Persist() => RecoveryJournal.Save(RecoveryPath, snapshots.Values);

    public void Apply()
    {
        if (disposed)
            return;
        ApplyCount++;
        if (!settings.Enabled)
        {
            Restore();
            StatusChanged?.Invoke("Оформление отключено · исходная панель восстановлена");
            return;
        }
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            Restore();
            StatusChanged?.Invoke("Windows 11 · доступно превью");
            return;
        }

        applying = true;
        try
        {
            var bars = N.Taskbars();
            if (bars.Count == 0)
            {
                observedBars.Clear();
                layoutCheck.Start();
                StatusChanged?.Invoke("Ожидание Explorer…");
                return;
            }
            AttachExplorer();
            PruneInvalidWindows();

            var material = TaskbarMaterial.Resolve(settings);
            int changedBars = 0;
            bool refreshing = false;
            observedBars.Clear();
            foreach (var bar in bars)
            {
                if (!N.GetWindowRect(bar, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0)
                    continue;
                observedBars[bar] = bounds;

                bool horizontal = bounds.Width >= bounds.Height;
                if (!horizontal || (!settings.AllMonitors && N.Class(bar) != "Shell_TrayWnd"))
                {
                    RestoreBar(bar);
                    continue;
                }

                if (snapshots.TryGetValue(bar, out var captured) && !captured.MatchesCurrentBarSize())
                {
                    RestoreBar(bar);
                    // Let Explorer lay out the new dimensions before recording another baseline.
                    refreshing = true;
                    Queue();
                    continue;
                }

                Capture(bar, true);
                var controls = TaskbarControls.Discover(bar);
                var appearance = Appearance.ForWindow(bar, bounds, material);
                ApplyControls(bar, bounds, controls, appearance.SideInset);

                if (!appearances.TryGetValue(bar, out var previous) || appearance != previous)
                {
                    ApplyAppearance(bar, appearance);
                    appearances[bar] = appearance;
                }
                changedBars++;
            }

            layoutCheck.Start();
            StatusChanged?.Invoke(changedBars > 0
                ? $"Оформление активно · панелей: {changedBars} · Windows 10"
                : refreshing ? "Обновляем расположение панели задач…"
                : "Вертикальная панель · оформление Windows");
        }
        catch (Exception exception)
        {
            Log.Write("ERROR", "Apply: " + exception);
            Restore();
            StatusChanged?.Invoke("Не удалось применить · исходная панель восстановлена");
        }
        finally
        {
            applying = false;
        }
    }

    private void PruneInvalidWindows()
    {
        var invalid = snapshots.Where(item => !item.Value.Valid).Select(item => item.Key).ToArray();
        foreach (var handle in invalid)
        {
            snapshots.Remove(handle);
            appearances.Remove(handle);
            expectedPlacements.Remove(handle);
            animations.Remove(handle);
            measuredContent.Remove(handle);
            searchPlacements.Remove(handle);
        }
        watchedControls.RemoveWhere(handle => !N.IsWindow(handle));
        foreach (var handle in measuredContent.Keys.Where(handle => !N.IsWindow(handle)).ToArray())
            measuredContent.Remove(handle);
    }

    private void ApplyControls(nint bar, N.Rect bounds, TaskbarControls controls, int sideInset)
    {
        foreach (var handle in new[] { controls.Rebar, controls.Start, controls.SearchIcon,
            controls.SearchBox, controls.TaskView, controls.Tray, controls.Clock })
        {
            if (handle != 0)
                watchedControls.Add(handle);
        }

        foreach (var handle in new[] { controls.Rebar, controls.Start, controls.TaskView, controls.Tray, controls.Clock })
            Capture(handle);

        nint search = ConfigureSearch(controls);
        foreach (nint inactiveSearch in new[] { controls.SearchIcon, controls.SearchBox }.Where(handle => handle != search))
            searchPlacements.Remove(inactiveSearch);
        Show(controls.Start, settings.ShowStart);
        Show(controls.TaskView, settings.ShowTaskView);
        Show(controls.Tray, settings.ShowTray);
        Show(controls.Clock, settings.ShowClock && settings.ShowTray);

        PlaceTray(bar, bounds.Width, controls.Tray, sideInset);
        if (controls.TaskList != 0 && controls.Rebar != 0)
            Align(bounds, controls, search, sideInset);
        else
            Log.Write("DEBUG", "Native task list unavailable on " + bar);
    }

    private nint ConfigureSearch(TaskbarControls controls)
    {
        bool hasBox = HasSize(controls.SearchBox);
        bool hasIcon = HasSize(controls.SearchIcon);
        nint search = 0;

        // Explorer owns the selected presentation. Prefer its visible nonempty control, then
        // remember the mode we hid, and use the compact icon when both are idle.
        if (hasBox && N.IsWindowVisible(controls.SearchBox))
            search = controls.SearchBox;
        else if (hasIcon && N.IsWindowVisible(controls.SearchIcon))
            search = controls.SearchIcon;
        else if (hasBox && WasVisible(controls.SearchBox))
            search = controls.SearchBox;
        else if (hasIcon)
            search = controls.SearchIcon;
        else if (hasBox)
            search = controls.SearchBox;

        if (search != 0)
        {
            Capture(search);
            Show(search, settings.ShowSearch);
        }
        return settings.ShowSearch ? search : 0;
    }

    private bool WasVisible(nint handle) =>
        snapshots.TryGetValue(handle, out var snapshot) && snapshot.Visible;

    private static bool HasSize(nint handle) =>
        handle != 0 && N.GetWindowRect(handle, out var bounds) && bounds.Width > 0 && bounds.Height > 0;

    private void Show(nint handle, bool visible)
    {
        if (handle == 0 || N.IsWindowVisible(handle) == visible)
            return;
        // Showing a child should never capture focus from the active application.
        N.ShowWindow(handle, visible ? 8 : 0);
        if (!visible)
        {
            expectedPlacements.Remove(handle);
            animations.Remove(handle);
        }
    }

    private void PlaceTray(nint bar, int barWidth, nint tray, int sideInset)
    {
        if (tray == 0 || !N.IsWindowVisible(tray) || N.GetParent(tray) != bar)
            return;
        var position = N.RelativeRect(tray);
        if (position.Width <= 0 || position.Width > barWidth - sideInset)
            return;

        // The Show Desktop strip sits at the final pixels of the tray. Moving its parent
        // inside the floating outline keeps all these pixels reachable.
        int target = barWidth - position.Width - sideInset;
        Move(tray, new Placement(target, position.Top, position.Width, position.Height), animate: false);
    }

    private void Align(N.Rect bounds, TaskbarControls controls, nint search, int sideInset)
    {
        if (!N.GetWindowRect(controls.TaskList, out var listBounds) ||
            !N.GetWindowRect(controls.Rebar, out var rebarBounds))
            return;

        int? measured = MeasureButtons(controls.TaskList, listBounds);
        if (measured.HasValue)
            measuredContent[controls.TaskList] = measured.Value;
        if (!measuredContent.TryGetValue(controls.TaskList, out int content))
            return;
        var prefix = new[] { controls.Start, search, controls.TaskView }
            .Where(handle => handle != 0 && N.IsWindowVisible(handle))
            .Distinct()
            .Select(handle => (Handle: handle, Rect: N.RelativeRect(handle)))
            .Where(item => item.Rect.Width > 0)
            .ToArray();
        foreach (var hidden in new[] { controls.Start, controls.SearchIcon, controls.SearchBox, controls.TaskView }
            .Except(prefix.Select(item => item.Handle)))
        {
            expectedPlacements.Remove(hidden);
            animations.Remove(hidden);
        }
        if (content == 0 && prefix.Length == 0)
            return;

        int left = Math.Max(8, sideInset + 6);
        int limit = bounds.Width - left;
        if (controls.Tray != 0 && N.IsWindowVisible(controls.Tray) &&
            N.GetWindowRect(controls.Tray, out var trayBounds))
            limit = Math.Min(limit, trayBounds.Left - bounds.Left - 8);
        int total = content + prefix.Sum(item => item.Rect.Width);
        int start = settings.Alignment switch
        {
            "Left" => left,
            "Dynamic Center" => left + (limit - left - total) / 2,
            _ => (bounds.Width - total) / 2
        };
        int x = Math.Clamp(start, left, Math.Max(left, limit - total));

        foreach (var (handle, rect) in prefix)
        {
            if (handle == search)
            {
                nint parent = N.GetParent(handle);
                if (!searchPlacements.TryGetValue(handle, out var tracked) || tracked.Parent != parent)
                    searchPlacements[handle] = new SearchPlacement(parent, 0);
            }
            Move(handle, new Placement(x, rect.Top, rect.Width, rect.Height));
            x += rect.Width;
        }

        int nestedListInset = Math.Max(0, listBounds.Left - rebarBounds.Left);
        var rebar = N.RelativeRect(controls.Rebar);
        int rebarX = x - nestedListInset;
        int rebarWidth = limit - rebarX;
        if (rebarWidth > 0)
            Move(controls.Rebar, new Placement(rebarX, rebar.Top, rebarWidth, rebar.Height));
        Log.Write("DEBUG", $"Alignment: content={content}, prefix={prefix.Length}, rebar={rebarX}, limit={limit}");
    }

    private static int? MeasureButtons(nint list, N.Rect listBounds)
    {
        int content = 0;
        try
        {
            var root = AutomationElement.FromHandle(list);
            var buttons = root.FindAll(TreeScope.Children, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
            foreach (AutomationElement button in buttons)
            {
                var bounds = button.Current.BoundingRectangle;
                if (!bounds.IsEmpty && bounds.Width > 0)
                    content = Math.Max(content, (int)(bounds.Right - listBounds.Left));
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            Log.Write("DEBUG", "UI Automation: " + exception.Message);
            return null;
        }
        return content;
    }

    private void ApplyAppearance(nint bar, Appearance appearance)
    {
        int right = appearance.Width - appearance.SideInset;
        int bottom = appearance.Height - appearance.VerticalInset;
        int diameter = appearance.Radius * 2;
        nint region = diameter > 0
            ? N.CreateRoundRectRgn(appearance.SideInset, appearance.VerticalInset, right, bottom, diameter, diameter)
            : N.CreateRectRgn(appearance.SideInset, appearance.VerticalInset, right, bottom);
        if (region != 0 && N.SetWindowRgn(bar, region, true) == 0)
        {
            N.DeleteObject(region);
            Log.Write("WARNING", "SetWindowRgn failed: " + Marshal.GetLastWin32Error());
        }

        var material = appearance.Material;
        if (!material.NativeTintEnabled)
        {
            N.Accent(bar, 0, 0);
            return;
        }
        if (material.Frosted && N.Accent(bar, 4, material.NativeTint))
            return;
        if (!N.Accent(bar, 2, material.NativeTint))
            Log.Write("WARNING", "Taskbar tint unavailable; keeping system background.");
    }

    private void Move(nint handle, Placement target, bool animate = true)
    {
        var current = N.RelativeRect(handle);
        bool sameTarget = expectedPlacements.TryGetValue(handle, out var oldTarget) && oldTarget == target;
        bool repairingSearch = sameTarget && !animations.ContainsKey(handle) &&
            searchPlacements.TryGetValue(handle, out var search) && IsSearchPositionReset(handle, search.Parent, target);
        bool animated = animate && settings.AnimationEnabled && settings.AnimateRepositioning &&
            settings.AnimationDuration > 0 && current.Left != target.X && !repairingSearch;
        if (animated && sameTarget && animations.ContainsKey(handle))
            return;

        expectedPlacements[handle] = target;
        if (target.Matches(current))
        {
            animations.Remove(handle);
            return;
        }
        if (!animated)
        {
            animations.Remove(handle);
            Position(handle, target, current.Width == target.Width && current.Height == target.Height);
            return;
        }

        animations[handle] = new Motion(current.Left, target, Stopwatch.GetTimestamp());
        if (!frames.IsEnabled)
            frames.Start();
    }

    private void Animate()
    {
        FrameCount++;
        foreach (var (handle, motion) in animations.ToArray())
        {
            double t = Math.Clamp(Stopwatch.GetElapsedTime(motion.Started).TotalMilliseconds /
                Math.Max(1, settings.AnimationDuration), 0, 1);
            double eased = t >= 1 ? 1 : settings.AnimationType switch
            {
                "Linear" => t,
                "Ease" => t * t * (3 - 2 * t),
                "Spring" => 1 - Math.Exp(-7 * t) * Math.Cos(t * 10),
                _ => 1 - Math.Pow(1 - t, 3)
            };
            int x = (int)Math.Round(motion.From + (motion.Target.X - motion.From) * eased);
            var frame = motion.Target with { X = x };
            var current = N.RelativeRect(handle);
            if (!frame.Matches(current))
                Position(handle, frame, current.Width == frame.Width && current.Height == frame.Height);
            if (t >= 1)
                animations.Remove(handle);
        }
        if (animations.Count == 0)
            frames.Stop();
    }

    private static void Position(nint handle, Placement placement, bool keepSize)
    {
        uint flags = KeepZOrderAndFocus | (keepSize ? KeepSize : 0);
        if (N.IsWindow(handle) && !N.SetWindowPos(handle, 0, placement.X, placement.Y, placement.Width, placement.Height, flags))
            Log.Write("WARNING", "SetWindowPos: " + Marshal.GetLastWin32Error());
    }

    private void RestoreBar(nint bar)
    {
        if (!snapshots.ContainsKey(bar))
            return;
        var handles = N.Children(bar).Append(bar).ToHashSet();
        var affected = snapshots.Values.Where(item => handles.Contains((nint)item.Handle)).ToArray();
        WindowSnapshot.RestoreAll(affected);
        foreach (var snapshot in affected)
        {
            nint handle = (nint)snapshot.Handle;
            animations.Remove(handle);
            expectedPlacements.Remove(handle);
            watchedControls.Remove(handle);
            searchPlacements.Remove(handle);
            snapshots.Remove(handle);
        }
        appearances.Remove(bar);
        foreach (var handle in handles)
            measuredContent.Remove(handle);
        Persist();
    }

    public void Restore()
    {
        frames.Stop();
        layoutCheck.Stop();
        animations.Clear();
        appearances.Clear();
        observedBars.Clear();
        expectedPlacements.Clear();
        measuredContent.Clear();
        searchPlacements.Clear();
        watchedControls.Clear();
        bool hadSnapshots = snapshots.Count > 0;
        try
        {
            WindowSnapshot.RestoreAll(snapshots.Values);
        }
        catch (Exception exception) { Log.Write("WARNING", "Restore: " + exception.Message); }
        snapshots.Clear();
        if (hadSnapshots)
        {
            try { Persist(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Write("WARNING", "Recovery journal: " + exception.Message);
            }
        }
    }

    public string MonitorSummary() => string.Join("\n", N.Taskbars().Select(handle =>
    {
        var details = new N.MonitorInfo { Size = Marshal.SizeOf<N.MonitorInfo>(), Device = "" };
        N.GetMonitorInfo(N.MonitorFromWindow(handle, 2), ref details);
        return $"{details.Device} · {details.Monitor.Width} × {details.Monitor.Height} · DPI {N.GetDpiForWindow(handle)} · {(details.Flags == 1 ? "основной" : "дополнительный")}";
    }));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        debounce.Stop();
        foreach (var hook in hooks)
            N.UnhookWinEvent(hook);
        Restore();
    }

    private readonly record struct Placement(int X, int Y, int Width, int Height)
    {
        internal bool Matches(N.Rect rect) =>
            rect.Left == X && rect.Top == Y && rect.Width == Width && rect.Height == Height;

        internal bool Matches(nint window) => Matches(N.RelativeRect(window));
    }

    private readonly record struct Motion(int From, Placement Target, long Started);
    private readonly record struct SearchPlacement(nint Parent, long LastRepair);

    private readonly record struct Appearance(int Width, int Height, int SideInset, int VerticalInset, int Radius, TaskbarMaterial Material)
    {
        internal static Appearance ForWindow(nint bar, N.Rect bounds, TaskbarMaterial material)
        {
            double scale = Math.Max(1, N.GetDpiForWindow(bar) / 96.0);
            int sideInset = Math.Min((int)Math.Round(material.SideInset * scale), bounds.Width / 8);
            int verticalInset = Math.Min((int)material.VerticalInset, bounds.Height / 8);
            int maxRadius = Math.Max(0, Math.Min(bounds.Width - 2 * sideInset, bounds.Height - 2 * verticalInset) / 2);
            int radius = Math.Min((int)Math.Round(material.Radius * scale), maxRadius);
            return new Appearance(bounds.Width, bounds.Height, sideInset, verticalInset, radius, material);
        }

        internal bool HasCurrentSize(nint window) => N.GetWindowRect(window, out var rect) &&
            rect.Width == Width && rect.Height == Height;
    }
}

