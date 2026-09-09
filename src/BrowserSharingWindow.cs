using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nocturne;

public sealed class BrowserSharingWindow : Window
{
    readonly Func<BrowserPianoHost?> current;
    readonly Func<BrowserPianoOptions, Task> start;
    readonly Func<Task> stop;
    readonly BrowserPianoOptions options = BrowserPianoOptions.Load();
    readonly TextBox port = new(), stun = new(), turn = new(), user = new(), link = new();
    readonly PasswordBox password = new();
    readonly CheckBox lan = new() { Content = "允许局域网访问（需要 Windows 防火墙允许此程序）" };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) };
    readonly Button toggle = new() { Content = "开启共享", Padding = new Thickness(18, 10, 18, 10) };
    readonly StackPanel settings = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool busy;
    public BrowserSharingWindow(Func<BrowserPianoHost?> current, Func<BrowserPianoOptions, Task> start, Func<Task> stop)
    {
        this.current = current; this.start = start; this.stop = stop;
        Title = "Nocturne · 浏览器共享"; Width = 650; Height = 690; MinWidth = 540; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 23, 25)); Foreground = Brushes.WhiteSmoke;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
        var body = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = "把琴房分享出去", FontSize = 25, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(new TextBlock { Text = "手机或电脑打开邀请链接，即可轻触琴键和聆听。\nWebRTC 优先 · 仅 Pianoteq 声音 · 最多 8 位访问者", Foreground = Brushes.DarkSeaGreen, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        body.Children.Add(settings);
        Field("本地网页端口", port); port.Text = options.Port.ToString();
        lan.Foreground = Foreground; lan.Margin = new Thickness(0, 8, 0, 12); lan.IsChecked = options.AllowLan; settings.Children.Add(lan);
        var advanced = new Expander { Header = "WebRTC 中继配置（可选）", Margin = new Thickness(0, 4, 0, 10) };
        var advancedBody = new StackPanel(); advanced.Content = advancedBody; settings.Children.Add(advanced);
        Field("STUN 地址", stun, advancedBody); stun.Text = options.StunUrl;
        Field("TURN 地址（如 turn:your-server:3478）", turn, advancedBody); turn.Text = options.TurnUrl;
        Field("TURN 用户名", user, advancedBody); user.Text = options.TurnUser;
        advancedBody.Children.Add(new TextBlock { Text = "TURN 密码", Margin = new Thickness(0, 8, 0, 4) });
        password.Password = options.TurnPassword; password.Padding = new Thickness(8); advancedBody.Children.Add(password);
        body.Children.Add(new TextBlock { Text = "内网穿透目标：http://127.0.0.1:端口，需支持 WebSocket。\n外网请使用 HTTPS。只有网页穿透也能使用备用音频；配置 TURN 可提高 WebRTC 在复杂网络下的连通率。", Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        body.Children.Add(status); body.Children.Add(toggle);
        body.Children.Add(new TextBlock { Text = "邀请链接（开启后生成，关闭共享立即失效）", Margin = new Thickness(0, 20, 0, 8) });
        link.IsReadOnly = true; link.TextWrapping = TextWrapping.Wrap; link.Padding = new Thickness(8); body.Children.Add(link);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var copy = new Button { Content = "复制邀请链接" }; copy.Click += (_, _) => { if (current() != null) Clipboard.SetText(link.Text); };
        var open = new Button { Content = "本机浏览器试听" }; open.Click += (_, _) => { if (current() is { } host) Process.Start(new ProcessStartInfo(host.LocalUrl) { UseShellExecute = true }); };
        buttons.Children.Add(copy); buttons.Children.Add(open); body.Children.Add(buttons);
        body.Children.Add(new TextBlock { Text = "配置好穿透后，将链接中的 http://127.0.0.1:端口 替换成你的 HTTPS 域名，保留 /#key=… 部分。请仅把完整链接发给你允许进入琴房的人。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 12, 0, 0) });
        toggle.Click += Toggle; timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { DarkTitle.Apply(this); Refresh(); timer.Start(); };
        Closing += (_, e) => { if (busy) e.Cancel = true; }; Closed += (_, _) => timer.Stop();
    }
    void Field(string label, TextBox input, StackPanel? panel = null)
    {
        panel ??= settings; panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 4) });
        input.Padding = new Thickness(8); panel.Children.Add(input);
    }
    void Refresh()
    {
        if (busy) return;
        var host = current(); settings.IsEnabled = host == null;
        toggle.Content = host == null ? "开启共享" : "关闭共享并撤销邀请";
        if (host != null) { link.Text = host.LocalUrl; status.Text = host.LastError ?? $"● 共享中 · {host.ClientCount} 位访问者 · 端口 {host.Options.Port}"; }
        else { link.Text = "尚未开启"; if (!status.Text.StartsWith("操作失败")) status.Text = "共享已关闭 · 每次开启都会生成新的邀请密钥"; }
    }
    async void Toggle(object sender, RoutedEventArgs e)
    {
        busy = true; toggle.IsEnabled = false; status.Text = "正在处理…";
        try
        {
            if (current() != null) await stop();
            else
            {
                if (!int.TryParse(port.Text, out int number)) throw new ArgumentException("端口请输入数字。");
                options.Port = number; options.AllowLan = lan.IsChecked == true;
                options.StunUrl = stun.Text.Trim(); options.TurnUrl = turn.Text.Trim(); options.TurnUser = user.Text.Trim(); options.TurnPassword = password.Password;
                options.Validate(); options.Save(); await start(options);
            }
        }
        catch (Exception ex) { status.Text = "操作失败：" + ex.Message; }
        finally { busy = false; toggle.IsEnabled = true; Refresh(); }
    }
}
