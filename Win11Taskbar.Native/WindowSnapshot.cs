using System.Runtime.InteropServices;
using System.Text.Json;
namespace Win11Taskbar.Native;

public sealed class WindowSnapshot
{
    public long Handle {get;set;}
    public uint ProcessId {get;set;}
    public string ClassName {get;set;}="";
    public NativeMethods.Rect Position {get;set;}
    public bool Visible {get;set;}
    public bool Root {get;set;}
    public byte[]? Region {get;set;}
    public static WindowSnapshot Capture(nint hwnd,bool root=false)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd,out var pid);
        var s=new WindowSnapshot{Handle=hwnd,ProcessId=pid,ClassName=NativeMethods.Class(hwnd),Position=NativeMethods.RelativeRect(hwnd),Visible=NativeMethods.IsWindowVisible(hwnd),Root=root};
        if(root) {var r=NativeMethods.CreateRectRgn(0,0,0,0);try {if(NativeMethods.GetWindowRgn(hwnd,r)>0){uint len=NativeMethods.GetRegionData(r,0,null);if(len>0){s.Region=new byte[len];NativeMethods.GetRegionData(r,len,s.Region);}}}finally{NativeMethods.DeleteObject(r);}}
        return s;
    }
    public bool Valid {get {var h=(nint)Handle;NativeMethods.GetWindowThreadProcessId(h,out var pid);return NativeMethods.IsWindow(h)&&pid==ProcessId&&NativeMethods.Class(h)==ClassName;}}
    public void Restore()
    {
        if(!Valid)return;
        nint h=(nint)Handle;
        if(Root) {NativeMethods.Accent(h,0,0);var r=Region==null?0:NativeMethods.ExtCreateRegion(0,(uint)Region.Length,Region);if(NativeMethods.SetWindowRgn(h,r,true)==0&&r!=0)NativeMethods.DeleteObject(r);}
        else {var p=Position;NativeMethods.SetWindowPos(h,0,p.Left,p.Top,p.Width,p.Height,0x14);RestoreVisibility();}
    }

    public bool MatchesCurrentBarSize() =>
        NativeMethods.GetWindowRect((nint)Handle, out var current) &&
        current.Width == Position.Width && current.Height == Position.Height;

    public static void RestoreAll(IEnumerable<WindowSnapshot> saved)
    {
        var snapshots = saved.Where(snapshot => snapshot.Valid).ToArray();
        var resizedBars = snapshots
            .Where(snapshot => snapshot.Root && !snapshot.MatchesCurrentBarSize())
            .Select(snapshot => (nint)snapshot.Handle)
            .ToHashSet();

        foreach (var snapshot in snapshots.Reverse())
        {
            nint handle = (nint)snapshot.Handle;
            nint bar = snapshot.Root ? handle : GetAncestor(handle, 2);
            if (!resizedBars.Contains(bar))
            {
                snapshot.Restore();
            }
            else if (snapshot.Root)
            {
                // A region from the old edge can clip most of the newly docked panel.
                NativeMethods.SetWindowRgn(handle, 0, true);
                NativeMethods.Accent(handle, 0, 0);
            }
            else
            {
                snapshot.RestoreVisibility();
            }
        }

        foreach (var bar in resizedBars)
            RequestTaskbarLayout(bar);
    }

    private void RestoreVisibility()
    {
        nint handle = (nint)Handle;
        if (NativeMethods.IsWindowVisible(handle) != Visible)
            NativeMethods.ShowWindow(handle, Visible ? 8 : 0);
    }

    public static void RequestTaskbarLayout(nint bar)
    {
        if (NativeMethods.Class(bar) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd") ||
            !GetClientRect(bar, out var client))
            return;
        // Explorer gets the same client dimensions it would receive after redocking.
        nint dimensions = (nint)((uint)(ushort)client.Width | ((uint)(ushort)client.Height << 16));
        PostMessage(bar, 0x0005, 0, dimensions); // WM_SIZE / SIZE_RESTORED
    }

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out NativeMethods.Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    public static JsonSerializerOptions JsonOptions {get;}=new(){IncludeFields=true,WriteIndented=true};
}

