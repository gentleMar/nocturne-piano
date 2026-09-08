using System.IO;
namespace Nocturne;
public static class SelfTest
{
    public static async Task RunRecording()
    {
        using var engine = new Engine();
        var settings = Settings.Load();
        await engine.Connect(settings.PianoteqPath);
        string folder = Path.Combine(Path.GetTempPath(), "NocturneRecordingTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var lines = new List<string>();
        void Check(bool success, string message) { if (!success) throw new Exception(message); lines.Add("PASS: " + message); }
        string source = Path.Combine(Settings.Root, "Music", "First Light.mid");
        var original = Engine.First(await engine.Call("getInfo")).GetProperty("current_preset").GetProperty("name").GetString();
        try
        {
            var recorder = new MidiAudioRecorder(engine);
            string wave = await recorder.RecordAsync(settings.PianoteqPath, source, folder, null, CancellationToken.None);
            var info = MidiAudioRecorder.InspectWave(wave);
            Check(info.Channels == 2 && info.SampleRate == 48000 && info.BitsPerSample == 24, "native stereo PCM WAV, 48 kHz / 24-bit");
            Check(info.Duration > 38.4, "full demo and natural piano tail: " + info.Duration.ToString("0.000") + " seconds");
            byte[] bytes = File.ReadAllBytes(wave);
            Check(bytes.Skip(bytes.Length / 4).Take(bytes.Length / 4).Any(b => b != 0), "render contains audible sample data");
            var current = Engine.First(await engine.Call("getInfo")).GetProperty("current_preset").GetProperty("name").GetString();
            Check(original == current, "preset snapshot preserves selected preset");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool canceled = false;
            try { await recorder.RecordAsync(settings.PianoteqPath, source, folder, null, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && Directory.GetFiles(folder).Length == 1, "cancel discards partial recording and preserves completed recording");
            var seq = Engine.First(await engine.Call("getSequencerInfo"));
            Check(!seq.GetProperty("is_playing").GetBoolean(), "cancel stops playback");
            string broken = Path.Combine(folder, "broken.wav");
            File.WriteAllBytes(broken, new byte[] { 0, 1, 2, 3 });
            bool rejected = false;
            try { MidiAudioRecorder.InspectWave(broken); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "reject corrupt WAV");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "recording-test-results.txt"), lines);
        }
        finally { Directory.Delete(folder, true); }
    }

    public static async Task Run(bool integration)
    {
        var lines = new List<string>(); void Check(bool b, string msg) { if (!b) throw new Exception("FAIL: " + msg); lines.Add("PASS: " + msg); }
        var demo = MidiSong.Load(Path.Combine(Settings.Root, "Music", "First Light.mid")); Check(demo.Notes.Count == 160, "demo note count"); Check(Math.Abs(demo.Duration - 38.4) < .001, "demo tempo and duration"); Check(Settings.Defaults().Count == 20, "default mapping"); Check(Settings.Note(60) == "C4", "middle C naming");
        string tmp = Path.GetTempFileName(); try
        {
            // Format 1: a separate tempo track, running status, zero-velocity note-off.
            byte[] tempo = { 0, 255, 81, 3, 7, 161, 32, 0x83, 0x60, 255, 81, 3, 15, 66, 64, 0, 255, 47, 0 };
            byte[] notes = { 0, 144, 60, 90, 0x83, 0x60, 60, 0, 0, 64, 80, 0x83, 0x60, 128, 64, 0, 0, 255, 47, 0 };
            using (var w = new BinaryWriter(File.Create(tmp))) { void U16(int n) { w.Write((byte)(n >> 8)); w.Write((byte)n); } void U32(int n) { U16(n >> 16); U16(n); } w.Write("MThd"u8.ToArray()); U32(6); U16(1); U16(2); U16(480); foreach (var t in new[] { tempo, notes }) { w.Write("MTrk"u8.ToArray()); U32(t.Length); w.Write(t); } }
            var f = MidiSong.Load(tmp); Check(f.Notes.Count == 2 && Math.Abs(f.Notes[0].End - .5) < .001 && Math.Abs(f.Notes[1].End - 1.5) < .001, "format 1 tempo merge + running status + zero velocity"); File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 }); bool bad = false; try { MidiSong.Load(tmp); } catch { bad = true; }
            Check(bad, "reject malformed MIDI");
        }
        finally { File.Delete(tmp); }
        if (integration)
        {
            using var e = new Engine(); await e.Connect(new Settings().PianoteqPath); var info = Engine.First(await e.Call("getInfo")); Check(info.GetProperty("product_name").GetString() == "Pianoteq", "live Pianoteq RPC connection");
            var audio = Engine.First(await e.Call("getAudioDeviceInfo")); Check(audio.GetProperty("status").GetString() == "running", "audio device running");
            try
            {
                await e.Send(144, 60, 65); await Task.Delay(120); await e.Send(128, 60, 0); await e.Send(176, 64, 127); await e.Send(176, 64, 0); lines.Add("PASS: live note on/off + sustain RPC");
                await e.Call("loadMidiFile", new { path = Path.Combine(Settings.Root, "Music", "First Light.mid") }); await e.Call("midiPlay"); await Task.Delay(750); var seq = Engine.First(await e.Call("getSequencerInfo")); Check(seq.GetProperty("is_playing").GetBoolean() && seq.GetProperty("position").GetDouble() > 0, "native MIDI playback advances");
                await e.Call("midiPause"); seq = Engine.First(await e.Call("getSequencerInfo")); Check(seq.GetProperty("is_paused").GetBoolean(), "MIDI pause"); await e.Call("midiSeek", new { seconds = 4.0 }); seq = Engine.First(await e.Call("getSequencerInfo")); Check(Math.Abs(seq.GetProperty("position").GetDouble() - 4) < .1, "MIDI seek");
            }
            finally { await e.Call("midiStop"); await e.Call("midiRewind"); await e.Call("panic"); }
        }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "test-results.txt"), lines);
    }
}
