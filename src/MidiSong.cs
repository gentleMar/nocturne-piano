using System.IO;
using System.Text;
namespace Nocturne;
public record PianoNote(int Pitch, double Start, double End, int Velocity);
public sealed class MidiSong
{
    public List<PianoNote> Notes { get; } = new();
    public double Duration { get; private set; }
    public double MaxNoteLength { get; private set; }
    public int Tracks { get; private set; }
    record Ev(long Tick, int Order, int Status, int A, int B, int Tempo = 0);
    public static MidiSong Load(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("MIDI 文件过大（上限 32 MB）。");
        using var r = new BinaryReader(File.OpenRead(path));
        int U16() => (r.ReadByte() << 8) | r.ReadByte();
        int U32() { uint x = ((uint)U16() << 16) | (uint)U16(); if (x > int.MaxValue) throw new InvalidDataException("MIDI 区块过大。"); return (int)x; }
        string Tag() => Encoding.ASCII.GetString(r.ReadBytes(4));
        if (Tag() != "MThd") throw new InvalidDataException("不是有效的标准 MIDI 文件。");
        int header = U32(); if (header < 6) throw new InvalidDataException("MIDI 文件头损坏。");
        int format = U16(), tracks = U16(), division = U16();
        if (format > 1) throw new InvalidDataException("暂不支持 MIDI Format 2，请导出为 Format 0 或 1。");
        if (division == 0) throw new InvalidDataException("MIDI 时间分辨率无效。");
        r.BaseStream.Seek(header - 6, SeekOrigin.Current);
        var events = new List<Ev>(); int order = 0; long lastTick = 0;
        for (int t = 0; t < tracks; t++)
        {
            if (Tag() != "MTrk") throw new InvalidDataException("MIDI 音轨损坏。");
            int len = U32(); long end = r.BaseStream.Position + len; if (end > r.BaseStream.Length) throw new InvalidDataException("MIDI 文件不完整。");
            byte Read() { if (r.BaseStream.Position >= end) throw new InvalidDataException("MIDI 事件被截断。"); return r.ReadByte(); }
            int Var() { int v = 0; for (int i = 0; i < 4; i++) { byte b = Read(); v = (v << 7) | (b & 127); if (b < 128) return v; } throw new InvalidDataException("MIDI 可变长度数字无效。"); }
            void Skip(int n) { if (r.BaseStream.Position + n > end) throw new InvalidDataException("MIDI 数据长度错误。"); r.BaseStream.Position += n; }
            long tick = 0; int running = 0;
            while (r.BaseStream.Position < end)
            {
                tick += Var(); lastTick = Math.Max(lastTick, tick); int s = Read(); int? first = null;
                if (s < 128) { if (running == 0) throw new InvalidDataException("无效的 MIDI running status。"); first = s; s = running; }
                if (s == 255) { running = 0; int type = Read(), n = Var(); if (type == 81 && n == 3) { int tempo = (Read() << 16) | (Read() << 8) | Read(); if (tempo <= 0) throw new InvalidDataException("无效的 MIDI 速度。"); events.Add(new(tick, order++, 0, 0, 0, tempo)); } else Skip(n); if (type == 47) { r.BaseStream.Position = end; break; } continue; }
                if (s == 240 || s == 247) { running = 0; Skip(Var()); continue; }
                if (s < 128 || s >= 240) throw new InvalidDataException("不支持的 MIDI 事件。");
                running = s; int a = first ?? Read(); int b = (s & 240) == 192 || (s & 240) == 208 ? 0 : Read(); if (a > 127 || b > 127) throw new InvalidDataException("无效的 MIDI 音符数据。");
                events.Add(new(tick, order++, s, a, b));
            }
        }
        var song = new MidiSong { Tracks = tracks }; var active = new Dictionary<(int, int), Queue<(double, int)>>();
        double seconds = 0; long previous = 0; int mpq = 500000;
        double TickSeconds(long delta) { if ((division & 0x8000) == 0) return delta * (mpq / 1000000.0) / division; int fps = -(sbyte)(division >> 8); if (fps is not (24 or 25 or 29 or 30) || (division & 255) == 0) throw new InvalidDataException("无效的 SMPTE 时间格式。"); return delta / ((fps == 29 ? 29.97 : fps) * (division & 255)); }
        foreach (var e in events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            seconds += TickSeconds(e.Tick - previous); previous = e.Tick; if (e.Tempo > 0) { mpq = e.Tempo; continue; }
            int type = e.Status & 240; var key = (e.Status & 15, e.A);
            if (type == 144 && e.B > 0) { if (!active.TryGetValue(key, out var q)) active[key] = q = new(); q.Enqueue((seconds, e.B)); }
            else if (type == 128 || type == 144 && e.B == 0) { if (active.TryGetValue(key, out var q) && q.Count > 0) { var n = q.Dequeue(); song.Notes.Add(new(e.A, n.Item1, seconds, n.Item2)); } }
        }
        seconds += TickSeconds(lastTick - previous); song.Duration = seconds;
        foreach (var p in active) foreach (var n in p.Value) song.Notes.Add(new(p.Key.Item2, n.Item1, seconds, n.Item2));
        song.Notes.Sort((a, b) => a.Start.CompareTo(b.Start)); song.MaxNoteLength = song.Notes.Count == 0 ? 0 : song.Notes.Max(n => n.End - n.Start); return song;
    }
}
