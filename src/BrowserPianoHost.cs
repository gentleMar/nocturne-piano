using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Nocturne;

public sealed class BrowserPianoOptions
{
    public int Port { get; set; } = 18982;
    public bool AllowLan { get; set; }
    public string StunUrl { get; set; } = "stun:stun.cloudflare.com:3478";
    public string TurnUrl { get; set; } = "";
    public string TurnUser { get; set; } = "";
    public string TurnPassword { get; set; } = "";
    public static string PathName => Path.Combine(Settings.Root, "remote-settings.json");
    public static BrowserPianoOptions Load()
    {
        try { return JsonSerializer.Deserialize<BrowserPianoOptions>(File.ReadAllText(PathName)) ?? new(); }
        catch { return new(); }
    }
    public void Save() => File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    public void Validate()
    {
        if (Port < 1024 || Port > 65535 || Port == 18981) throw new ArgumentException("共享端口请选择 1024–65535，且不能使用音源端口 18981。");
        if (StunUrl.Length > 512 || TurnUrl.Length > 512 || TurnUser.Length > 256 || TurnPassword.Length > 1024)
            throw new ArgumentException("中继配置过长。");
        if (StunUrl != "" && !StunUrl.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("STUN 地址应以 stun: 开头。");
        if (TurnUrl != "" && !TurnUrl.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) && !TurnUrl.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("TURN 地址应以 turn: 或 turns: 开头。");
    }
}

/// <summary>Small authenticated performance surface. No file, transport, preset or RPC proxy endpoints.</summary>
public sealed class BrowserPianoHost : IAsyncDisposable
{
    readonly Engine engine;
    readonly Func<int> velocity;
    readonly ConcurrentDictionary<Guid, Peer> peers = new();
    readonly SemaphoreSlim slots = new(8, 8), notesGate = new(1, 1);
    readonly Dictionary<int, int> notes = new();
    readonly CancellationTokenSource stop = new();
    readonly Channel<byte[]> audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    WebApplication? app;
    ProcessAudioCapture? capture;
    Task? audioTask, watchdog;
    volatile bool inputEnabled = true;
    int disposed;
    public BrowserPianoOptions Options { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public string LocalUrl => $"http://127.0.0.1:{Options.Port}/#key={Token}";
    public int ClientCount => peers.Values.Count(p => p.Authenticated);
    public string? LastError { get; private set; }
    public BrowserPianoHost(Engine engine, Func<int> velocity, BrowserPianoOptions options)
    { this.engine = engine; this.velocity = velocity; Options = options; }

    public async Task StartAsync()
    {
        Options.Validate();
        capture = new ProcessAudioCapture();
        capture.Frame += frame => audio.Writer.TryWrite(frame);
        capture.Failed += ex => { LastError = "音频采集已停止：" + ex.Message; foreach (var p in peers.Values) p.Signal(new { type = "error", message = "主机音频已中断，请联系主机重新开启共享。" }); };
        await capture.StartAsync(ProcessAudioCapture.FindPianoteqProcess());
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 0;
            server.Limits.MaxConcurrentConnections = 32;
            server.Limits.MaxConcurrentUpgradedConnections = 8;
            server.Listen(Options.AllowLan ? IPAddress.Any : IPAddress.Loopback, Options.Port);
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Permissions-Policy"] = "microphone=(), camera=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; media-src 'self' blob:; worker-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            await next(context);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        foreach (var asset in new[] { ("/", "index.html", "text/html; charset=utf-8"), ("/piano.css", "piano.css", "text/css"), ("/piano.js", "piano.js", "text/javascript"), ("/audio-worklet.js", "audio-worklet.js", "text/javascript") })
        {
            app.MapGet(asset.Item1, async context =>
            {
                context.Response.ContentType = asset.Item3;
                using var stream = typeof(BrowserPianoHost).Assembly.GetManifestResourceStream("Nocturne.RemoteWeb." + asset.Item2)!;
                await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
            });
        }
        app.MapGet("/ws", AcceptAsync);
        await app.StartAsync(stop.Token);
        audioTask = Task.Run(EncodeAsync);
        watchdog = Task.Run(WatchdogAsync);
    }

    async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        // Require same-origin browser connections; possession of the fragment key is still mandatory.
        if (!Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 403; return; }
        if (!await slots.WaitAsync(0)) { context.Response.StatusCode = 503; return; }
        Peer? peer = null;
        try
        {
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            peer = new Peer(this, ws); peers[peer.Id] = peer;
            await peer.RunAsync();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException) { }
        finally
        {
            if (peer != null) { peers.TryRemove(peer.Id, out _); await peer.DisposeAsync(); }
            slots.Release();
        }
    }

    async Task EncodeAsync()
    {
        try
        {
            using var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            encoder.Bitrate = 192000; encoder.Complexity = 5;
            short[] pcm = new short[960]; byte[] encoded = new byte[4000];
            await foreach (byte[] frame in audio.Reader.ReadAllAsync(stop.Token))
            {
                var listeners = peers.Values.Where(p => p.Authenticated).ToArray();
                if (listeners.Length == 0) continue;
                Buffer.BlockCopy(frame, 0, pcm, 0, frame.Length);
                byte[]? opus = null;
                if (listeners.Any(p => p.RtcConnected))
                { int length = encoder.Encode(pcm.AsSpan(), 480, encoded.AsSpan(), encoded.Length); opus = encoded[..length]; }
                foreach (var p in listeners) p.SendAudio(frame, opus);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = "音频传输已停止：" + ex.Message; foreach (var p in peers.Values) p.Signal(new { type = "error", message = "音频传输已停止，请联系主机。" }); }
    }

    async Task WatchdogAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(stop.Token))
                foreach (var p in peers.Values)
                    if (Environment.TickCount64 - p.LastNotes > 1200) await ApplyNotesAsync(p, Array.Empty<int>());
        }
        catch (OperationCanceledException) { }
    }

    async Task ApplyNotesAsync(Peer peer, int[] desired)
    {
        await notesGate.WaitAsync();
        try
        {
            var target = inputEnabled && !peer.Closed ? desired.ToHashSet() : new HashSet<int>();
            foreach (int n in peer.Held.Except(target).ToArray())
            {
                if (notes.TryGetValue(n, out int count) && count > 1) notes[n] = count - 1;
                else { notes.Remove(n); await engine.Send(0x8F, n, 0); }
                peer.Held.Remove(n);
            }
            foreach (int n in target.Except(peer.Held).ToArray())
            {
                notes.TryGetValue(n, out int count);
                if (count == 0) await engine.Send(0x9F, n, Math.Clamp(velocity(), 1, 127));
                notes[n] = count + 1; peer.Held.Add(n);
            }
        }
        catch (Exception ex) { LastError = "音源控制失败：" + ex.Message; }
        finally { notesGate.Release(); }
    }

    public async Task SetInputEnabledAsync(bool enabled)
    {
        inputEnabled = enabled;
        foreach (var p in peers.Values)
        {
            p.Signal(new { type = "input", enabled });
            if (!enabled) await ApplyNotesAsync(p, Array.Empty<int>());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        if (capture != null) await capture.DisposeAsync();
        foreach (var p in peers.Values) p.Abort();
        if (app != null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { } await app.DisposeAsync(); }
        if (audioTask != null) await audioTask;
        if (watchdog != null) await watchdog;
        stop.Dispose();
    }

    sealed class Peer : IAsyncDisposable
    {
        readonly BrowserPianoHost owner;
        readonly WebSocket ws;
        readonly CancellationTokenSource cancellation;
        readonly Channel<(byte[] Data, WebSocketMessageType Type)> outbound = Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        readonly Channel<int[]> keyStates = Channel.CreateBounded<int[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        readonly object messageLock = new();
        RTCPeerConnection? pc;
        Task? sender, keys;
        long sequence = -1, rateStart = Environment.TickCount64;
        int messages;
        volatile bool fallback = true;
        public Guid Id { get; } = Guid.NewGuid();
        public bool Authenticated { get; private set; }
        public bool Closed { get; private set; }
        public long LastNotes = Environment.TickCount64;
        public HashSet<int> Held { get; } = new();
        public bool RtcConnected => pc?.connectionState == RTCPeerConnectionState.connected;

        public Peer(BrowserPianoHost owner, WebSocket ws)
        { this.owner = owner; this.ws = ws; cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.stop.Token); }
        public void Abort() { try { cancellation.Cancel(); ws.Abort(); } catch (ObjectDisposedException) { } }
        public void Signal(object message)
        {
            if (Closed) return;
            if (!outbound.Writer.TryWrite((JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text))) Abort();
        }
        public void SendAudio(byte[] pcm, byte[]? opus)
        {
            try { if (opus != null && RtcConnected) pc!.SendAudio(480, opus); } catch { }
            // Never queue more than 30 ms of fallback audio. A stalled TCP write closes the peer.
            if (fallback && outbound.Reader.Count < 3) outbound.Writer.TryWrite((pcm, WebSocketMessageType.Binary));
        }
        async Task SendAsync()
        {
            await foreach (var item in outbound.Reader.ReadAllAsync(cancellation.Token))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await ws.SendAsync(item.Data, item.Type, true, deadline.Token);
            }
        }
        async Task<string> ReceiveAsync(CancellationToken token)
        {
            byte[] buffer = new byte[32768]; int used = 0;
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(used), token);
                if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Text messages required");
                used += result.Count;
                if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer, 0, used);
                if (used == buffer.Length) throw new WebSocketException("Message too large");
            }
        }
        public async Task RunAsync()
        {
            using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                authTimeout.CancelAfter(5000);
                using var auth = JsonDocument.Parse(await ReceiveAsync(authTimeout.Token));
                var root = auth.RootElement;
                if (!root.TryGetProperty("type", out var kind) || kind.GetString() != "auth" ||
                    !root.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key.GetString()!), Encoding.UTF8.GetBytes(owner.Token))) return;
            }
            Authenticated = true;
            sender = SendAsync();
            _ = sender.ContinueWith(_ => Abort(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            keys = Task.Run(async () => { await foreach (int[] state in keyStates.Reader.ReadAllAsync(cancellation.Token)) await owner.ApplyNotesAsync(this, state); });
            var ice = new List<object>();
            if (owner.Options.StunUrl != "") ice.Add(new { urls = owner.Options.StunUrl });
            if (owner.Options.TurnUrl != "") ice.Add(new { urls = owner.Options.TurnUrl, username = owner.Options.TurnUser, credential = owner.Options.TurnPassword });
            Signal(new { type = "ready", iceServers = ice, enabled = owner.inputEnabled, rate = 48000, channels = 2 });
            while (!cancellation.IsCancellationRequested)
            {
                string json = await ReceiveAsync(cancellation.Token);
                using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
                string? type = root.GetProperty("type").GetString();
                if (!RateAllowed()) throw new WebSocketException("Rate exceeded");
                switch (type)
                {
                    case "notes": ReadNotes(root); break;
                    case "ping": Signal(new { type = "pong", time = root.GetProperty("time").GetDouble() }); break;
                    case "fallback": fallback = root.GetProperty("enabled").GetBoolean(); break;
                    case "offer": await OfferAsync(root.GetProperty("sdp").GetString()!); break;
                    case "ice": if (pc != null) pc.addIceCandidate(JsonSerializer.Deserialize<RTCIceCandidateInit>(root.GetProperty("candidate").GetRawText())!); break;
                    default: throw new WebSocketException("Unknown message");
                }
            }
        }
        bool RateAllowed()
        {
            lock (messageLock)
            {
                long now = Environment.TickCount64;
                if (now - rateStart >= 1000) { rateStart = now; messages = 0; }
                return ++messages <= 180;
            }
        }
        void ReadNotes(JsonElement root)
        {
            long next = root.GetProperty("seq").GetInt64();
            int[] state = root.GetProperty("notes").EnumerateArray().Select(n => n.GetInt32()).ToArray();
            if (state.Length > 24 || state.Any(n => n < 21 || n > 108)) throw new ArgumentException("Invalid notes");
            lock (messageLock)
            {
                if (next <= sequence) return;
                sequence = next; LastNotes = Environment.TickCount64; keyStates.Writer.TryWrite(state);
            }
        }
        async Task OfferAsync(string sdp)
        {
            if (pc != null) throw new InvalidOperationException("Already negotiated");
            var servers = new List<RTCIceServer>();
            if (owner.Options.StunUrl != "") servers.Add(new RTCIceServer { urls = owner.Options.StunUrl });
            if (owner.Options.TurnUrl != "") servers.Add(new RTCIceServer { urls = owner.Options.TurnUrl, username = owner.Options.TurnUser, credential = owner.Options.TurnPassword });
            pc = new RTCPeerConnection(new RTCConfiguration { iceServers = servers });
            pc.addTrack(new MediaStreamTrack(new List<AudioFormat> { new(111, "opus", 48000, 2, "minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxaveragebitrate=192000") }, MediaStreamStatusEnum.SendOnly));
            pc.onicecandidate += candidate => { if (candidate != null) Signal(new { type = "ice", candidate = candidate.toJSON() }); };
            pc.ondatachannel += channel => channel.onmessage += (_, _, data) =>
            {
                try
                {
                    if (data.Length > 1024 || !RateAllowed()) { Abort(); return; }
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.GetProperty("type").GetString() == "notes") ReadNotes(doc.RootElement);
                }
                catch { Abort(); }
            };
            if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp }) != SetDescriptionResultEnum.OK)
                throw new ArgumentException("Invalid SDP");
            var answer = pc.createAnswer(null);
            await pc.setLocalDescription(answer);
            Signal(new { type = "answer", sdp = answer.sdp });
        }
        public async ValueTask DisposeAsync()
        {
            Closed = true; cancellation.Cancel(); pc?.Close("Session ended"); pc?.Dispose();
            if (sender != null) { try { await sender; } catch { } }
            if (keys != null) { try { await keys; } catch { } }
            await owner.ApplyNotesAsync(this, Array.Empty<int>());
            cancellation.Dispose();
        }
    }
}
