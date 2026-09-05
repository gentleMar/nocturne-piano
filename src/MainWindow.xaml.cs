using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
namespace Nocturne;
public partial class MainWindow : Window
{
    readonly Engine engine = new(); readonly Settings config = Settings.Load();
    readonly Dictionary<Key, int> pressed = new(); readonly Dictionary<int, int> voices = new();
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer volumeDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly Stopwatch clock = new(); double position, duration; bool loopArmed; bool connected, connecting, playing, seeking, polling, loadingPreset, ready, sustain, closing; string? songPath;
    public record Preset(string Name, string Bank);
    public MainWindow()
    {
        InitializeComponent(); Piano.Config = config; Velocity.Value = config.Velocity; OctaveLabel.Text = $"移调 {config.Transpose:+0;-0;0}"; Piano.NoteChanged += (n, on) => _ = Guard(() => Voice(n, on));
        Loaded += async (_, _) => { DarkTitle.Apply(this); ready = true; await Guard(Connect); Activate(); Focus(); Keyboard.Focus(this); };
        Deactivated += (_, _) => { if (!closing) _ = Guard(Release); };
        Closing += (_, e) => { if (closing) return; e.Cancel = true; closing = true; poll.Stop(); animation.Stop(); _ = FinishClose(); };
        poll.Tick += async (_, _) => { if (polling || !connected || seeking) return; polling = true; try { await Poll(); } catch (Exception e) { connected = false; ConnectionLabel.Text = "● 音源已断开"; Status.Text = e.Message; loopArmed = false; playing = false; Piano.Held.Clear(); } finally { polling = false; } };
        animation.Tick += (_, _) => { if (seeking) return; double t = playing ? Math.Min(duration, position + clock.Elapsed.TotalSeconds) : position; Piano.PlaybackActive = playing; Piano.Position = t; Piano.InvalidateVisual(); Progress.Value = t; TimeLabel.Text = $"{Time(t)} / {Time(duration)}"; };
        volumeDebounce.Tick += async (_, _) => { volumeDebounce.Stop(); if (connected) await Guard(() => engine.Call("setParameters", new { list = new[] { new { id = "volume", text = Volume.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) } } })); };
    }
    async Task FinishClose() { try { loopArmed = false; playing = false; await Release(); if (connected) { await engine.Call("midiStop"); await engine.Call("panic"); } config.Save(); } catch { } finally { engine.Dispose(); Close(); } }
    async Task Guard(Func<Task> action) { try { await action(); } catch (Exception e) { Status.Text = "操作失败：" + e.Message; } }
    async Task Connect()
    {
        if (connecting) return; connecting = true; ConnectionLabel.Text = "● 正在连接音源"; try
        {
            await engine.Connect(config.PianoteqPath); connected = true; ConnectionLabel.Text = "● PIANOTEQ 已连接";
            var info = Engine.First(await engine.Call("getInfo")); var current = info.GetProperty("current_preset").GetProperty("name").GetString(); var presets = await engine.Call("getListOfPresets"); loadingPreset = true;
            Presets.ItemsSource = presets.EnumerateArray().Where(p => p.GetProperty("license_status").GetString() == "ok").Select(p => new Preset(p.GetProperty("name").GetString()!, p.GetProperty("bank").GetString()!)).ToList(); Presets.SelectedItem = Presets.Items.Cast<Preset>().FirstOrDefault(p => p.Name == current); loadingPreset = false;
            var audio = Engine.First(await engine.Call("getAudioDeviceInfo")); AudioLabel.Text = $"{audio.GetProperty("device_type").GetString()} · {audio.GetProperty("sample_rate").GetString()} Hz · 缓冲 {audio.GetProperty("buffer_size").GetString()}";
            var pars = await engine.Call("getParameters"); var vol = pars.EnumerateArray().FirstOrDefault(p => p.GetProperty("id").GetString() == "volume"); if (vol.ValueKind != System.Text.Json.JsonValueKind.Undefined && double.TryParse(vol.GetProperty("text").GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { ready = false; Volume.Value = v; ready = true; }
            Status.Text = audio.GetProperty("status").GetString() == "running" ? "音源已就绪 · 使用英文输入法弹奏，支持多键和弦。" : "音频设备未运行，请在 Pianoteq 中设置音频输出。";
            poll.Start(); animation.Start(); if (songPath == null) { string demo = Path.Combine(Settings.Root, "Music", "First Light.mid"); if (File.Exists(demo)) await LoadSong(demo); }
        }
        catch { connected = false; ConnectionLabel.Text = "● 连接失败"; throw; }
        finally { connecting = false; }
    }
    async Task Poll()
    {
        var s = Engine.First(await engine.Call("getSequencerInfo")); bool was = playing; playing = s.GetProperty("is_playing").GetBoolean(); position = s.GetProperty("position").GetDouble(); duration = s.GetProperty("duration").GetDouble(); clock.Restart(); Progress.Maximum = Math.Max(0.01, duration); PlayButton.Content = playing ? "Ⅱ 暂停" : "▶ 播放";
        if (loopArmed && was && !playing && !s.GetProperty("is_paused").GetBoolean() && Loop.IsChecked == true && songPath != null) { await engine.Call("midiRewind"); await engine.Call("midiPlay"); }
    }
    async Task Voice(int n, bool on) { if (!connected) return; if (on) { voices.TryGetValue(n, out int count); voices[n] = count + 1; if (count > 0) return; Piano.Held.Add(n); NoteLabel.Text = $"{Settings.Note(n)}  ·  MIDI {n}  ·  力度 {config.Velocity}"; await engine.Send(0x90, n, config.Velocity); } else { if (!voices.TryGetValue(n, out int count)) return; if (count > 1) { voices[n] = count - 1; return; } voices.Remove(n); Piano.Held.Remove(n); await engine.Send(0x80, n, 0); } Piano.InvalidateVisual(); }
    async Task Release() { pressed.Clear(); var notes = voices.Keys.ToArray(); voices.Clear(); Piano.Held.Clear(); foreach (int n in notes) if (connected) await engine.Send(0x80, n, 0); await SetSustain(false); Piano.InvalidateVisual(); }
    async Task SetSustain(bool on) { sustain = on; SustainButton.Content = on ? "延音 ON" : "延音 OFF"; if (connected) await engine.Send(0xB0, 64, on ? 127 : 0); }
    bool Editing() => Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || Presets.IsDropDownOpen;
    async void KeyDownHandler(object sender, KeyEventArgs e)
    {
        if (Editing() || Keyboard.Modifiers != ModifierKeys.None) return; var k = e.Key; if (k == Key.Escape) { e.Handled = true; await Guard(async () => { loopArmed = false; playing = false; await Release(); if (connected) { await engine.Call("midiStop"); await engine.Call("panic"); } Status.Text = "已紧急止音"; }); return; }
        if (e.IsRepeat) return;
        if (k == Key.Space) { e.Handled = true; await Guard(() => SetSustain(true)); return; }
        if (k == Key.Up || k == Key.Down) { e.Handled = true; await Guard(() => Transpose(k == Key.Up ? 12 : -12)); return; }
        if (config.Mapping.TryGetValue(k.ToString(), out int n) && !pressed.ContainsKey(k) && connected) { n += config.Transpose; if (n < 0 || n > 127) return; e.Handled = true; pressed[k] = n; await Guard(() => Voice(n, true)); }
    }
    async void KeyUpHandler(object sender, KeyEventArgs e) { if (e.Key == Key.Space) { e.Handled = true; await Guard(() => SetSustain(false)); } if (pressed.Remove(e.Key, out int n)) { e.Handled = true; await Guard(() => Voice(n, false)); } }
    async Task Transpose(int delta) { await Release(); config.Transpose = Math.Clamp(config.Transpose + delta, -24, 24); OctaveLabel.Text = $"移调 {config.Transpose:+0;-0;0}"; config.Save(); Piano.InvalidateVisual(); }
    void ExpressionChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (VelocityText == null) return; config.Velocity = (int)Velocity.Value; VelocityText.Text = config.Velocity.ToString(); if (ready) config.Save(); }
    void VolumeChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (VolumeText == null) return; VolumeText.Text = $"{Volume.Value:0} dB"; if (ready) { volumeDebounce.Stop(); volumeDebounce.Start(); } }
    async void PresetChanged(object s, SelectionChangedEventArgs e) { if (loadingPreset || !connected || Presets.SelectedItem is not Preset p) return; await Guard(async () => { await Release(); await engine.Call("loadPreset", new { name = p.Name, bank = p.Bank }); Status.Text = "当前音色：" + p.Name; }); }
    async Task LoadSong(string path) { var song = await Task.Run(() => MidiSong.Load(path)); if (!connected) throw new InvalidOperationException("请先连接音源。"); loopArmed = false; playing = false; await Release(); await engine.Call("midiStop"); await engine.Call("loadMidiFile", new { path }); songPath = path; Piano.Song = song; position = 0; loopArmed = false; playing = false; duration = song.Duration; Progress.Maximum = Math.Max(.01, duration); SongLabel.Text = Path.GetFileNameWithoutExtension(path); Status.Text = $"{song.Tracks} 条音轨 · {song.Notes.Count:N0} 个音符 · 已载入，点击播放"; await Poll(); }
    async void OpenClick(object s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "MIDI 文件|*.mid;*.midi", InitialDirectory = Path.Combine(Settings.Root, "Music") }; if (d.ShowDialog() == true) await Guard(() => LoadSong(d.FileName)); }
    async void FileDrop(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) await Guard(() => LoadSong(paths[0])); }
    async void DemoClick(object s, RoutedEventArgs e) => await Guard(() => LoadSong(Path.Combine(Settings.Root, "Music", "First Light.mid")));
    async void PlayClick(object s, RoutedEventArgs e) => await Guard(async () => { if (songPath == null) throw new InvalidOperationException("请先打开 MIDI 文件。"); loopArmed = !playing; await engine.Call(playing ? "midiPause" : "midiPlay"); await Poll(); });
    async void StopClick(object s, RoutedEventArgs e) => await Guard(async () => { loopArmed = false; playing = false; await Release(); if (connected) { await engine.Call("midiStop"); await engine.Call("midiRewind"); await engine.Call("panic"); await Poll(); } });
    void SeekStart(object s, MouseButtonEventArgs e) { seeking = true; }
    async void SeekEnd(object s, MouseButtonEventArgs e) { if (!seeking) return; await Guard(async () => { if (songPath != null && connected) await engine.Call("midiSeek", new { seconds = Progress.Value }); }); seeking = false; await Guard(Poll); }
    async void ConnectClick(object s, RoutedEventArgs e) => await Guard(Connect);
    async void OctaveDown(object s, RoutedEventArgs e) => await Guard(() => Transpose(-12));
    async void OctaveUp(object s, RoutedEventArgs e) => await Guard(() => Transpose(12));
    async void SustainClick(object s, RoutedEventArgs e) => await Guard(() => SetSustain(!sustain));
    async void SettingsClick(object s, RoutedEventArgs e) { await Guard(Release); var d = new MappingWindow(config) { Owner = this }; if (d.ShowDialog() == true) { config.Save(); Piano.InvalidateVisual(); Status.Text = "按键映射已保存 · 新路径将在下次连接时使用"; } }
    static string Time(double t) => $"{(int)Math.Max(0, t) / 60:00}:{(int)Math.Max(0, t) % 60:00}";
}
