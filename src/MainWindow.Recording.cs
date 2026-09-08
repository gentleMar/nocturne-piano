using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Nocturne;

public partial class MainWindow
{
    CancellationTokenSource? recordingCancellation;
    Task? recordingTask;
    bool RecordingBusy => recordingCancellation != null;
    string? lastRecording;

    async void RecordClick(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    async Task ToggleRecordingAsync()
    {
        if (RecordingBusy) { recordingCancellation!.Cancel(); return; }
        if (!connected || songPath == null) { Status.Text = "请先连接音源并选择一首 MIDI，再按 F9 录制。"; return; }
        recordingTask = RecordCurrentSongAsync(songPath);
        await recordingTask;
    }

    async Task RecordCurrentSongAsync(string path)
    {
        using var cancellation = new CancellationTokenSource();
        recordingCancellation = cancellation;
        SetRecordingControls(false);
        RecordButton.Content = "■ 取消录制 · F9";
        Status.Text = "正在准备录制 · 从头播放一次并自动保存 WAV…";
        loopArmed = false;
        volumeDebounce.Stop();
        try
        {
            // Let an already-running sequencer poll finish before the recording owns playback.
            while (polling) await Task.Delay(30, cancellation.Token);
            await Release();
            await engine.Call("setParameters", new { list = new[] { new { id = "volume", text = Volume.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) } } });
            var recorder = new MidiAudioRecorder(engine);
            var updates = new Progress<RecordingProgress>(p =>
            {
                if (!RecordingBusy) return;
                position = p.Position; duration = p.Duration; playing = p.Playing;
                clock.Restart(); Progress.Maximum = Math.Max(.01, duration); Status.Text = p.Message;
            });
            lastRecording = await recorder.RecordAsync(config.PianoteqPath, path,
                Path.Combine(Settings.Root, "Recordings"), updates, cancellation.Token);
            Status.Text = "录制完成 · " + lastRecording;
            Status.ToolTip = lastRecording;
        }
        catch (OperationCanceledException) { Status.Text = "录制已取消 · 未保存不完整音频"; }
        catch (Exception ex) { Status.Text = "录制失败：" + ex.Message; Status.ToolTip = ex.ToString(); }
        finally
        {
            playing = false; Piano.PlaybackActive = false; Piano.InvalidateVisual();
            recordingCancellation = null;
            RecordButton.Content = "● 录制整曲 · F9";
            SetRecordingControls(true);
        }
    }

    void SetRecordingControls(bool enabled)
    {
        HeaderControls.IsEnabled = enabled; InstrumentControls.IsEnabled = enabled;
        PianoControls.IsEnabled = enabled; TransportControls.IsEnabled = enabled;
        Progress.IsEnabled = enabled; AllowDrop = enabled;
    }

    void OpenRecordingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.Combine(Settings.Root, "Recordings");
            Directory.CreateDirectory(folder);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (lastRecording != null && File.Exists(lastRecording))
            {
                start.ArgumentList.Add("/select,"); start.ArgumentList.Add(lastRecording);
            }
            else start.ArgumentList.Add(folder);
            Process.Start(start);
        }
        catch (Exception ex) { Status.Text = "无法打开录音文件夹：" + ex.Message; }
    }
}
