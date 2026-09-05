using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
namespace Nocturne;
public sealed class Engine : IDisposable
{
    readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    readonly SemaphoreSlim midiGate = new(1, 1);
    long id;
    public async Task<JsonElement> Call(string method, object? args = null)
    {
        using var response = await http.PostAsync("http://127.0.0.1:18981/jsonrpc", new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref id), method, @params = args ?? Array.Empty<object>() }), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }
    public static JsonElement First(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e[0] : e;
    public async Task Connect(string path)
    {
        try { var info = First(await Call("getInfo")); if (info.GetProperty("product_name").GetString() == "Pianoteq") return; } catch { }
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 Pianoteq，请在设置中选择程序路径。", path);
        Process.Start(new ProcessStartInfo(path, "--serve 127.0.0.1:18981") { UseShellExecute = true });
        for (int i = 0; i < 30; i++) { await Task.Delay(350); try { await Call("getInfo"); return; } catch { } }
        throw new IOException("连接 Pianoteq 超时。请确认程序已打开，且端口 18981 可用，然后点击重新连接。");
    }
    public async Task Send(params int[] bytes) { await midiGate.WaitAsync(); try { await Call("midiSend", new { bytes }); } finally { midiGate.Release(); } }
    public void Dispose() => http.Dispose();
}
public sealed class Settings
{
    public string PianoteqPath { get; set; } = @"D:\Music\Modartt\Pianoteq 9\Pianoteq 9.exe";
    public int Velocity { get; set; } = 88;
    public int Transpose { get; set; }
    public Dictionary<string, int> Mapping { get; set; } = Defaults();
    public static Dictionary<string, int> Defaults()
    {
        string[] keys = { "A", "W", "S", "E", "D", "F", "T", "G", "Y", "H", "U", "J", "K", "O", "L", "P", "OemSemicolon", "OemQuotes", "OemCloseBrackets", "OemQuestion" };
        return keys.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => 60 + p.i);
    }
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string FilePath => Path.Combine(Root, "settings.json");
    public static Settings Load() { try { var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); s.Velocity = Math.Clamp(s.Velocity, 1, 127); s.Transpose = Math.Clamp(s.Transpose, -24, 24); s.Mapping = s.Mapping.Where(x => Enum.TryParse<Key>(x.Key, out var k) && Allowed(k) && x.Value >= 21 && x.Value <= 108).ToDictionary(x => x.Key, x => x.Value); return s; } catch { return new(); } }
    public void Save() { var temp = FilePath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, FilePath, true); }
    public static bool Allowed(Key k) => k >= Key.A && k <= Key.Z || k >= Key.D0 && k <= Key.D9 || k >= Key.NumPad0 && k <= Key.NumPad9 || new[] { Key.OemSemicolon, Key.OemQuotes, Key.OemOpenBrackets, Key.OemCloseBrackets, Key.OemComma, Key.OemPeriod, Key.OemQuestion, Key.OemMinus, Key.OemPlus, Key.OemBackslash, Key.OemPipe }.Contains(k);
    public static string Label(string key) => key switch { "OemSemicolon" => ";", "OemQuotes" => "'", "OemCloseBrackets" => "]", "OemOpenBrackets" => "[", "OemQuestion" => "/", "OemComma" => ",", "OemPeriod" => ".", "OemMinus" => "−", "OemPlus" => "=", _ => key.StartsWith("D") && key.Length == 2 ? key[1..] : key };
    public static string Note(int n) => new[] { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" }[n % 12] + (n / 12 - 1);
}
