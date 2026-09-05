using System.Windows;
namespace Nocturne;
public partial class App : Application
{
    Mutex? single;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { MessageBox.Show(a.Exception.Message, "Nocturne · 出错了"); a.Handled = true; };
        if (e.Args.Contains("--self-test")) { try { await SelfTest.Run(e.Args.Contains("--integration")); Shutdown(0); } catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "test-results.txt"), ex.ToString()); Shutdown(1); } return; }
        single = new Mutex(true, "Local\\NocturnePianoDesktop", out bool created);
        if (!created) { var current = System.Diagnostics.Process.GetCurrentProcess(); var other = System.Diagnostics.Process.GetProcessesByName(current.ProcessName).FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero); if (other != null) { ShowWindow(other.MainWindowHandle, 9); SetForegroundWindow(other.MainWindowHandle); } Shutdown(); return; }
        new MainWindow().Show();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int command);
}
