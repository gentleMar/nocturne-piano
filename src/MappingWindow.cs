using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
namespace Nocturne;
public sealed class MappingWindow : Window
{
    readonly Settings config; Dictionary<string, int> map; readonly StackPanel rows = new(); readonly TextBox path = new(); readonly TextBlock hint = new(); readonly ComboBox note = new(); readonly Button capture = new() { Content = "点击后按下一个键" }; string? pending; bool listening;
    public MappingWindow(Settings s)
    {
        config = s; map = new(s.Mapping); Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#111719")!; Foreground = System.Windows.Media.Brushes.White; FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"); Title = "Nocturne · 设置与按键映射"; Width = 660; Height = 760; MinWidth = 600; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner; Loaded += (_, _) => DarkTitle.Apply(this);
        var root = new DockPanel { Margin = new Thickness(28) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "让键盘适合你的手", FontSize = 25, Margin = new Thickness(0, 0, 0, 8) });
        top.Children.Add(new TextBlock { Text = "可绑定字母、数字、标点和数字小键盘。Space / ↑ / ↓ / Esc 为保留键。", FontSize = 11, Margin = new Thickness(0, 0, 0, 22) });
        top.Children.Add(new TextBlock { Text = "PIANOTEQ 程序路径", FontSize = 11 }); var p = new DockPanel { Margin = new Thickness(0, 8, 0, 20) }; var browse = new Button { Content = "浏览", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); p.Children.Add(browse); path.Text = s.PianoteqPath; p.Children.Add(path); top.Children.Add(p); browse.Click += (_, _) => { var d = new OpenFileDialog { Filter = "Pianoteq 程序|*.exe" }; if (d.ShowDialog() == true) path.Text = d.FileName; };
        var add = new StackPanel { Orientation = Orientation.Horizontal }; top.Children.Add(add); capture.Width = 215; capture.Click += (_, _) => { listening = true; InputMethod.SetIsInputMethodEnabled(this, false); Focus(); Keyboard.Focus(this); capture.Content = "现在按下要绑定的键…"; }; add.Children.Add(capture);
        note.Width = 125; note.ItemsSource = Enumerable.Range(21, 88).Select(n => new NoteItem(n, $"{Settings.Note(n)} · {n}")).ToList(); note.DisplayMemberPath = "Name"; note.SelectedValuePath = "Number"; note.SelectedValue = 60; add.Children.Add(note);
        var save = new Button { Content = "绑定 / 更新", Margin = new Thickness(10, 0, 0, 0) }; add.Children.Add(save); save.Click += (_, _) => { if (pending == null) { hint.Text = "请先点击左侧按钮并按下一个键。"; return; } map[pending] = (int)note.SelectedValue; hint.Text = $"已绑定 {Settings.Label(pending)} → {Settings.Note(map[pending])}"; Refresh(); };
        hint.Text = "同一按键再次绑定会替换旧映射。音符以中央 C = C4 为准。"; hint.Margin = new Thickness(0, 12, 0, 14); hint.FontSize = 11; top.Children.Add(hint);
        var bottom = new StackPanel { Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); var actions = new StackPanel { Orientation = Orientation.Horizontal }; bottom.Children.Add(actions);
        void Action(string title, Action fn) { var b = new Button { Content = title }; b.Click += (_, _) => { try { fn(); } catch (Exception e) { hint.Text = e.Message; } }; actions.Children.Add(b); }
        Action("恢复默认", () => { map = Settings.Defaults(); Refresh(); }); Action("导入", () => { var d = new OpenFileDialog { Filter = "映射配置|*.json" }; if (d.ShowDialog() != true) return; var candidate = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(d.FileName)) ?? throw new Exception("配置为空"); if (candidate.Any(x => !Enum.TryParse<Key>(x.Key, out var k) || !Settings.Allowed(k) || x.Value < 21 || x.Value > 108)) throw new Exception("映射包含无效按键或超出 21–108 的音符。"); map = candidate; Refresh(); }); Action("导出", () => { var d = new SaveFileDialog { Filter = "映射配置|*.json", FileName = "my-piano-keys.json" }; if (d.ShowDialog() == true) File.WriteAllText(d.FileName, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true })); }); Action("保存设置", () => { if (!File.Exists(path.Text) || !path.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new Exception("请选择有效的 Pianoteq .exe 文件。"); config.Mapping = new(map); config.PianoteqPath = path.Text; DialogResult = true; });
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Refresh();
        PreviewKeyDown += (_, e) => { if (!listening) return; e.Handled = true; var key = e.Key == Key.System ? e.SystemKey : e.Key; if (!Settings.Allowed(key)) { hint.Text = "这个键是保留键，请使用字母、数字或标点。"; return; } pending = key.ToString(); listening = false; InputMethod.SetIsInputMethodEnabled(this, true); capture.Content = "按键：" + Settings.Label(pending); };
    }
    record NoteItem(int Number, string Name);
    void Refresh() { rows.Children.Clear(); foreach (var pair in map.OrderBy(p => p.Value).ThenBy(p => p.Key)) { var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) }; var remove = new Button { Content = "移除", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0) }; DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove); remove.Click += (_, _) => { map.Remove(pair.Key); Refresh(); }; row.Children.Add(new TextBlock { Text = $"{Settings.Label(pair.Key),-12} →   {Settings.Note(pair.Value)}     ·     MIDI {pair.Value}", VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(10, 0, 0, 0) }); rows.Children.Add(row); } }
}
