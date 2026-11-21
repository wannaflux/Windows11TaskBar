using System.Runtime.InteropServices;
using System.Text;

namespace Win11Taskbar.Native;

public static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public nint Data; public int Size; }
    public delegate bool EnumProc(nint hwnd, nint data);
    public delegate void WinEventProc(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, nint data);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(nint parent, EnumProc proc, nint data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(nint hwnd, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder title, int size);
    [DllImport("user32.dll")] public static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern int MapWindowPoints(nint from, nint to, ref Rect rect, uint count);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError=true)] public static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] public static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] public static extern nint CreateRectRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")] public static extern nint CreateRoundRectRgn(int l,int t,int r,int b,int ew,int eh);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] public static extern uint GetRegionData(nint region,uint size,byte[]? data);
    [DllImport("gdi32.dll")] public static extern nint ExtCreateRegion(nint transform,uint size,byte[] data);
    // Windows 10 has no documented public taskbar acrylic API. Isolated, optional fallback.
    [DllImport("user32.dll")] static extern bool SetWindowCompositionAttribute(nint hwnd, ref CompositionData data);
    public static string Class(nint hwnd) { var b=new StringBuilder(256); GetClassName(hwnd,b,256); return b.ToString(); }
    public static string Title(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
    public static List<nint> Children(nint hwnd) { var result=new List<nint>(); EnumChildWindows(hwnd,(h,_)=>{result.Add(h);return true;},0);return result; }
    public static List<nint> Taskbars() { var result=new List<nint>(); EnumWindows((h,_)=>{if(Class(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") result.Add(h);return true;},0);return result; }
    public static bool Accent(nint hwnd,int state,uint color)
    {
        var policy=new AccentPolicy { State=state, Flags=state==4 ? 2 : 0, Color=color };
        var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try { Marshal.StructureToPtr(policy,ptr,false); var data=new CompositionData{Attribute=19,Data=ptr,Size=Marshal.SizeOf<AccentPolicy>()};return SetWindowCompositionAttribute(hwnd,ref data); }
        catch(EntryPointNotFoundException) {return false;} finally{Marshal.FreeHGlobal(ptr);}
    }
    public static Rect RelativeRect(nint hwnd) { GetWindowRect(hwnd,out var rect); MapWindowPoints(0,GetParent(hwnd),ref rect,2);return rect; }
}
