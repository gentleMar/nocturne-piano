using System.Windows;

namespace Nocturne;

public partial class MainWindow
{
    BrowserPianoHost? browserHost;
    async void BrowserClick(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            await Release();
            var dialog = new BrowserSharingWindow(() => browserHost, async options =>
            {
                if (!connected) throw new InvalidOperationException("请先连接音源。");
                var host = new BrowserPianoHost(engine, () => config.Velocity, options);
                try { await host.StartAsync(); browserHost = host; }
                catch { await host.DisposeAsync(); throw; }
                Status.Text = "浏览器共享已开启 · 访问者只能弹奏琴键和聆听钢琴";
            }, async () =>
            {
                if (browserHost != null) { await browserHost.DisposeAsync(); browserHost = null; }
                Status.Text = "浏览器共享已关闭 · 邀请链接已失效";
            }) { Owner = this };
            dialog.ShowDialog();
        });
    }
}
