using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScreenNote;

if (args.Contains("--launch-browser-check"))
{
    using var launchCheck = new AiBridgeService(0);
    launchCheck.Start();
    var result = await launchCheck.SendTextAsync("isolated launch check; never queued without pairing");
    Require(!result.Success && result.Message.Contains("已打开") && result.Message.Contains("首次配对"),
        "The real default-browser launcher opens the official page without sending an unpaired payload.");
    return;
}

using var service = new AiBridgeService(0, openTarget: _ => { });
service.Start();
Require(service.ListeningPort > 0 && !service.Status.Contains("占用"), "Harness uses an isolated ephemeral loopback port.");
using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{service.ListeningPort}"), Timeout = TimeSpan.FromSeconds(5) };
var extension = new string('a', 32);

async Task<(HttpStatusCode Status, JsonElement Data)> Post(string path, object body, string? token = null, string? origin = null, string? extensionId = null, string? host = null, bool retryEmpty = true, int retryCount = 0)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    request.Headers.Add("X-ScreenNote-Extension", extensionId ?? extension);
    request.Headers.Add("Origin", origin ?? $"chrome-extension://{extensionId ?? extension}");
    if (token is not null) request.Headers.Authorization = new("Bearer", token);
    if (host is not null) request.Headers.Host = host;
    using var response = await http.SendAsync(request);
    var data = await response.Content.ReadFromJsonAsync<JsonElement>();
    if (path == "/poll" && retryEmpty && retryCount < 100 && response.IsSuccessStatusCode && data.GetProperty("job").ValueKind == JsonValueKind.Null)
    {
        // Encoding is asynchronous; wait for publication, never sleep until an alarm.
        await Task.Delay(10);
        return await Post(path, body, token, origin, extensionId, host, retryEmpty, retryCount + 1);
    }
    return (response.StatusCode, data);
}

Require((await Post("/poll", new { })).Status == HttpStatusCode.Unauthorized, "Unauthenticated poll is rejected.");
Require((await Post("/pair", new { code = service.PairCode }, origin: "https://attacker.example")).Status == HttpStatusCode.Forbidden, "Website Origin cannot pair.");
Require((await Post("/pair", new { code = service.PairCode }, host: "attacker.example")).Status == HttpStatusCode.Forbidden, "Rebound Host cannot pair.");
var wrongCode = service.PairCode == "000000" ? "000001" : "000000";
Require((await Post("/pair", new { code = wrongCode })).Status == HttpStatusCode.Forbidden, "Wrong pairing code is rejected.");
var pair = await Post("/pair", new { code = service.PairCode });
Require(pair.Status == HttpStatusCode.OK, "Pairing succeeds.");
var token = pair.Data.GetProperty("token").GetString()!;
Require(token.Length == 64, "Pairing generates a 256-bit token.");
Require((await Post("/pair", new { code = service.PairCode })).Status == HttpStatusCode.Conflict, "Second extension cannot silently replace pairing.");
Require((await Post("/poll", new { }, "incorrect")).Status == HttpStatusCode.Unauthorized, "Wrong token is rejected.");
Require((await Post("/poll", new { }, token, extensionId: new string('b', 32))).Status == HttpStatusCode.Unauthorized, "Token is bound to extension ID.");

var send = service.SendTextAsync("第一行\nsecond line", "append");
Require(!(await service.SendTextAsync("second")).Success, "Concurrent delivery is rejected.");
var poll = await Post("/poll", new { }, token);
var job = poll.Data.GetProperty("job");
Require(job.GetProperty("text").GetString() == "第一行\nsecond line", "Text payload survives JSON and transport.");
Require((await Post("/poll", new { }, token, retryEmpty: false)).Data.GetProperty("job").ValueKind == JsonValueKind.Null, "Claimed task is not redelivered.");
await Post("/ack", new { id = job.GetProperty("id").GetString(), success = true, message = "文字已放入草稿" }, token);
Require((await send).Success, "Desktop succeeds only after explicit acknowledgement.");

send = service.SendTextAsync("指定到 ChatGPT", target: "chatgpt");
job = (await Post("/poll", new { }, token)).Data.GetProperty("job");
Require(job.GetProperty("target").GetString() == "chatgpt" && job.GetProperty("kind").GetString() == "target-text", "Other-target text uses a new kind so old extensions cannot route it to DeepSeek.");
await Post("/ack", new { id = job.GetProperty("id").GetString(), success = true, message = "文字已放入 ChatGPT" }, token);
Require((await send).Success, "Target-specific text remains acknowledged normally.");

Require(!(await service.SendImageAsync([1, 2, 3])).Success, "Non-PNG data is rejected.");
byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 1, 2];
var imageSend = service.SendImageAsync(png);
job = (await Post("/poll", new { }, token)).Data.GetProperty("job");
Require(Convert.FromBase64String(job.GetProperty("imageBase64").GetString()!).SequenceEqual(png), "PNG bytes survive transport.");
await Post("/ack", new { id = job.GetProperty("id").GetString(), success = false, message = "未观察到附件" }, token);
Require(!(await imageSend).Success, "Unconfirmed attachment is failure.");

Require(!(await service.SendImagesAsync([])).Success, "Empty selection is rejected.");
Require(!(await service.SendImagesAsync(Enumerable.Repeat(png, 11).ToArray())).Success, "More than ten separate images are rejected.");
Require(!(await service.SendImagesAsync([png, [1, 2, 3]])).Success, "An invalid later image rejects the whole batch before dispatch.");
var largePng = new byte[6 * 1024 * 1024 + 1];
Array.Copy(png, largePng, png.Length);
Require(!(await service.SendImagesAsync([largePng, largePng])).Success, "Combined twelve-MB limit applies across images.");
Require(!(await service.SendImagesAsync([png], "https://attacker.example")).Success, "Unapproved target is rejected.");
var batchSend = service.SendImagesAsync([png, png, png], "chatgpt");
job = (await Post("/poll", new { }, token)).Data.GetProperty("job");
Require(job.GetProperty("kind").GetString() == "images", "Batch job cannot be mistaken for a single image by older extensions.");
Require(job.GetProperty("target").GetString() == "chatgpt", "Selected target survives transport.");
var images = job.GetProperty("images").EnumerateArray().ToArray();
Require(images.Length == 3 && images.All(image => Convert.FromBase64String(image.GetProperty("imageBase64").GetString()!).SequenceEqual(png)), "All ordered PNG payloads survive transport independently.");
Require(images.Select(image => image.GetProperty("fileName").GetString()).Distinct().Count() == 3, "Batch filenames are distinct even for repeated PNG content.");
await Post("/ack", new { id = job.GetProperty("id").GetString(), success = true, verifiedCount = 1, message = "partial must not count as complete" }, token);
Require(!(await batchSend).Success, "Partial batch cannot succeed even if the extension incorrectly sets success.");
batchSend = service.SendImagesAsync([png, png], "doubao");
job = (await Post("/poll", new { }, token)).Data.GetProperty("job");
await Post("/ack", new { id = job.GetProperty("id").GetString(), success = true, verifiedCount = 2, message = "全部 2 张附件可见" }, token);
Require((await batchSend).Success, "Batch succeeds only after all expected attachments are acknowledged.");

using (var blocked = new ClientWebSocket())
{
    blocked.Options.SetRequestHeader("Origin", "https://attacker.example");
    try { await blocked.ConnectAsync(new Uri($"ws://127.0.0.1:{service.ListeningPort}/events"), CancellationToken.None); throw new InvalidOperationException("Website opened event channel."); }
    catch (WebSocketException) { Require(true, "Website Origin cannot open real-time notifications."); }
}
using (var wrong = new ClientWebSocket())
{
    wrong.Options.SetRequestHeader("Origin", $"chrome-extension://{extension}");
    await wrong.ConnectAsync(new Uri($"ws://127.0.0.1:{service.ListeningPort}/events"), CancellationToken.None);
    await SendEvent(wrong, new { type = "auth", token = "incorrect", extensionId = extension });
    try { await ReceiveEvent(wrong); throw new InvalidOperationException("Wrong token received notification."); }
    catch (WebSocketException) { Require(true, "Real-time channel authenticates token before any notification."); }
}
using var realtime = await ConnectEvents();
var watch = Stopwatch.StartNew();
var fastSend = service.SendImagesAsync([png, png], "deepseek");
var notification = await ReceiveEvent(realtime);
var notifyMilliseconds = watch.ElapsedMilliseconds;
Require(notification.GetProperty("type").GetString() == "job" && notification.GetProperty("target").GetString() == "deepseek", "New screenshot batch emits an immediate target notification.");
Require(!notification.TryGetProperty("images", out _) && !notification.TryGetProperty("text", out _), "Notification contains no screenshot or OCR payload.");
Require(notifyMilliseconds < 2000, "Real-time delivery does not wait for a thirty-second browser alarm.");
job = (await Post("/poll", new { }, token)).Data.GetProperty("job");
await Post("/ack", new { id = job.GetProperty("id").GetString(), success = true, verifiedCount = 2, message = "全部附件已确认" }, token);
var fastResult = await fastSend;
Require(fastResult.Success && fastResult.Timing?.QueueMilliseconds < 2000, "Queue and page timings are recorded without screenshot content.");
Console.WriteLine($"TIMING: notification={notifyMilliseconds}ms queue={fastResult.Timing!.QueueMilliseconds}ms total={fastResult.Timing.TotalMilliseconds}ms (synthetic two-image batch, not website upload).");
// The HTTP handshake timeout must not kill the established event stream after eight seconds.
await Task.Delay(8500);
await SendEvent(realtime, new { type = "ping" });
Require((await ReceiveEvent(realtime)).GetProperty("type").GetString() == "pong", "Established event stream survives handshake timeout and answers heartbeat.");
realtime.Abort();
var recoverySend = service.SendTextAsync("pending after socket disconnect");
using var reconnected = await ConnectEvents();
notification = await ReceiveEvent(reconnected);
Require(notification.GetProperty("type").GetString() == "job", "Reconnecting announces an unclaimed pending job.");
job = (await Post("/poll", new { }, token)).Data.GetProperty("job");
Require((await Post("/poll", new { }, token, retryEmpty: false)).Data.GetProperty("job").ValueKind == JsonValueKind.Null, "Real-time reconnect does not change claim-once semantics.");
await Post("/ack", new { id = job.GetProperty("id").GetString(), success = true, message = "草稿保留" }, token);
Require((await recoverySend).Success, "Recovered task still requires explicit webpage acknowledgement.");

await Post("/disconnect", new { }, token);
Require((await Post("/poll", new { }, token)).Status == HttpStatusCode.Unauthorized, "Disconnect invalidates token.");
Require(!(await service.SendTextAsync("not connected")).Success, "Unpaired text reports fallback, not delivery.");
Console.WriteLine("PASS: paired loopback transport, origin/host/token restrictions, delivery acknowledgement, PNG batches, target routing, limits, disconnect.");
await PairingPersistenceChecks.Run();
await BrowserStartupChecks.Run();

async Task<ClientWebSocket> ConnectEvents()
{
    var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Origin", $"chrome-extension://{extension}");
    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{service.ListeningPort}/events"), CancellationToken.None);
    await SendEvent(socket, new { type = "auth", token, extensionId = extension });
    Require((await ReceiveEvent(socket)).GetProperty("type").GetString() == "ready", "Paired real-time channel is ready.");
    return socket;
}

static async Task SendEvent(ClientWebSocket socket, object value)
{
    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task<JsonElement> ReceiveEvent(ClientWebSocket socket)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var bytes = new byte[4096];
    var count = 0;
    ValueWebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(bytes.AsMemory(count), timeout.Token);
        if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Event channel closed.");
        count += result.Count;
    } while (!result.EndOfMessage);
    using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
    return json.RootElement.Clone();
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}
