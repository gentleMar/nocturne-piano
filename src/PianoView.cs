using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Globalization;
namespace Nocturne;
public sealed class PianoView : FrameworkElement
{
    public Settings Config { get; set; } = new();
    public MidiSong? Song { get; set; }
    public double Position { get; set; }
    public bool PlaybackActive { get; set; }
    public HashSet<int> Held { get; } = new();
    public event Action<int, bool>? NoteChanged;
    readonly Dictionary<int, Rect> keys = new(); int? mouseNote;
    static Brush B(string s) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
    readonly Brush mint = B("#A8E8CE"), muted = B("#64777B"), white = B("#E8E7DE"), black = B("#172023");
    public static bool IsBlack(int n) => n % 12 is 1 or 3 or 6 or 8 or 10;
    void Text(DrawingContext d, string s, double x, double y, double size, Brush b) { var f = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip); d.DrawText(f, new Point(x, y)); }
    protected override void OnRender(DrawingContext d)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 10 || h < 40) return; double kh = Math.Min(175, h * .43), top = h - kh; keys.Clear();
        d.DrawRectangle(B("#141E21"), null, new Rect(0, 0, w, h));
        int low = 36, high = 96; int count = Enumerable.Range(low, high - low + 1).Count(n => !IsBlack(n)); double kw = w / count; int wi = 0;
        for (int n = low; n <= high; n++) if (!IsBlack(n)) { keys[n] = new Rect(wi * kw, top, kw - 1, kh); wi++; } else keys[n] = new Rect(wi * kw - kw * .30, top, kw * .60, kh * .62);
        for (int i = 0; i < 5; i++) { double y = top - i * top / 4; d.DrawLine(new Pen(B("#243235"), 1), new Point(0, y), new Point(w, y)); }
        foreach (var p in keys.Where(p => p.Key % 12 == 0)) { d.DrawLine(new Pen(B("#263538"), 1), new Point(p.Value.X, 0), new Point(p.Value.X, top)); Text(d, Settings.Note(p.Key), p.Value.X + 5, 9, 10, muted); }
        var active = new HashSet<int>(Held);
        if (Song != null)
        {
            // Binary search avoids scanning the entire file at every animation frame.
            double maxEnd = Song.MaxNoteLength;
            int l = 0, r = Song.Notes.Count; while (l < r) { int m = (l + r) / 2; if (Song.Notes[m].Start < Position - maxEnd) l = m + 1; else r = m; }
            for (int i = l; i < Song.Notes.Count; i++)
            {
                var note = Song.Notes[i]; if (note.Start > Position + 4) break; if (note.End < Position) continue; if (PlaybackActive && note.Start <= Position && note.End > Position) active.Add(note.Pitch); if (!keys.TryGetValue(note.Pitch, out var rect)) continue;
                double bottom = top - (note.Start - Position) / 4 * top, y = top - (note.End - Position) / 4 * top; y = Math.Max(25, y); bottom = Math.Min(top, bottom); if (bottom <= y) continue;
                var brush = B(IsBlack(note.Pitch) ? "#669B8F" : "#A8E8CE"); d.DrawRoundedRectangle(brush, null, new Rect(rect.X + 2, y, Math.Max(2, rect.Width - 4), bottom - y), 3, 3);
            }
        }
        else { Text(d, "让每一次落键，都成为音乐。", w * .32, top * .42, 22, B("#B6C8C7")); Text(d, "弹奏电脑键盘，或拖入 MIDI 文件开始", w * .35, top * .42 + 38, 12, muted); }
        foreach (bool isBlack in new[] { false, true }) foreach (var p in keys.Where(p => IsBlack(p.Key) == isBlack))
            {
                bool on = active.Contains(p.Key); var rect = p.Value; d.DrawRoundedRectangle(on ? mint : isBlack ? black : white, new Pen(B("#0D1517"), 1), rect, 0, 3);
                if (isBlack && !on) d.DrawLine(new Pen(B("#344246"), 2), new Point(rect.Left + 3, rect.Bottom - 5), new Point(rect.Right - 3, rect.Bottom - 5));
                string? binding = Config.Mapping.FirstOrDefault(x => x.Value + Config.Transpose == p.Key).Key;
                if (binding != null) Text(d, Settings.Label(binding), rect.X + rect.Width * .24, rect.Bottom - 26, Math.Min(12, kw * .44), isBlack && !on ? white : black);
                if (!isBlack && p.Key % 12 == 0) Text(d, Settings.Note(p.Key), rect.X + 4, rect.Bottom - 45, 9, B("#688079"));
            }
        d.DrawLine(new Pen(mint, 2), new Point(0, top), new Point(w, top));
    }
    int? Hit(Point p) { foreach (bool b in new[] { true, false }) foreach (var k in keys.Where(x => IsBlack(x.Key) == b)) if (k.Value.Contains(p)) return k.Key; return null; }
    protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); if (e.ChangedButton != MouseButton.Left) return; CaptureMouse(); Change(Hit(e.GetPosition(this))); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Change(Hit(e.GetPosition(this))); }
    protected override void OnMouseUp(MouseButtonEventArgs e) { Change(null); ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { Change(null); }
    void Change(int? n) { if (n == mouseNote) return; if (mouseNote is int old) NoteChanged?.Invoke(old, false); mouseNote = n; if (n is int next) NoteChanged?.Invoke(next, true); }
}
