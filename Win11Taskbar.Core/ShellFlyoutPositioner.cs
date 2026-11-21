using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using N = Win11Taskbar.Native.NativeMethods;

namespace Win11Taskbar.Core;

/// <summary>Repositions the Windows 10 Start and Search windows without changing their contents.</summary>
public sealed class ShellFlyoutPositioner : IDisposable
{
    private const uint ForegroundChanged = 0x0003;
    private const uint ObjectShow = 0x8002;
    private const uint ObjectHide = 0x8003;
    private const uint ObjectCloaked = 0x8017;
    private const uint ObjectUncloaked = 0x8018;
    private const uint OutOfContextSkipOwnProcess = 0x0002;
    private const uint RootAncestor = 2;
    private const uint NearestMonitor = 2;
    private const uint PrimaryMonitor = 1;
    private const int DwmCloaked = 14;
    // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS.
    private const uint MoveHorizontally = 0x4215;
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan MoveCooldown = TimeSpan.FromMilliseconds(170);
    private const int MaximumMovesPerOpen = 3;
    private const string ShellWindowClass = "Windows.UI.Core.CoreWindow";

    private static readonly string SystemApps = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
    private static readonly string StartPath = Path.Combine(SystemApps,
        "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy", "StartMenuExperienceHost.exe");
    private static readonly string SearchPath = Path.Combine(SystemApps,
        "Microsoft.Windows.Search_cw5n1h2txyewy", "SearchApp.exe");

    private readonly Settings settings;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer settle;
    private readonly N.WinEventProc eventCallback;
    private readonly Dictionary<Flyout, Placement> placements = new();
    private readonly Dictionary<Flyout, Opening> openings = new();
    private readonly Dictionary<uint, nint[]> shellHooks = new();
    private readonly HashSet<(uint Pid, string Class, bool TopLevel)> observedUnsupported = new();
    private nint foregroundHook;
    private bool disposed;

    public ShellFlyoutPositioner(Settings settings, Dispatcher dispatcher)
    {
        this.settings = settings;
        this.dispatcher = dispatcher;
        settle = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(55)
        };
        settle.Tick += OnSettle;
        eventCallback = OnWindowEvent;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ||
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        settings.PropertyChanged += OnSettingsChanged;
        Refresh();
    }

    private bool ShouldPosition => settings.Enabled && settings.Alignment is "Center" or "Dynamic Center";

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(Settings.Enabled) or nameof(Settings.Alignment) or nameof(Settings.AllMonitors)))
            return;

        if (dispatcher.CheckAccess()) Refresh();
        else dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        if (disposed) return;

        if (!ShouldPosition)
        {
            StopWatching();
            return;
        }

        if (foregroundHook == 0)
        {
            foregroundHook = Hook(ForegroundChanged);
            if (foregroundHook == 0)
                Log.Write("DEBUG", "Windows flyout observation is unavailable.");
        }

        RebindShellHooks();
        foreach (var item in openings.Keys.ToArray())
        {
            if (!IsSameWindow(item) || !IsVisible(item.Handle) || !TryFindWorkArea(item.Handle, out _))
                Restore(item);
        }
        TrackForeground();
    }

    private nint Hook(uint kind, uint pid = 0) =>
        N.SetWinEventHook(kind, kind, 0, eventCallback, pid, 0, OutOfContextSkipOwnProcess);

    private void RebindShellHooks()
    {
        var running = new HashSet<uint>();
        foreach (var (name, path) in new[] { ("StartMenuExperienceHost", StartPath), ("SearchApp", SearchPath) })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (!string.Equals(TryGetPath(process), path, StringComparison.OrdinalIgnoreCase)) continue;
                    uint pid = (uint)process.Id;
                    running.Add(pid);
                    EnsureShellHooks(pid);
                }
            }
        }

        foreach (uint pid in shellHooks.Keys.Where(pid => !running.Contains(pid)).ToArray())
            RemoveShellHooks(pid);
    }

    private void EnsureShellHooks(uint pid)
    {
        if (!shellHooks.ContainsKey(pid))
            shellHooks[pid] = [Hook(ObjectShow, pid), Hook(ObjectHide, pid), Hook(ObjectUncloaked, pid), Hook(ObjectCloaked, pid)];
    }

    private void RemoveShellHooks(uint pid)
    {
        if (!shellHooks.Remove(pid, out var handles)) return;
        foreach (nint handle in handles)
        {
            if (handle != 0) N.UnhookWinEvent(handle);
        }
    }

    private void OnWindowEvent(nint hook, uint kind, nint handle, int objectId, int childId, uint thread, uint time)
    {
        if (disposed || handle == 0) return;

        if (kind == ForegroundChanged)
        {
            // Only inspect foreground window metadata. The dispatcher runs native moves
            // outside the WinEvent callback, avoiding reentrancy through the shell.
            if (openings.Count != 0 || N.Class(GetAncestor(handle, RootAncestor)) == ShellWindowClass)
                dispatcher.BeginInvoke(TrackForeground);
            return;
        }
        if (objectId != 0 || childId != 0) return;

        if (kind is ObjectShow or ObjectUncloaked)
        {
            dispatcher.BeginInvoke(() => TrackShown(handle));
        }
        else if (openings.Keys.Any(item => item.Handle == handle))
        {
            dispatcher.BeginInvoke(() => RestoreHidden(handle));
        }
    }

    private void TrackShown(nint handle)
    {
        if (disposed || !ShouldPosition) return;
        if (GetAncestor(handle, RootAncestor) == handle && TryIdentify(handle, out var flyout))
            Schedule(flyout, needsForeground: false);
        else
            LogUnsupportedCandidate(handle);
    }

    private void TrackForeground()
    {
        if (disposed || !ShouldPosition) return;

        foreach (var item in openings.Keys.ToArray())
        {
            // A newly observed flyout can still be cloaked during the first frames.
            if (!IsSameWindow(item) || (openings[item].WasVisible && !IsVisible(item.Handle)))
                Restore(item);
        }

        var handle = GetAncestor(GetForegroundWindow(), RootAncestor);
        if (!TryIdentify(handle, out var flyout)) return;

        if (!shellHooks.ContainsKey(flyout.ProcessId)) RebindShellHooks();
        Schedule(flyout, needsForeground: true);
    }

    private void Schedule(Flyout flyout, bool needsForeground)
    {
        if (openings.TryGetValue(flyout, out var existing))
        {
            existing.NeedsForeground &= needsForeground;
            // Foreground events may repeat as the search box gains keyboard focus.
            // Only a new visibility event can start another correction window.
            if (needsForeground || Stopwatch.GetElapsedTime(existing.Started) < SettleWindow) return;
        }

        openings[flyout] = new Opening(needsForeground);
        Log.Write("DEBUG", $"Shell flyout observed: {flyout.Name}, {ShellWindowClass} ({(needsForeground ? "foreground" : "visible")}).");
        settle.Start();
    }

    private void OnSettle(object? sender, EventArgs args)
    {
        if (!ShouldPosition)
        {
            settle.Stop();
            return;
        }

        bool checking = false;
        foreach (var (flyout, opening) in openings.ToArray())
        {
            bool expired = Stopwatch.GetElapsedTime(opening.Started) >= SettleWindow;
            if (!IsSameWindow(flyout) || ((expired || opening.WasVisible) && !IsVisible(flyout.Handle)))
            {
                Restore(flyout);
                continue;
            }
            if (expired || opening.Moves >= MaximumMovesPerOpen) continue;

            checking = true;
            if (!opening.NeedsForeground || GetAncestor(GetForegroundWindow(), RootAncestor) == flyout.Handle)
                TryPosition(flyout, opening);
        }
        if (!checking) settle.Stop();
    }

    private void TryPosition(Flyout flyout, Opening opening)
    {
        if (!IsVisible(flyout.Handle) || !N.GetWindowRect(flyout.Handle, out var popup))
            return;
        opening.WasVisible = true;

        if (!TryFindWorkArea(flyout.Handle, out var workArea))
        {
            LogGeometry(flyout, opening, popup, "no customized horizontal taskbar on this monitor");
            return;
        }

        nint monitor = N.MonitorFromWindow(flyout.Handle, NearestMonitor);
        if (opening.Monitor != 0 && monitor != opening.Monitor)
        {
            LogGeometry(flyout, opening, popup, "Windows moved the flyout to another monitor");
            return;
        }

        if (popup.Height < 160 || popup.Width < 200 || CenteredLeft(popup.Width, workArea) is not int desired ||
            popup.Top < workArea.Top - 16 || popup.Bottom > workArea.Bottom + 16)
        {
            LogGeometry(flyout, opening, popup, "not a bounded flyout rectangle");
            return;
        }

        // Ignore transitional XAML rectangles. Correct only after two matching
        // samples, including after Windows recomputes a visible popup's placement.
        if (opening.LastRect is not { } previous || !SameRectangle(previous, popup))
        {
            opening.LastRect = popup;
            return;
        }
        opening.Monitor = monitor;
        if (Math.Abs(popup.Left - desired) <= 1 ||
            (opening.LastMove != 0 && Stopwatch.GetElapsedTime(opening.LastMove) < MoveCooldown))
            return;

        opening.LastMove = Stopwatch.GetTimestamp();
        if (!N.SetWindowPos(flyout.Handle, 0, desired, popup.Top, 0, 0, MoveHorizontally))
        {
            Log.Write("DEBUG", $"Windows flyout repositioning unavailable ({Marshal.GetLastWin32Error()}).");
            opening.Moves = MaximumMovesPerOpen;
            return;
        }

        opening.Moves++;
        placements[flyout] = new Placement(popup.Left, desired);
        Log.Write("DEBUG", $"Centered Windows {flyout.Name} on X={desired} (was {popup.Left}).");
    }

    private static bool SameRectangle(N.Rect x, N.Rect y) =>
        x.Left == y.Left && x.Top == y.Top && x.Right == y.Right && x.Bottom == y.Bottom;

    private static void LogGeometry(Flyout flyout, Opening opening, N.Rect popup, string reason)
    {
        if (!Log.DebugEnabled || !opening.ReportedReasons.Add(reason)) return;
        Log.Write("DEBUG", $"Shell flyout skipped: {flyout.Name}, {ShellWindowClass}, " +
            $"rect {popup.Left},{popup.Top} {popup.Width}×{popup.Height}: {reason}.");
    }

    private void LogUnsupportedCandidate(nint handle)
    {
        if (!Log.DebugEnabled || observedUnsupported.Count >= 32) return;
        N.GetWindowThreadProcessId(handle, out uint pid);
        if (!shellHooks.ContainsKey(pid)) return;
        string windowClass = N.Class(handle);
        if (windowClass.Length == 0) return;
        bool topLevel = GetAncestor(handle, RootAncestor) == handle;
        if (!observedUnsupported.Add((pid, windowClass, topLevel))) return;
        N.GetWindowRect(handle, out var rect);
        Log.Write("DEBUG", $"Shell flyout candidate skipped: {windowClass}, " +
            $"top-level={topLevel}, rect {rect.Left},{rect.Top} {rect.Width}×{rect.Height}.");
    }

    // A window wider than the work area keeps Windows' placement.
    internal static int? CenteredLeft(int width, N.Rect workArea)
    {
        int available = workArea.Width;
        if (width <= 0 || width >= available) return null;
        return workArea.Left + (available - width) / 2;
    }

    private bool TryFindWorkArea(nint handle, out N.Rect workArea)
    {
        workArea = default;
        nint monitor = N.MonitorFromWindow(handle, NearestMonitor);
        var info = new N.MonitorInfo { Size = Marshal.SizeOf<N.MonitorInfo>(), Device = "" };
        if (monitor == 0 || !N.GetMonitorInfo(monitor, ref info) ||
            (!settings.AllMonitors && (info.Flags & PrimaryMonitor) == 0))
            return false;

        bool hasHorizontalTaskbar = N.Taskbars().Any(bar => N.MonitorFromWindow(bar, NearestMonitor) == monitor &&
            N.GetWindowRect(bar, out var rect) && rect.Width > rect.Height && rect.Height > 0);
        if (!hasHorizontalTaskbar) return false;

        workArea = info.Work;
        return workArea.Width > 0 && workArea.Height > 0;
    }

    private static bool TryIdentify(nint handle, out Flyout flyout)
    {
        flyout = default;
        if (handle == 0 || N.Class(handle) != ShellWindowClass) return false;
        N.GetWindowThreadProcessId(handle, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string? path = TryGetPath(process);
            if (string.Equals(path, StartPath, StringComparison.OrdinalIgnoreCase))
                flyout = new Flyout(handle, pid, "Start");
            else if (string.Equals(path, SearchPath, StringComparison.OrdinalIgnoreCase))
                flyout = new Flyout(handle, pid, "Search");
            return flyout.Handle != 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsSameWindow(Flyout flyout)
    {
        N.GetWindowThreadProcessId(flyout.Handle, out uint pid);
        return pid == flyout.ProcessId && N.Class(flyout.Handle) == ShellWindowClass &&
            GetAncestor(flyout.Handle, RootAncestor) == flyout.Handle;
    }

    private static bool IsVisible(nint hwnd) =>
        N.IsWindowVisible(hwnd) && DwmGetWindowAttribute(hwnd, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked == 0;

    private static string? TryGetPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return null; }
    }

    private void RestoreHidden(nint handle)
    {
        foreach (var item in openings.Keys.Where(item => item.Handle == handle).ToArray())
        {
            if (!IsSameWindow(item) || !IsVisible(item.Handle))
                Restore(item);
        }
    }

    private void Restore(Flyout flyout)
    {
        openings.Remove(flyout);
        if (!placements.Remove(flyout, out var placement)) return;
        if (IsSameWindow(flyout) && N.GetWindowRect(flyout.Handle, out var current) &&
            Math.Abs(current.Left - placement.MovedX) <= 1)
            N.SetWindowPos(flyout.Handle, 0, placement.OriginalX, current.Top, 0, 0, MoveHorizontally);
    }

    private void StopWatching()
    {
        settle.Stop();
        if (foregroundHook != 0) N.UnhookWinEvent(foregroundHook);
        foregroundHook = 0;
        foreach (var item in openings.Keys.ToArray()) Restore(item);
        foreach (uint pid in shellHooks.Keys.ToArray()) RemoveShellHooks(pid);
        observedUnsupported.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        settings.PropertyChanged -= OnSettingsChanged;
        StopWatching();
    }

    private readonly record struct Flyout(nint Handle, uint ProcessId, string Name);
    private readonly record struct Placement(int OriginalX, int MovedX);

    private sealed class Opening(bool needsForeground)
    {
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly HashSet<string> ReportedReasons = new(StringComparer.Ordinal);
        internal bool NeedsForeground = needsForeground;
        internal bool WasVisible;
        internal N.Rect? LastRect;
        internal nint Monitor;
        internal long LastMove;
        internal int Moves;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint handle, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
