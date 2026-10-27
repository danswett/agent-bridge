// Spike S2 driver.
//   vectors <out.json>          fixed-key XX handshake + transport messages, for the JS
//                               implementation to reproduce byte for byte
//   serve --port N --web <dir>  static page + WebSocket responder for live interop
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using S2.Noise;

static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

var mode = args.Length > 0 ? args[0] : "";
if (mode == "vectors")
{
    var prologue = Encoding.UTF8.GetBytes("agent-bridge-s2-vectors");
    using var iS = P256KeyPair.Generate();
    using var iE = P256KeyPair.Generate();
    using var rS = P256KeyPair.Generate();
    using var rE = P256KeyPair.Generate();
    var ini = new XXHandshake(true, iS, prologue, iE);
    var res = new XXHandshake(false, rS, prologue, rE);

    var handshake = new List<object>();
    void Hs(XXHandshake w, XXHandshake r, string from, byte[] payload)
    {
        var m = w.WriteMessage(payload);
        var got = r.ReadMessage(m);
        if (!got.AsSpan().SequenceEqual(payload)) { throw new Exception("self-check failed"); }
        handshake.Add(new { from, payload = Hex(payload), message = Hex(m) });
    }
    Hs(ini, res, "i", Encoding.UTF8.GetBytes("hello from the initiator"));
    Hs(res, ini, "r", Encoding.UTF8.GetBytes("{\"host\":\"s2\",\"features\":[\"blobs\"]}"));
    Hs(ini, res, "i", []);
    if (!ini.HandshakeHash.AsSpan().SequenceEqual(res.HandshakeHash)) { throw new Exception("hash mismatch"); }
    if (!ini.RemoteStatic!.AsSpan().SequenceEqual(rS.Public) || !res.RemoteStatic!.AsSpan().SequenceEqual(iS.Public)) { throw new Exception("static mismatch"); }

    var transport = new List<object>();
    var big = new byte[60000];
    RandomNumberGenerator.Fill(big);
    foreach (var (from, payload) in new[] {
        ("i", Encoding.UTF8.GetBytes("café – 世界 ✓ “smart” 🚀")), ("r", Encoding.UTF8.GetBytes("ack")), ("i", big), ("r", Array.Empty<byte>()), ("r", Encoding.UTF8.GetBytes("second in a row")) })
    {
        var (w, r) = from == "i" ? (ini, res) : (res, ini);
        var m = w.Send!.EncryptWithAd([], payload);
        var got = r.Receive!.DecryptWithAd([], m);
        if (!got.AsSpan().SequenceEqual(payload)) { throw new Exception("transport self-check failed"); }
        transport.Add(new { from, payload = Hex(payload), message = Hex(m) });
    }

    object K(P256KeyPair k) { var (d, x, y) = k.Export(); return new { d = Hex(d), x = Hex(x), y = Hex(y), pub = Hex(k.Public) }; }
    var doc = new
    {
        protocol = NoiseConst.ProtocolName,
        prologue = Hex(prologue),
        keys = new { initStatic = K(iS), initEphemeral = K(iE), respStatic = K(rS), respEphemeral = K(rE) },
        handshake,
        handshakeHash = Hex(ini.HandshakeHash),
        transport,
    };
    File.WriteAllText(args[1], JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"wrote {args[1]}");
    return 0;
}

if (mode == "serve")
{
    int port = int.Parse(Arg("--port") ?? "8765");
    var web = Path.GetFullPath(Arg("--web") ?? ".");
    using var serverStatic = P256KeyPair.Generate();
    var prologue = Encoding.UTF8.GetBytes("agent-bridge-s2-live");

    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    app.UseWebSockets();
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(web) });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(web),
        ServeUnknownFileTypes = true,
    });
    // Spike only: hands out the key a client pins. In the product the pin comes from pairing.
    app.MapGet("/pin", () => Hex(serverStatic.Public));
    app.Map("/ws", async (HttpContext ctx) =>
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var hs = new XXHandshake(false, serverStatic, prologue);
        try
        {
            hs.ReadMessage(await Receive(ws));
            await SendBin(ws, hs.WriteMessage(Encoding.UTF8.GetBytes("{\"host\":\"s2-live\"}")));
            hs.ReadMessage(await Receive(ws));
            Console.WriteLine($"handshake ok, client static {Hex(hs.RemoteStatic!)[..16]}..., h={Hex(hs.HandshakeHash)[..16]}...");
            while (ws.State == WebSocketState.Open)
            {
                var frame = await Receive(ws);
                if (frame.Length == 0 && ws.State != WebSocketState.Open) { break; }
                byte[] plain;
                try { plain = hs.Receive!.DecryptWithAd([], frame); }
                catch (CryptographicException)
                {
                    Console.WriteLine("transport frame failed authentication; closing 4001");
                    await ws.CloseAsync((WebSocketCloseStatus)4001, "decrypt-failed", default);
                    return;
                }
                await SendBin(ws, hs.Send!.EncryptWithAd([], plain)); // echo
            }
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            Console.WriteLine($"handshake failed: {ex.Message}");
            if (ws.State == WebSocketState.Open) { await ws.CloseAsync((WebSocketCloseStatus)4002, "handshake-failed", default); }
        }
        catch (WebSocketException) { }
    });
    Console.WriteLine($"serving {web} on http://127.0.0.1:{port}  (static {Hex(serverStatic.Public)[..16]}...)");
    await app.RunAsync();
    return 0;
}

Console.Error.WriteLine("usage: vectors <out.json> | serve --port N --web <dir>");
return 2;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<byte[]> Receive(WebSocket ws)
{
    var buffer = new byte[70000];
    using var ms = new MemoryStream();
    WebSocketReceiveResult r;
    do
    {
        r = await ws.ReceiveAsync(buffer, default);
        if (r.MessageType == WebSocketMessageType.Close) { return []; }
        ms.Write(buffer, 0, r.Count);
        if (ms.Length > NoiseConst.MaxMessage) { throw new InvalidOperationException("frame too long"); }
    } while (!r.EndOfMessage);
    return ms.ToArray();
}

static Task SendBin(WebSocket ws, byte[] data) => ws.SendAsync(data, WebSocketMessageType.Binary, true, default);
