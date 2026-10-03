using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ScreenNote;

internal static class BrowserStartupChecks
{
    private static readonly string Extension = new('a', 32);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 1, 2];

    public static async Task Run()
    {
        var opened = new ConcurrentQueue<string>();
        using (var service = Start(target => opened.Enqueue(target)))
        {
            var token = (await Post(service, "/pair", new { code = service.PairCode })).Data.GetProperty("token").GetString()!;
            Require(service.IsConnected, "Fresh pairing has a recent heartbeat even without a running browser event channel.");
            var send = service.SendImagesAsync([Png, Png]);
            await Until(() => opened.Count == 1);
            Require(opened.Single() == "deepseek" && !send.IsCompleted, "Cold browser opens the selected website once and keeps the screenshot task queued.");
            Require(!(await service.SendTextAsync("overlap")).Success && opened.Count == 1, "A concurrent send neither replaces the queued screenshots nor opens another browser tab.");
            using var socket = await Connect(service, token);
            Require((await Receive(socket)).GetProperty("type").GetString() == "job", "Browser startup reconnect discovers the already queued screenshot task immediately.");
            var job = (await Post(service, "/poll", new { }, token)).Data.GetProperty("job");
            Require(job.GetProperty("images").GetArrayLength() == 2 && job.GetProperty("target").GetString() == "deepseek", "The cold-start delivery retains every image and the selected destination.");
            Require((await Post(service, "/poll", new { }, token)).Data.GetProperty("job").ValueKind == JsonValueKind.Null, "Browser startup still claims a queued task only once.");
            await Ack(service, token, job, 2);
            Require((await send).Success, "Opening a browser alone never counts as success; the page must acknowledge both images.");

            var warm = service.SendTextAsync("warm browser");
            Require((await Receive(socket)).GetProperty("type").GetString() == "job", "An already connected browser receives the regular immediate notification.");
            job = (await Post(service, "/poll", new { }, token)).Data.GetProperty("job");
            await Ack(service, token, job);
            Require((await warm).Success && opened.Count == 1, "Sending with a live browser does not invoke the default-browser launcher again.");

            var combined = service.SendImagesAsync([Png, Png], "deepseek", "Translate fixture prompt");
            await Receive(socket);
            job = (await Post(service, "/poll", new { }, token)).Data.GetProperty("job");
            Require(job.GetProperty("text").GetString() == "Translate fixture prompt" && job.GetProperty("images").GetArrayLength() == 2,
                "Translation and summary retain the prompt and all images in one task.");
            await Post(service, "/ack", new { id = job.GetProperty("id").GetString(), success = true, verifiedCount = 2, promptVerified = true }, token);
            Require((await combined).Success, "Combined delivery succeeds only when both attachments and prompt are confirmed.");
            var oldExtension = service.SendImagesAsync([Png], "deepseek", "Summary fixture prompt");
            await Receive(socket);
            job = (await Post(service, "/poll", new { }, token)).Data.GetProperty("job");
            await Ack(service, token, job, 1);
            Require(!(await oldExtension).Success, "An old extension cannot falsely acknowledge translation without putting its prompt in the draft.");

            socket.Abort();
            await Until(() => typeof(AiBridgeService).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service) is null);
            Require(service.IsConnected, "The recently closed browser still has a recent heartbeat for the regression scenario.");
            var reopened = service.SendTextAsync("restart the closed browser", target: "chatgpt");
            await Until(() => opened.Count == 2);
            Require(opened.Last() == "chatgpt", "A recently closed browser is reopened using the live connection instead of stale heartbeat state.");
            using var restored = await Connect(service, token);
            await Receive(restored);
            job = (await Post(service, "/poll", new { }, token)).Data.GetProperty("job");
            await Ack(service, token, job);
            Require((await reopened).Success && job.GetProperty("target").GetString() == "chatgpt", "The reopened browser receives the original task for the selected AI target.");
        }

        var unpairedOpened = new ConcurrentQueue<string>();
        using (var service = Start(target => unpairedOpened.Enqueue(target)))
        {
            var result = await service.SendTextAsync("unpaired fixture");
            Require(!result.Success && result.Message.Contains("首次配对") && unpairedOpened.Single() == "deepseek", "First use opens the website and explains pairing without pretending that content was delivered.");
            Require((await Post(service, "/poll", new { })).Status == HttpStatusCode.Unauthorized, "Opening the browser does not bypass pairing or disclose an unpaired task.");
            var count = unpairedOpened.Count;
            Require(!(await service.SendImageAsync([1, 2, 3])).Success
                && !(await service.SendTextAsync("bad target", target: "https://invalid.example")).Success
                && unpairedOpened.Count == count, "Invalid content and arbitrary websites are rejected before a browser is opened.");
        }

        var attempts = 0;
        using (var service = Start(_ => { Interlocked.Increment(ref attempts); throw new Win32Exception("fixture launch failure"); }))
        {
            var token = (await Post(service, "/pair", new { code = service.PairCode })).Data.GetProperty("token").GetString()!;
            var failed = await service.SendTextAsync("launch failure");
            Require(!failed.Success && failed.Message.Contains("未能打开"), "A default-browser launch error is reported immediately with a retry instruction.");
            Require((await Post(service, "/poll", new { }, token)).Data.GetProperty("job").ValueKind == JsonValueKind.Null,
                "A launch failure removes the unclaimed task instead of uploading it unexpectedly later.");
            await service.SendTextAsync("retry after failure");
            Require(attempts == 2, "A failed browser launch releases the delivery slot for a later retry.");
        }

        using (var service = Start(_ => { }, TimeSpan.FromMilliseconds(250)))
        {
            var token = (await Post(service, "/pair", new { code = service.PairCode })).Data.GetProperty("token").GetString()!;
            var result = await service.SendTextAsync("no matching extension");
            Require(!result.Success && result.Message.Contains("默认浏览器") && result.Message.Contains("扩展"),
                "A browser without the paired extension gets an actionable timeout rather than a silent no-op.");
            Require((await Post(service, "/poll", new { }, token)).Data.GetProperty("job").ValueKind == JsonValueKind.Null,
                "An unclaimed expired task is never delivered after a later reconnect.");
            var claimed = service.SendTextAsync("claimed but no acknowledgement");
            var job = (await Post(service, "/poll", new { }, token)).Data.GetProperty("job");
            Require(job.ValueKind != JsonValueKind.Null, "A matching extension can claim the next task after timeout.");
            Require(!(await claimed).Success && (await claimed).Message.Contains("检查草稿"),
                "Once claimed, an acknowledgement timeout asks the user to inspect the draft instead of automatically resending.");
        }
        Console.WriteLine("PASS: default-browser cold startup, live-channel detection, queued reconnect, single claim, launch failure and extension timeout.");
    }

    private static AiBridgeService Start(Action<string> open, TimeSpan? timeout = null)
    {
        var service = new AiBridgeService(0, openTarget: open, deliveryTimeout: timeout ?? TimeSpan.FromSeconds(5));
        service.Start(); return service;
    }
    private static async Task<(HttpStatusCode Status, JsonElement Data)> Post(AiBridgeService service, string path, object body, string? token = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{service.ListeningPort}{path}")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", $"chrome-extension://{Extension}"); request.Headers.Add("X-ScreenNote-Extension", Extension);
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }
    private static async Task Ack(AiBridgeService service, string token, JsonElement job, int count = 0) =>
        await Post(service, "/ack", new { id = job.GetProperty("id").GetString(), success = true, verifiedCount = count }, token);
    private static async Task<ClientWebSocket> Connect(AiBridgeService service, string token)
    {
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", $"chrome-extension://{Extension}");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{service.ListeningPort}/events"), CancellationToken.None);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "auth", token, extensionId = Extension }).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        Require((await Receive(socket)).GetProperty("type").GetString() == "ready", "Browser reconnect authenticates before receiving its pending job.");
        return socket;
    }
    private static async Task<JsonElement> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var bytes = new byte[4096]; var count = 0; ValueWebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(bytes.AsMemory(count), timeout.Token); count += part.Count; } while (!part.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count)); return document.RootElement.Clone();
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(condition(), "Asynchronous browser startup condition completes within the test deadline.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
