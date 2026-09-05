using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace Nocturne;
public static class DarkTitle
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public static void Apply(Window w) { try { int v = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref v, 4); } catch { } }
}
