using System.Diagnostics;
using System.IO;
using System.Windows;
using Win11Taskbar.Core;
using Forms=System.Windows.Forms;
namespace Win11Taskbar.App;

public partial class App : System.Windows.Application
{
    const string InstanceMutex = @"Local\Win11Taskbar.App";
    const string RecoveryMutex = @"Local\Win11Taskbar.Recovery";
    const string OpenSettingsEventName = @"Local\Win11Taskbar.OpenSettings";
    internal static string VersionLabel => typeof(App).Assembly.GetName().Version!.ToString(3);
    Mutex? mutex;Forms.NotifyIcon? tray;TaskbarService? engine;MainWindow? window;Settings? settings;SettingsService? store;string? cleanMarker;
    EventWaitHandle? openSettingsEvent;
    RegisteredWaitHandle? openSettingsWait;
    ShellFlyoutPositioner? flyouts;
    public bool Exiting {get;private set;}
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.FirstOrDefault()=="--watchdog") {Watchdog(e.Args);Shutdown();return;}
        if(e.Args.Contains("--restore")){Shutdown(RecoveryJournal.TryRestore(RecoveryJournal.DefaultPath) ? 0 : 1);return;}
        if(e.Args.FirstOrDefault()=="--smoke-test"){_=SmokeTests.Run(this,e.Args.ElementAtOrDefault(1)??Path.Combine(Log.Folder,"TestResults"));return;}
        if(e.Args.FirstOrDefault()=="--crash-probe")
        {
            Directory.CreateDirectory(e.Args[1]);var probe=new Settings{AnimationEnabled=false,OnboardingComplete=true};engine=new(probe,Dispatcher,Path.Combine(e.Args[1],"recovery.json"));StartWatchdog(engine.RecoveryPath);engine.Apply();File.WriteAllText(Path.Combine(e.Args[1],"probe.ready"),Environment.ProcessId.ToString());return;
        }
        if (e.Args.FirstOrDefault() == "--restart-after" && !WaitForPreviousInstance(e.Args))
        {
            Shutdown(1);
            return;
        }

        openSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEventName);
        mutex = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            if (!e.Args.Contains("--minimized")) openSettingsEvent.Set();
            Shutdown();
            return;
        }
        try
        {
            // Recover a previous interrupted session before taking a new snapshot.
            WithRecoveryLock(() => RecoveryJournal.TryRestore(RecoveryJournal.DefaultPath));
            store = new SettingsService();
            settings = store.Load();
            try
            {
                settings.StartWithWindows = AutostartService.Enabled();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Write("WARNING", $"Autostart is unavailable: {exception.Message}");
            }
            Log.DebugEnabled=settings.DebugLogging;Log.Write("INFO",$"Starting {VersionLabel}, Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
            engine=new(settings,Dispatcher);
            flyouts = new ShellFlyoutPositioner(settings, Dispatcher);
            StartWatchdog(engine.RecoveryPath);
            window=new(settings,engine,store);MainWindow=window;new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            CreateTray();
            openSettingsWait = ThreadPool.RegisterWaitForSingleObject(openSettingsEvent, (_, _) =>
                Dispatcher.BeginInvoke(() => { if (!Exiting) ShowSettings(); }), null, Timeout.Infinite, false);
            settings.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(Settings.StartWithWindows)){try{AutostartService.Set(settings.StartWithWindows);}catch(Exception ex){Log.Write("ERROR",ex.Message);window.Notify("Не удалось изменить автозапуск: "+ex.Message);}}Log.DebugEnabled=settings.DebugLogging;};
            DispatcherUnhandledException+=(_,args)=>{Log.Write("ERROR",args.Exception.ToString());engine.Restore();args.Handled=true;window.Notify("Ошибка обработана. Панель восстановлена. "+args.Exception.Message);};
            SessionEnding+=(_,_)=>ExitApp();
            bool showSettings = e.Args.Contains("--show-settings");
            if(showSettings || (!settings.StartMinimized && !e.Args.Contains("--minimized")))window.Show();
            engine.Queue();
            var diagnostics=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
            diagnostics.Tick+=(_,_)=>{diagnostics.Stop();Log.Write("INFO",$"Startup diagnostics: events={engine.EventCount}, applies={engine.ApplyCount}, animationFrames={engine.FrameCount}");};diagnostics.Start();
            if(!settings.OnboardingComplete&&!showSettings){window.Show();window.ShowWelcome();}
            if(settings.AutoCheckUpdates)_=CheckUpdates(false);
        }
        catch(Exception ex){Log.Write("ERROR",ex.ToString());engine?.Dispose();System.Windows.MessageBox.Show(ex.Message,"Win11Taskbar — запуск");Shutdown(1);}
    }
    void StartWatchdog(string path)
    {
        var p=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        cleanMarker=path+"."+Environment.ProcessId+".clean";
        p.ArgumentList.Add("--watchdog");p.ArgumentList.Add(Environment.ProcessId.ToString());p.ArgumentList.Add(path);p.ArgumentList.Add(cleanMarker);Process.Start(p);
    }
    static void Watchdog(string[] args)
    {
        if(args.Length!=4||!int.TryParse(args[1],out int pid))return;
        try{using var process=Process.GetProcessById(pid);process.WaitForExit();}catch(ArgumentException){}
        try
        {
            if (File.Exists(args[3])) { File.Delete(args[3]); return; }
            WithRecoveryLock(() =>
            {
                // A newer instance restores the journal itself before taking snapshots.
                if (args[2] == RecoveryJournal.DefaultPath &&
                    Mutex.TryOpenExisting(InstanceMutex, out var runningInstance))
                {
                    runningInstance.Dispose();
                    return;
                }
                if (RecoveryJournal.TryRestore(args[2]))
                    Log.Write("INFO", "Recovery guardian restored taskbar after owner exit.");
            });
        }
        catch (Exception ex)
        {
            Log.Write("ERROR", "Guardian: " + ex.Message);
        }
    }

    static void WithRecoveryLock(Action action)
    {
        using var recoveryLock = new Mutex(false, RecoveryMutex);
        try { recoveryLock.WaitOne(); }
        catch (AbandonedMutexException) { /* The mutex now belongs to this thread. */ }
        try { action(); }
        finally { recoveryLock.ReleaseMutex(); }
    }

    static bool WaitForPreviousInstance(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out int pid)) return false;
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (previous.WaitForExit(10000)) return true;
            Log.Write("WARNING", "Restart timed out while waiting for the previous instance.");
            return false;
        }
        catch (ArgumentException) { return true; }
    }

    void RestartApp()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--restart-after");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            using var replacement = Process.Start(start);
            if (replacement is not null) ExitApp();
        }
        catch (Exception e)
        {
            Log.Write("ERROR", $"Restart failed: {e.Message}");
            ShowSettings();
            window!.Notify("Не удалось перезапустить Win11Taskbar.");
        }
    }
    private TrayMenu? trayMenu;
    private System.Drawing.Icon? trayIcon;

    private void CreateTray()
    {
        trayIcon = TrayIcon.Create();
        trayMenu = new TrayMenu(settings!, window!.DraftSettings, () => window.HasPendingChanges,
            ShowSettings, window.RestartExplorer, RestartApp,
            () => CheckUpdates(true), ExitApp);
        tray = new Forms.NotifyIcon
        {
            Text = "Win11Taskbar",
            Icon = trayIcon,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowSettings();
        };
    }

    private void DisposeTray()
    {
        tray?.Dispose();
        tray = null;
        trayMenu?.Dispose();
        trayMenu = null;
        trayIcon?.Dispose();
        trayIcon = null;
    }
    public void ShowSettings()
    {
        window!.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    public async Task CheckUpdates(bool show){try{var result=await UpdateService.CheckAsync(settings!.UpdateEndpoint);if(show){ShowSettings();window!.Notify(result);}}catch(Exception ex){Log.Write("WARNING","Update: "+ex.Message);if(show)window!.Notify("Проверка обновлений: "+ex.Message);}}
    public void ExitApp()
    {
        if (Exiting) return;
        Exiting = true;
        flyouts?.Dispose();
        engine?.Dispose();
        DisposeTray();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        openSettingsWait?.Unregister(null);
        openSettingsEvent?.Dispose();
        flyouts?.Dispose();
        engine?.Dispose();
        DisposeTray();
        if (cleanMarker is not null)
        {
            try { File.WriteAllText(cleanMarker, "restored"); }
            catch (IOException) { }
        }
        mutex?.Dispose();
        base.OnExit(e);
    }
}
