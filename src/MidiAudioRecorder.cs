using System.Diagnostics;
using System.IO;
using System.Text;

namespace Nocturne;

public sealed record RecordingProgress(double Position, double Duration, bool Playing, string Message);
public sealed record WaveInfo(int Channels, int SampleRate, int BitsPerSample, double Duration);

/// <summary>Renders MIDI inside Pianoteq; never captures an audio device or system loopback.</summary>
public sealed class MidiAudioRecorder
{
    readonly Engine engine;
    public MidiAudioRecorder(Engine engine) => this.engine = engine;

    public async Task<string> RecordAsync(string executable, string midiPath, string outputDirectory,
        IProgress<RecordingProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        string session = Path.Combine(Path.GetTempPath(), "NocturneRecording-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string snapshot = Path.Combine(session, "instrument.fxp");
        string midi = Path.Combine(session, "sequence.mid");
        string wave = Path.Combine(session, "render.wav");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? render = null;
        try
        {
            token.ThrowIfCancellationRequested();
            await engine.Call("midiStop");
            await engine.Call("panic");
            await engine.Call("loadMidiFile", new { path = midiPath });
            await engine.Call("midiRewind");
            await engine.Call("saveMidiFile", new { path = midi });
            await SnapshotAsync(snapshot);
            token.ThrowIfCancellationRequested();
            var sequence = Engine.First(await engine.Call("getSequencerInfo"));
            double duration = sequence.GetProperty("duration").GetDouble();
            render = RenderAsync(executable, snapshot, midi, wave, session, duration, lifetime.Token);
            await engine.Call("midiPlay");
            var elapsed = Stopwatch.StartNew();
            double furthest = 0;
            bool started = false;
            while (true)
            {
                await Task.Delay(120, token);
                if (render.IsFaulted || render.IsCanceled) await render;
                sequence = Engine.First(await engine.Call("getSequencerInfo"));
                double position = sequence.GetProperty("position").GetDouble();
                bool playing = sequence.GetProperty("is_playing").GetBoolean();
                bool paused = sequence.GetProperty("is_paused").GetBoolean();
                started |= playing || position > 0;
                furthest = Math.Max(furthest, position);
                progress?.Report(new(position, duration, playing, "正在录制整曲 · 仅保存钢琴声音 · F9 / Esc 取消"));
                if (paused) throw new IOException("Pianoteq 播放被暂停，录制已取消，没有保存不完整的音频。");
                if (!playing)
                {
                    if (elapsed.Elapsed.TotalSeconds < 1 && !started) continue;
                    if (duration > 0.5 && (furthest < duration - 0.5 || elapsed.Elapsed.TotalSeconds < duration - 0.75))
                        throw new IOException("Pianoteq 未完整播放所选 MIDI，录制已取消。请保持播放不中断。");
                    break;
                }
                if (elapsed.Elapsed.TotalSeconds > duration + 30)
                    throw new IOException("播放超时，请关闭 Pianoteq 自身的循环播放后重试。");
            }
            progress?.Report(new(duration, duration, false, "整曲播放完毕，正在保存钢琴尾音…"));
            await render;
            token.ThrowIfCancellationRequested();
            var info = InspectWave(wave);
            if (info.SampleRate != 48000 || info.BitsPerSample != 24 || info.Duration < duration - 0.25)
                throw new IOException("导出音频的格式或长度不符合预期，没有保存不完整文件。");
            string name = Path.GetFileNameWithoutExtension(midiPath);
            string target = Path.Combine(outputDirectory, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.wav");
            string pending = target + ".partial";
            try
            {
                File.Copy(wave, pending, false);
                token.ThrowIfCancellationRequested();
                File.Move(pending, target, false);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            return target;
        }
        finally
        {
            lifetime.Cancel();
            if (render != null) { try { await render; } catch { } }
            try { await engine.Call("midiStop"); await engine.Call("panic"); } catch { }
            try { Directory.Delete(session, true); } catch { }
        }
    }

    async Task SnapshotAsync(string destination)
    {
        string name = "capture-" + Guid.NewGuid().ToString("N");
        const string bank = "Nocturne Recordings";
        // SavePreset does not change the selected preset. Only our unique temporary preset is removed.
        try
        {
            await engine.Call("savePreset", new { name, bank });
            var presets = await engine.Call("getListOfPresets");
            var preset = presets.EnumerateArray().First(p => p.GetProperty("name").GetString() == name && p.GetProperty("bank").GetString() == bank);
            File.Copy(preset.GetProperty("file").GetString()!, destination);
        }
        finally { await engine.Call("deletePreset", new { name, bank }); }
    }

    internal static async Task RenderAsync(string executable, string preset, string midi, string wave,
        string session, double duration, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "--headless", "--prefs", Path.Combine(session, "renderer.prefs"),
            "--fxp", preset, "--midi", midi, "--wav", wave, "--rate", "48000", "--bit-depth", "24" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Pianoteq 音频导出进程。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, duration * 3 + 30)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            throw new IOException("Pianoteq 导出超时，请重试。");
        }
        string diagnostics = (await stderr) + (await stdout);
        if (process.ExitCode != 0 || !File.Exists(wave))
            throw new IOException("Pianoteq 导出失败：" + diagnostics[..Math.Min(600, diagnostics.Length)]);
    }

    public static WaveInfo InspectWave(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (Tag() != "RIFF") throw new InvalidDataException("导出文件不是 WAV。");
        reader.ReadUInt32();
        if (Tag() != "WAVE") throw new InvalidDataException("导出文件不是 WAV。");
        int channels = 0, rate = 0, bits = 0, alignment = 0;
        long dataSize = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string tag = Tag(); uint size = reader.ReadUInt32();
            long next = reader.BaseStream.Position + size;
            if (next > reader.BaseStream.Length) throw new InvalidDataException("WAV 数据不完整。");
            if (tag == "fmt " && size >= 16)
            {
                int format = reader.ReadUInt16();
                if (format != 1 && format != 0xfffe) throw new InvalidDataException("WAV 不是 PCM 格式。");
                channels = reader.ReadUInt16(); rate = reader.ReadInt32(); reader.ReadUInt32();
                alignment = reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            if (tag == "data") dataSize += size;
            reader.BaseStream.Position = next + (size & 1);
        }
        if (channels < 1 || rate < 1 || bits < 1 || alignment != channels * ((bits + 7) / 8) || dataSize == 0 || dataSize % alignment != 0)
            throw new InvalidDataException("WAV 音频内容无效或为空。");
        return new(channels, rate, bits, (double)dataSize / alignment / rate);
    }
}
