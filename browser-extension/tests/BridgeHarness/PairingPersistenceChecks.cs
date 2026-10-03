using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ScreenNote;

internal static class PairingPersistenceChecks
{
    public static async Task Run()
    {
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), ".build", "pairing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var file = Path.Combine(fixture, "browser-pairing.json");
        var extension = new string('a', 32);
        string token;
        try
        {
            using (var initial = Start(file))
            {
                var pair = await Post(initial, "/pair", new { code = initial.PairCode });
                Require(pair.Status == HttpStatusCode.OK, "Persistent pairing is committed before success.");
                token = pair.Data.GetProperty("token").GetString()!;
                var stored = File.ReadAllText(file);
                Require(!stored.Contains(token) && !stored.Contains(extension), "On-disk pairing stores ciphertext, not the token or extension ID.");
                var credential = new PairingStore(file).Load();
                Require(credential?.Token == token && credential.ExtensionId == extension, "Current Windows user can recover the DPAPI credential.");
            }
            Require(File.Exists(file), "Desktop exit preserves the pairing credential.");
            using (var restored = Start(file))
            {
                Require(restored.HasPairing && !restored.IsConnected, "Loaded pairing remains authorized without falsely claiming a live browser connection.");
                Require((await Post(restored, "/status", new { }, token)).Status == HttpStatusCode.OK, "Desktop restart accepts the original saved browser credential.");
                Require((await Post(restored, "/status", new { }, token, new string('b', 32))).Status == HttpStatusCode.Unauthorized, "Restored credential remains bound to the same extension.");
                Require((await Post(restored, "/pair", new { code = restored.PairCode })).Status == HttpStatusCode.Conflict, "Restart does not silently replace an existing pairing.");
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Origin", $"chrome-extension://{extension}");
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{restored.ListeningPort}/events"), CancellationToken.None);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "auth", token, extensionId = extension }).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
                Require((await Event(socket)).GetProperty("type").GetString() == "ready", "Saved credential restores the real-time event channel after desktop restart.");
                var send = restored.SendTextAsync("isolated restart fixture");
                Require((await Event(socket)).GetProperty("type").GetString() == "job", "Restored real-time channel still signals a new draft without automatic submission.");
                var job = (await Post(restored, "/poll", new { }, token)).Data.GetProperty("job");
                await Post(restored, "/ack", new { id = job.GetProperty("id").GetString(), success = true }, token);
                Require((await send).Success, "Restored pairing keeps explicit claim and acknowledgement semantics.");
                socket.Abort();

                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failed = false;
                    try { restored.ResetPairing(); } catch (IOException) { failed = true; }
                    Require(failed, "Failed deletion reports an error instead of claiming reset.");
                    Require((await Post(restored, "/status", new { }, token)).Status == HttpStatusCode.OK, "Failed reset retains the live credential.");
                    Require((await Post(restored, "/disconnect", new { }, token)).Status == HttpStatusCode.InternalServerError, "Failed HTTP disconnect reports failed durable revocation.");
                    Require((await Post(restored, "/status", new { }, token)).Status == HttpStatusCode.OK, "Failed HTTP disconnect does not silently discard authorization.");
                }
                restored.ResetPairing();
                Require(!restored.HasPairing && !File.Exists(file), "Successful reset deletes the saved credential and clears remembered authorization.");
                Require((await Post(restored, "/status", new { }, token)).Status == HttpStatusCode.Unauthorized, "Successful reset immediately rejects the old token.");
            }
            using (var resetRestart = Start(file))
            {
                Require((await Post(resetRestart, "/status", new { }, token)).Status == HttpStatusCode.Unauthorized, "Revoked credential stays revoked after restart.");
                token = (await Post(resetRestart, "/pair", new { code = resetRestart.PairCode })).Data.GetProperty("token").GetString()!;
                Require((await Post(resetRestart, "/disconnect", new { }, token)).Status == HttpStatusCode.OK && !File.Exists(file), "Explicit disconnect removes the persistent credential.");
            }
            using (var disconnectRestart = Start(file))
                Require((await Post(disconnectRestart, "/status", new { }, token)).Status == HttpStatusCode.Unauthorized, "Disconnected pairing never returns after a later restart.");

            var blockedFile = Path.Combine(fixture, "directory-as-file");
            Directory.CreateDirectory(blockedFile);
            using (var blocked = Start(blockedFile))
            {
                Require((await Post(blocked, "/pair", new { code = blocked.PairCode })).Status == HttpStatusCode.InternalServerError, "Credential write failure does not report paired success.");
                Require(!(await blocked.SendTextAsync("not paired")).Success, "Failed save never publishes an in-memory pairing.");
            }
            Require(!Directory.EnumerateFiles(fixture, "*.tmp").Any(), "Failed save cleans up temporary encrypted files.");

            var store = new PairingStore(file);
            store.Save(new PairingStore.Credential(token, extension));
            using (var json = JsonDocument.Parse(File.ReadAllText(file)))
            {
                var bytes = Convert.FromBase64String(json.RootElement.GetProperty("ProtectedData").GetString()!);
                bytes[bytes.Length / 2] ^= 0x5A;
                File.WriteAllText(file, JsonSerializer.Serialize(new { Version = 1, ProtectedData = Convert.ToBase64String(bytes) }));
            }
            using (var corrupt = Start(file))
            {
                Require((await Post(corrupt, "/status", new { }, token)).Status == HttpStatusCode.Unauthorized, "Corrupted protected data fails closed instead of authenticating.");
                Require(File.Exists(file) && corrupt.Status.Contains("无法读取"), "Unreadable credential is reported and is not silently deleted.");
                corrupt.ResetPairing();
                Require(!File.Exists(file), "User reset can revoke an unreadable saved credential.");
            }
            Console.WriteLine("PASS: isolated DPAPI pairing persistence, restart, WebSocket restoration, durable reset/disconnect, storage failures, corruption.");
        }
        finally { Directory.Delete(fixture, recursive: true); }

        AiBridgeService Start(string pairingFile)
        {
            var service = new AiBridgeService(0, pairingFile, openTarget: _ => { });
            service.Start();
            return service;
        }

        async Task<(HttpStatusCode Status, JsonElement Data)> Post(AiBridgeService service, string path, object body, string? credential = null, string? id = null)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{service.ListeningPort}{path}") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            request.Headers.Add("X-ScreenNote-Extension", id ?? extension);
            request.Headers.Add("Origin", $"chrome-extension://{id ?? extension}");
            if (credential is not null) request.Headers.Authorization = new("Bearer", credential);
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }
    }

    private static async Task<JsonElement> Event(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new byte[4096];
        var count = 0;
        ValueWebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(bytes.AsMemory(count), timeout.Token); count += result.Count; } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
        return json.RootElement.Clone();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }
}
