using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win11Taskbar.Core;
using Win11Taskbar.Native;
using N=Win11Taskbar.Native.NativeMethods;
namespace Win11Taskbar.App;

internal static class SmokeTests
{
    public static async Task Run(App app,string output)
    {
        Directory.CreateDirectory(output);var results=new List<string>();TaskbarService? engine=null;
        void Check(bool ok,string name){results.Add((ok?"PASS ":"FAIL ")+name);if(!ok)throw new InvalidOperationException(name);}
        try
        {
            var store=new SettingsService(Path.Combine(output,"config-test"));
            if (File.Exists(store.FilePath)) File.Delete(store.FilePath);
            Check(store.Load().Opacity==.9,"Missing config defaults");
            var settings=new Settings{OnboardingComplete=true,AnimationEnabled=false,DebugLogging=true};
            store.Save(settings);Check(store.Load().Alignment=="Center","JSON round-trip");
            File.WriteAllBytes(store.FilePath, new byte[923]);
            Check(store.Load().Enabled && !File.Exists(store.FilePath), "Zeroed settings fall back to defaults");
            Check(Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "settings.json.corrupt-*")
                .Any(path => File.ReadAllBytes(path).SequenceEqual(new byte[923])), "Damaged settings preserved");
            store.Save(settings);

            var recoveryPath = Path.Combine(output, "recovery.json");
            File.WriteAllBytes(recoveryPath, new byte[2]);
            Check(!RecoveryJournal.TryRestore(recoveryPath) && !File.Exists(recoveryPath),
                "Zeroed recovery journal skipped");
            Check(Directory.EnumerateFiles(output, "recovery.json.corrupt-*")
                .Any(path => File.ReadAllBytes(path).SequenceEqual(new byte[2])), "Damaged recovery journal preserved");
            var bars=N.Taskbars();Check(bars.Count>0,"Explorer taskbar discovered");
            var before=bars.SelectMany(h=>N.Children(h).Prepend(h)).Distinct().Select(h=>WindowSnapshot.Capture(h,bars.Contains(h))).ToArray();
            File.WriteAllText(Path.Combine(output,"before.json"),JsonSerializer.Serialize(before,WindowSnapshot.JsonOptions));
            var diagnostics=new List<string>();
            foreach(var bar in bars)foreach(var h in N.Children(bar).Prepend(bar))
            {
                N.GetWindowRect(h,out var r);diagnostics.Add($"{h} {N.Class(h)} parent={N.GetParent(h)} rect={r.Left},{r.Top},{r.Width},{r.Height}");
                if(N.Class(h)=="MSTaskListWClass")
                {
                    var buttons=AutomationElement.FromHandle(h).FindAll(TreeScope.Children,System.Windows.Automation.Condition.TrueCondition);
                    foreach(AutomationElement b in buttons)diagnostics.Add($" UIA {b.Current.ControlType.ProgrammaticName} {b.Current.Name} {b.Current.BoundingRectangle}");
                }
            }
            File.WriteAllLines(Path.Combine(output,"native-diagnostics.txt"),diagnostics);
            Log.DebugEnabled=true;
            engine=new(settings,app.Dispatcher,recoveryPath);string status="";engine.StatusChanged+=s=>status=s;
            var primary = bars.FirstOrDefault(handle => N.Class(handle) == "Shell_TrayWnd");
            var primaryState = before.First(snapshot => snapshot.Root && snapshot.Handle == primary.ToInt64());
            bool horizontal = primaryState.Position.Width > primaryState.Position.Height;
            engine.Apply();
            await Task.Delay(700);
            Check(horizontal ? status.Contains("активно") : status.Contains("ертикаль"),
                horizontal ? "Apply native styling" : "Vertical taskbar keeps native layout");
            Check(bars.All(N.IsWindowVisible),"Taskbars stay visible");
            var start = before.First(snapshot => snapshot.ClassName == "Start" &&
                N.GetParent((nint)snapshot.Handle) == primary);
            if (horizontal)
            {
            var moved=before.Where(s=>!s.Root&&s.Valid).Any(s=>N.RelativeRect((nint)s.Handle).Left!=s.Position.Left);
            Check(moved,"Actual taskbar children repositioned");
            var alignedX = N.RelativeRect((nint)start.Handle).Left;
            Check(alignedX != start.Position.Left, "Start button joins centered group");
            var search = before.FirstOrDefault(snapshot => snapshot.RoleIsSearch(primary));
            if (search is not null)
            {
                nint searchHandle = (nint)search.Handle;
                var centered = N.RelativeRect(searchHandle);
                Check(N.IsWindowVisible(searchHandle) && centered.Left > alignedX &&
                    centered.Left != search.Position.Left, "Search is visible alongside Start");
                Check(N.SetWindowPos(searchHandle, 0, search.Position.Left, centered.Top, 0, 0, 0x15),
                    "Simulated Explorer resetting the Search button");
                var repair = Stopwatch.StartNew();
                while (repair.ElapsedMilliseconds < 200 && N.RelativeRect(searchHandle).Left != centered.Left)
                    await Task.Delay(10);
                var repaired = N.RelativeRect(searchHandle);
                Check(repaired.Left == centered.Left && repaired.Width == centered.Width &&
                    N.GetParent(searchHandle) == primary, "Search reset repaired without staged animation");
            }

            var stock = start.Position;
            Check(N.SetWindowPos((nint)start.Handle, 0, stock.Left, stock.Top, stock.Width, stock.Height, 0x14),
                "Simulated Explorer resetting the Start button");
            for (int attempt = 0; attempt < 40 && N.RelativeRect((nint)start.Handle).Left != alignedX; attempt++)
                await Task.Delay(100);
            Check(N.RelativeRect((nint)start.Handle).Left == alignedX, "Centering recovered after external reset");
            }
            else
            {
                var staleRoot = WindowSnapshot.Capture(primary, true);
                staleRoot.Position = new N.Rect { Right = primaryState.Position.Height, Bottom = primaryState.Position.Width };
                var staleTray = before.First(snapshot => snapshot.ClassName == "TrayNotifyWnd" &&
                    N.GetParent((nint)snapshot.Handle) == primary);
                staleTray = WindowSnapshot.Capture((nint)staleTray.Handle);
                staleTray.Position = new N.Rect { Left = 1600, Right = 1800, Bottom = 40 };
                WindowSnapshot.RestoreAll([staleRoot, staleTray]);
                await Task.Delay(400);
                var bounds = N.RelativeRect((nint)staleTray.Handle);
                Check(bounds.Left >= 0 && bounds.Right <= primaryState.Position.Width + 2 &&
                    bounds.Height > 40, "Recovery after docking ignores stale horizontal tray coordinates");
                before = before.Select(snapshot => WindowSnapshot.Capture((nint)snapshot.Handle, snapshot.Root)).ToArray();
            }
            var window=new MainWindow(settings,engine,store);window.Show();await Task.Delay(700);window.UpdateLayout();
            window.WindowState = WindowState.Maximized;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var windowHandle = new WindowInteropHelper(window).Handle;
            var monitor = new N.MonitorInfo { Size = Marshal.SizeOf<N.MonitorInfo>() };
            Check(N.GetMonitorInfo(N.MonitorFromWindow(windowHandle, 2), ref monitor) &&
                IsWithinWorkArea(window.SaveDraftButton, monitor.Work) &&
                IsWithinWorkArea(window.DiscardDraftButton, monitor.Work),
                "Maximized Save and Cancel buttons are fully within the monitor work area");
            window.WindowState = WindowState.Normal;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(window.WindowFrame.Padding == default, "Restoring window clears maximized layout padding");
            int positionBeforeDraft = N.RelativeRect((nint)start.Handle).Left;
            window.DraftSettings.Alignment = "Left";
            window.DraftSettings.Opacity = .63;
            await Task.Delay(550);
            Check(window.HasPendingChanges && settings.Alignment == "Center" &&
                N.RelativeRect((nint)start.Handle).Left == positionBeforeDraft && store.Load().Opacity != .63,
                "Draft is visible to settings without changing native alignment or the saved file");
            Check(window.SaveDraft() && !window.HasPendingChanges && store.Load().Opacity == .63,
                "Explicit save commits draft to disk");
            if (horizontal)
            {
                for (int attempt = 0; attempt < 30 && N.RelativeRect((nint)start.Handle).Left == positionBeforeDraft; attempt++)
                    await Task.Delay(100);
                Check(settings.Alignment == "Left" && N.RelativeRect((nint)start.Handle).Left < positionBeforeDraft,
                    "Native alignment changes after explicit save");
            }
            else
            {
                Check(settings.Alignment == "Left" && N.RelativeRect((nint)start.Handle).Left == positionBeforeDraft,
                    "Save commits alignment while preserving vertical native layout");
            }
            window.DraftSettings.Alignment = "Center";
            window.RevertDraft();
            Check(window.DraftSettings.Alignment == "Left" && !window.HasPendingChanges,
                "Discard restores last saved settings");
            window.DraftSettings.Alignment = "Center";
            window.DraftSettings.Opacity = .9;
            Check(window.SaveDraft(), "Restored center for remaining smoke checks");
            foreach(var theme in new[]{"Dark","Light"})
            {
                window.DraftSettings.Theme=theme;await Task.Delay(200);window.UpdateLayout();
                var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,"settings-"+theme.ToLowerInvariant()+".png"));encoder.Save(file);
            }
            foreach (var page in new[] { "General", "Taskbar", "Animation", "Advanced", "About", "Appearance" })
            {
                window.ShowPage(page);
                window.UpdateLayout();
                Check(window.ActualWidth > 0 && window.ActualHeight > 0, $"Settings page loaded: {page}");
            }
            Check(HexColorDialog.TryParseHex("#09a5f0", out var chosenColor) &&
                HexColorDialog.FormatHex(chosenColor) == "#09A5F0" &&
                !HexColorDialog.TryParseHex("#009XFF", out _), "HEX accepts RGB color and rejects invalid digits");
            window.DraftSettings.Theme = "Dark";
            var picker = new HexColorDialog("Цвет панели", chosenColor) { Owner = window };
            picker.Show();
            await Task.Delay(300);
            picker.UpdateLayout();
            var paletteImage = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            paletteImage.Render(picker);
            var paletteEncoder = new PngBitmapEncoder();
            paletteEncoder.Frames.Add(BitmapFrame.Create(paletteImage));
            using (var paletteFile = File.Create(Path.Combine(output, "color-picker.png")))
                paletteEncoder.Save(paletteFile);
            picker.Close();
            Check(picker.SelectedHex == null && window.DraftSettings.BackgroundColor == settings.BackgroundColor,
                "Closing the color picker leaves saved and draft color unchanged");
            window.RevertDraft();settings.Enabled=false;engine.Restore();await Task.Delay(500);
            foreach(var s in before.Where(s=>!s.Root&&s.Valid&&s.ClassName is "Start" or "ReBarWindow32" or "TrayNotifyWnd"))
            {var p=N.RelativeRect((nint)s.Handle);Check(p.Left==s.Position.Left&&p.Top==s.Position.Top&&N.IsWindowVisible((nint)s.Handle)==s.Visible,"Restored "+s.ClassName);}
            foreach(var s in before.Where(s=>s.Root&&s.Valid))Check(WindowSnapshot.Capture((nint)s.Handle,true).Region?.SequenceEqual(s.Region??[])??s.Region==null,"Original window region restored");
            settings.Enabled=true;engine.Apply();await Task.Delay(300);engine.Dispose();engine=null;
            Check(bars.All(N.IsWindowVisible),"Taskbar functional after dispose");
            if (!horizontal)
            {
                results.Add("INFO Search and animation checks require a horizontal taskbar.");
                return;
            }
            var ready=Path.Combine(output,"probe.ready");if(File.Exists(ready))File.Delete(ready);
            var pi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};pi.ArgumentList.Add("--crash-probe");pi.ArgumentList.Add(output);
            using var child=Process.Start(pi)!;
            try
            {
                for(int i=0;i<100&&!File.Exists(ready);i++)await Task.Delay(100);
                Check(File.Exists(ready),"Crash probe applied customization with guardian");
                child.Kill();await child.WaitForExitAsync();await Task.Delay(1800);
                foreach(var s in before.Where(s=>!s.Root&&s.Valid&&s.ClassName is "Start" or "ReBarWindow32"))
                {var p=N.RelativeRect((nint)s.Handle);Check(p.Left==s.Position.Left&&p.Top==s.Position.Top,"Guardian crash restoration "+s.ClassName);}
                foreach(var s in before.Where(s=>s.Root&&s.Valid))Check(WindowSnapshot.Capture((nint)s.Handle,true).Region?.SequenceEqual(s.Region??[])??s.Region==null,"Guardian restored original region");
            }
            finally{if(!child.HasExited)child.Kill();}
            results.Add("INFO "+Environment.OSVersion+"; "+Environment.Version);
        }
        catch(Exception e){results.Add("ERROR "+e);}
        finally{engine?.Dispose();File.WriteAllLines(Path.Combine(output,"smoke-results.txt"),results);app.Shutdown(results.Any(r=>r.StartsWith("FAIL")||r.StartsWith("ERROR"))?1:0);}
    }

    static bool RoleIsSearch(this WindowSnapshot snapshot, nint primary) =>
        snapshot.Visible && N.GetParent((nint)snapshot.Handle) == primary &&
        (snapshot.ClassName is "SearchButton" or "TrayDummySearchControl" ||
            N.Title((nint)snapshot.Handle) is "Поиск" or "Search");

    static bool IsWithinWorkArea(FrameworkElement element, N.Rect work)
    {
        if (!element.IsVisible || element.ActualWidth == 0 || element.ActualHeight == 0) return false;
        var topLeft = element.PointToScreen(new Point());
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return topLeft.X >= work.Left && topLeft.Y >= work.Top &&
            bottomRight.X <= work.Right && bottomRight.Y <= work.Bottom;
    }
}
