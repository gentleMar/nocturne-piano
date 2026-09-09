using Nocturne;
using System.IO;
using System.Text.Json;
using System.Reflection;

using var engine = new Engine();
await engine.Connect(Environment.GetEnvironmentVariable("PIANOTEQ_PATH") ?? @"D:\Music\Modartt\Pianoteq 9\Pianoteq 9.exe");
await engine.Call("midiStop");
await engine.Call("panic");
Console.WriteLine(await engine.Call("getAudioDeviceInfo"));
Console.WriteLine(await engine.Call("getInfo"));
foreach(var parameter in (await engine.Call("getParameters")).EnumerateArray())
    if(parameter.GetProperty("id").GetString()!.Contains("volume",StringComparison.OrdinalIgnoreCase)) Console.WriteLine(parameter);
if (args.Contains("--capture"))
{
    await using var capture = new ProcessAudioCapture();
    int count=0, peak=0;
    capture.Frame += frame => { Interlocked.Increment(ref count); for(int i=0;i<frame.Length;i+=2) peak=Math.Max(peak,Math.Abs((int)BitConverter.ToInt16(frame,i))); };
    capture.Failed += ex => Console.WriteLine(ex);
    await capture.StartAsync(ProcessAudioCapture.FindPianoteqProcess());
    await engine.Send(0x90,60,88); await Task.Delay(1500); await engine.Send(0x80,60,0); await Task.Delay(500);
    Console.WriteLine($"CAPTURE frames={count} peak={peak}");
    if(count<20 || peak<100) throw new Exception("No piano audio captured");
    return;
}
if (args.Contains("--isolation"))
{
    await Task.Delay(3000);
    using var wave = new MemoryStream(); using (var writer = new BinaryWriter(wave,System.Text.Encoding.UTF8,true))
    {
        writer.Write("RIFF"u8); writer.Write(36+48000*4); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(48000*4);
        for(int i=0;i<96000;i++) writer.Write((short)(12000*Math.Sin(2*Math.PI*1000*i/48000)));
    }
    wave.Position=0;
    int isolatedPeak=0, globalPeak=0;
    await using var isolated=new ProcessAudioCapture();
    using var global=new NAudio.Wave.WasapiLoopbackCapture();
    isolated.Frame+=data=>{for(int i=0;i<data.Length;i+=2) isolatedPeak=Math.Max(isolatedPeak,Math.Abs((int)BitConverter.ToInt16(data,i)));};
    global.DataAvailable+=(_,e)=>{for(int i=0;i<e.BytesRecorded;i+=4)globalPeak=Math.Max(globalPeak,(int)(Math.Abs(BitConverter.ToSingle(e.Buffer,i))*32767));};
    await isolated.StartAsync(ProcessAudioCapture.FindPianoteqProcess()); global.StartRecording();
    using var player=new System.Media.SoundPlayer(wave); await Task.Run(()=>player.PlaySync()); await Task.Delay(100);
    global.StopRecording();
    Console.WriteLine($"ISOLATION targetPeak={isolatedPeak} systemPeak={globalPeak}");
    if(isolatedPeak>20 || globalPeak<500) throw new Exception("Process isolation failed");
    return;
}
await using var host = new BrowserPianoHost(engine,()=>88,new BrowserPianoOptions { Port=18982, StunUrl="" });
await host.StartAsync();
await File.WriteAllTextAsync("host.json",JsonSerializer.Serialize(new { url=host.LocalUrl }));
Console.WriteLine("HOST READY");
for(int i=0;i<1200 && !File.Exists("stop");i++)
{
    if(File.Exists("disable")) { File.Delete("disable"); await host.SetInputEnabledAsync(false); }
    if(File.Exists("enable")) { File.Delete("enable"); await host.SetInputEnabledAsync(true); }
    var gate=(SemaphoreSlim)typeof(BrowserPianoHost).GetField("notesGate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(host)!;
    await gate.WaitAsync();
    try { await File.WriteAllTextAsync("state.json",JsonSerializer.Serialize(new {clients=host.ClientCount,notes=(Dictionary<int,int>)typeof(BrowserPianoHost).GetField("notes",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(host)!})); }
    finally { gate.Release(); }
    await Task.Delay(500);
}
Console.WriteLine("HOST STOPPED " + host.LastError);
