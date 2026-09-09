using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Nocturne;

/// <summary>Windows process loopback only. Never opens a system-wide loopback endpoint.</summary>
public sealed class ProcessAudioCapture : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    Task? worker;
    public event Action<byte[]>? Frame;
    public event Action<Exception>? Failed;
    public const int SampleRate = 48000, Channels = 2, FrameSamples = 480, FrameBytes = 1920;

    public static int FindPianoteqProcess()
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            int result = GetExtendedTcpTable(table, ref size, false, 2, 3, 0);
            if (result != 0) throw new InvalidOperationException("无法定位音源进程：" + result);
            for (int i = 0; i < Marshal.ReadInt32(table); i++)
            {
                IntPtr row = table + 4 + i * 24;
                int port = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                if (port != 18981) continue;
                int pid = Marshal.ReadInt32(row, 20);
                using var process = Process.GetProcessById(pid);
                if (!process.ProcessName.StartsWith("Pianoteq", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("音源端口所属进程不是 Pianoteq。");
                return pid;
            }
            throw new InvalidOperationException("请先连接 Pianoteq，再开启浏览器共享。");
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    public async Task StartAsync(int pid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new NotSupportedException("独立进程音频采集需要 Windows 11 或 Windows build 20348 以上。");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = Task.Factory.StartNew(() => Run(pid, started), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await started.Task;
    }

    void Run(int pid, TaskCompletionSource started)
    {
        try
        {
            using var client = Activate((uint)pid).GetAwaiter().GetResult();
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm,
                200000, 0, new WaveFormat(SampleRate, 16, Channels), Guid.Empty);
            var capture = client.AudioCaptureClient;
            byte[] frame = new byte[FrameBytes]; int used = 0;
            client.Start(); started.TrySetResult();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    while (capture.GetNextPacketSize() > 0)
                    {
                        IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags,
                            out _, out _);
                        try
                        {
                            int offset = 0, remaining = frames * 4;
                            while (remaining > 0)
                            {
                                int count = Math.Min(FrameBytes - used, remaining);
                                if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(frame, used, count);
                                else Marshal.Copy(data + offset, frame, used, count);
                                used += count; offset += count; remaining -= count;
                                if (used == FrameBytes) { Frame?.Invoke(frame); frame = new byte[FrameBytes]; used = 0; }
                            }
                        }
                        finally { capture.ReleaseBuffer(frames); }
                    }
                    stop.Token.WaitHandle.WaitOne(3);
                }
            }
            finally { client.Stop(); }
        }
        catch (Exception ex) { started.TrySetException(ex); if (!stop.IsCancellationRequested) Failed?.Invoke(ex); }
    }

    // The activation PROPVARIANT holds VT_BLOB / AUDIOCLIENT_ACTIVATION_PARAMS.
    // Keep both allocations and the COM callback alive until async activation completes.
    static async Task<AudioClient> Activate(uint pid)
    {
        IntPtr args = Marshal.AllocHGlobal(12), variant = Marshal.AllocHGlobal(24);
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var callback = new Completion();
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            Marshal.WriteInt32(args, 0, 1); Marshal.WriteInt32(args, 4, (int)pid);
            Marshal.WriteInt32(args, 8, 0); // Include target process tree.
            Marshal.WriteInt16(variant, 0, 65); Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, IntPtr.Size == 8 ? 16 : 12, args);
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid,
                variant, callback, out operation));
            return new AudioClient(await callback.Result.Task.ConfigureAwait(false));
        }
        finally
        {
            GC.KeepAlive(callback);
            if (operation != null) Marshal.ReleaseComObject(operation);
            Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(args);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgile
    {
        internal readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try { operation.GetActivateResult(out int hr, out object value); Marshal.ThrowExceptionForHR(hr); Result.TrySetResult((IAudioClient)value); }
            catch (Exception ex) { Result.TrySetException(ex); }
        }
    }
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgile { }
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, IntPtr activation,
        IActivateAudioInterfaceCompletionHandler completion, out IActivateAudioInterfaceAsyncOperation operation);
    [DllImport("iphlpapi.dll")] static extern int GetExtendedTcpTable(IntPtr table, ref int size,
        bool order, int family, int tableClass, uint reserved);

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); if (worker != null) await worker.ConfigureAwait(false); stop.Dispose();
    }
}
