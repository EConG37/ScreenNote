using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenNote;

public record BridgeDeliveryResult(bool Success, string Message)
{
    public BridgeDeliveryTiming? Timing { get; init; }
}

public sealed record BridgeDeliveryTiming(int ImageCount, long ImageBytes, long PreparationMilliseconds,
    long QueueMilliseconds, long PageMilliseconds, long TotalMilliseconds);

/// <summary>
/// Paired loopback transport with optional encrypted credential persistence. The extension reports a verified draft insertion;
/// the desktop never submits a prompt or handles AI website credentials.
/// </summary>
public sealed class AiBridgeService : IDisposable
{
    public const int Port = 18473;
    public const int MaximumImageCount = 10;
    public const int MaximumImageBytes = 12 * 1024 * 1024;
    private const int MaximumBodyBytes = 20 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(8);
    private readonly int _requestedPort;
    private readonly PairingStore? _pairingStore;
    private readonly Action<string> _openTarget;
    private readonly TimeSpan _deliveryTimeout;
    private bool _pairingLoadFailed;
    private int _listeningPort;
    private TcpListener? _listener;
    private Timer? _statusTimer;
    private string? _token;
    private string? _extensionId;
    private int _failedPairs;
    private DateTimeOffset _lastSeen;
    private PendingDelivery? _pending;
    private EventConnection? _events;
    private BridgeDeliveryTiming? _lastDeliveryTiming;
    private bool _disposed;
    private string _status = "网页连接尚未启动";

    public string PairCode { get; private set; } = NewPairCode();
    public int ListeningPort { get { lock (_gate) return _listeningPort; } }
    public BridgeDeliveryTiming? LastDeliveryTiming { get { lock (_gate) return _lastDeliveryTiming; } }
    public string Status { get { lock (_gate) return _status; } }
    public bool HasPairing { get { lock (_gate) return _token is not null; } }
    public bool IsConnected { get { lock (_gate) return _token is not null && DateTimeOffset.UtcNow - _lastSeen < TimeSpan.FromSeconds(45); } }
    public event EventHandler? StatusChanged;

    public AiBridgeService(int port = Port, string? pairingFile = null, Action<string>? openTarget = null, TimeSpan? deliveryTimeout = null)
    {
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _requestedPort = port;
        _openTarget = openTarget ?? OpenTarget;
        _deliveryTimeout = deliveryTimeout ?? TimeSpan.FromSeconds(60);
        if (_deliveryTimeout <= TimeSpan.Zero || _deliveryTimeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(deliveryTimeout));
        if (pairingFile is not null)
        {
            _pairingStore = new PairingStore(pairingFile);
            try
            {
                var saved = _pairingStore.Load();
                _token = saved?.Token;
                _extensionId = saved?.ExtensionId;
            }
            catch (Exception error) when (IsPairingStorageError(error)) { _pairingLoadFailed = true; }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
                _listener.Start(8);
                _listeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
                _listener = null;
                SetStatus("网页连接端口被占用，仍可复制内容并打开 AI 网页");
                return;
            }
        }
        SetStatus(_pairingLoadFailed ? "保存的配对无法读取，请重新配对" : _token is null ? "等待浏览器扩展配对" : "配对已保存，等待浏览器自动连接");
        _statusTimer = new Timer(_ => RefreshStatus(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _ = AcceptClientsAsync();
    }

    public Task<BridgeDeliveryResult> SendTextAsync(string text, string draftMode = "append", string target = "deepseek")
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(new BridgeDeliveryResult(false, "没有可发送的文字。"));
        if (text.Length > 200_000) return Task.FromResult(new BridgeDeliveryResult(false, "文字过长，请选取一部分再发送。"));
        return SendAsync(target == "deepseek" ? "text" : "target-text", text, null, draftMode == "replace" ? "replace" : "append", target);
    }

    public Task<BridgeDeliveryResult> SendImageAsync(byte[] png) => SendImagesAsync([png]);

    public Task<BridgeDeliveryResult> SendImagesAsync(IReadOnlyList<byte[]> images, string target = "deepseek", string prompt = "")
    {
        if (prompt.Length > 200_000) return Task.FromResult(new BridgeDeliveryResult(false, "提示词过长，请选取一部分再发送。"));
        if (images is null || images.Count == 0) return Task.FromResult(new BridgeDeliveryResult(false, "请先选择至少一张截图。"));
        if (images.Count > MaximumImageCount) return Task.FromResult(new BridgeDeliveryResult(false, $"一次最多带入 {MaximumImageCount} 张截图，请分批选择。"));
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        long total = 0;
        for (var index = 0; index < images.Count; index++)
        {
            var png = images[index];
            if (png is null || png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(signature)) return Task.FromResult(new BridgeDeliveryResult(false, $"第 {index + 1} 张截图不是有效的 PNG 图片。"));
            total += png.Length;
            if (total > MaximumImageBytes) return Task.FromResult(new BridgeDeliveryResult(false, "所选截图合计超过 12 MB，请减少图片或缩小后重试。"));
        }
        // Old extensions ignore Target and only understand DeepSeek's single-image job.
        // Use the new job kind for other targets so they cannot silently route elsewhere.
        return SendAsync(images.Count == 1 && target == "deepseek" && prompt.Length == 0 ? "image" : "images", prompt.Length == 0 ? null : prompt, images, "append", target);
    }

    private async Task<BridgeDeliveryResult> SendAsync(string kind, string? text, IReadOnlyList<byte[]>? images, string draftMode, string target)
    {
        if (target is not ("deepseek" or "chatgpt" or "doubao")) return new(false, "不支持这个 AI 网页，请选择 DeepSeek、ChatGPT 或豆包。");
        var started = Stopwatch.GetTimestamp();
        var id = Guid.NewGuid().ToString("N");
        // PNG conversion is completed before publication. Do not hold the transport lock or
        // the WPF thread while encoding a batch into its wire representation.
        var attachments = images is null ? null : await Task.Run(() => images.Select((png, index) =>
            new BridgeImage($"screennote-{id[..8]}-{index + 1:D2}.png", Convert.ToBase64String(png))).ToArray()).ConfigureAwait(false);
        PendingDelivery? pending = null;
        bool openBrowser;
        string? unavailable = null;
        lock (_gate)
        {
            if (_disposed) return new(false, "网页连接已关闭，可以复制内容后手动粘贴。" );
            if (_pending is not null) return new(false, "上一条内容正在处理，请先查看 AI 网页。" );
            openBrowser = _events?.IsOpen != true;
            if (_listener is null) unavailable = "网页连接不可用，可以复制内容后手动粘贴。";
            else if (_token is null) unavailable = "请先在默认浏览器中安装扩展并完成首次配对，再发送内容。";
            else
            {
                pending = new PendingDelivery(new BridgeJob(id, kind, text, kind == "image" ? attachments![0].ImageBase64 : null,
                    draftMode, DateTimeOffset.UtcNow.Add(_deliveryTimeout).ToUnixTimeMilliseconds(), target,
                    kind == "image" ? null : attachments), started, images?.Count ?? 0, images?.Sum(image => (long)image.Length) ?? 0);
                _pending = pending;
            }
        }
        try
        {
            if (openBrowser)
            {
                // Publish before starting the browser so its first reconnect can claim this job.
                // The last heartbeat may be recent even after the browser exits; use the live channel.
                lock (_gate) { openBrowser = pending is null || (!pending.Claimed && _events?.IsOpen != true); }
                if (openBrowser)
                {
                    SetStatus($"正在启动默认浏览器并打开 {TargetName(target)}…");
                    try { await Task.Run(() => _openTarget(target)).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        bool claimed; lock (_gate) claimed = pending?.Claimed == true;
                        if (!claimed)
                        {
                            var failed = new BridgeDeliveryResult(false, "默认浏览器未能打开：" + ex.Message + "。可以手动打开网页后重试。");
                            return pending is null ? failed : WithTiming(pending, failed);
                        }
                    }
                }
            }
            if (pending is null) return new(false, $"已打开 {TargetName(target)} 页面。" + unavailable);
            NotifyPendingDelivery();
            SetStatus(openBrowser ? $"正在等待浏览器连接并带入 {TargetName(target)}…" : $"正在连接 {TargetName(target)} 页面…");
            var result = await pending.Completion.Task.WaitAsync(_deliveryTimeout).ConfigureAwait(false);
            return WithTiming(pending, result);
        }
        catch (TimeoutException) when (pending is not null)
        {
            bool claimed; lock (_gate) claimed = pending?.Claimed == true;
            return WithTiming(pending!, new(false, claimed
                ? "网页未确认接收内容。请检查草稿，未登录时请先登录；避免重复发送。"
                : "浏览器扩展尚未接收内容。请确认默认浏览器中已安装并配对扩展，再重试；截图仍保留在历史中。"));
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_pending, pending)) _pending = null; }
            RefreshStatus();
        }
    }

    public static string TargetName(string target) => target switch { "deepseek" => "DeepSeek", "chatgpt" => "ChatGPT", "doubao" => "豆包", _ => "AI" };

    public static void OpenTarget(string target)
    {
        using var process = Process.Start(new ProcessStartInfo(target switch
        {
            "deepseek" => "https://chat.deepseek.com/",
            "chatgpt" => "https://chatgpt.com/",
            "doubao" => "https://www.doubao.com/chat/",
            _ => throw new ArgumentException("不支持这个 AI 网页。", nameof(target))
        }) { UseShellExecute = true });
    }

    public static void OpenDeepSeek() => OpenTarget("deepseek");

    public void ResetPairing()
    {
        EventConnection? events;
        lock (_gate)
        {
            // Delete first. A failed revocation must not report success then recover the old token on restart.
            try { _pairingStore?.Delete(); }
            catch (Exception error) when (IsPairingStorageError(error)) { throw new IOException("无法删除保存的配对，连接仍然保留。请检查设置目录的写入权限后重试。", error); }
            events = _events; _events = null;
            _token = null;
            _extensionId = null;
            _failedPairs = 0;
            _pairingLoadFailed = false;
            PairCode = NewPairCode();
            _pending?.Completion.TrySetResult(new(false, "网页连接已断开。"));
            _pending = null;
        }
        events?.Abort();
        SetStatus("等待浏览器扩展配对");
    }

    private static string NewPairCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private async Task AcceptClientsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (!_connections.Wait(0)) { client.Dispose(); continue; }
                _ = ProcessClientAsync(client);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (_stop.IsCancellationRequested) break; }
        }
    }

    private async Task ProcessClientAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request.Method == "GET" && request.Path == "/events")
                {
                    await HandleEventsAsync(stream, request, timeout.Token).ConfigureAwait(false);
                    return;
                }
                var (status, response) = HandleRequest(request);
                await WriteResponseAsync(stream, status, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidDataException or FormatException or WebSocketException or ObjectDisposedException)
            {
                // Malformed/slow connections are closed. No content is logged or disclosed.
            }
            finally { _connections.Release(); }
        }
    }

    private (int Status, object Response) HandleRequest(HttpRequest request)
    {
        if (request.Method != "POST" || !request.Headers.TryGetValue("Host", out var host) || host != $"127.0.0.1:{_listeningPort}") return (403, new { message = "不允许的请求。" });
        if (!request.Headers.TryGetValue("Content-Type", out var contentType) || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return (415, new { message = "仅接受 JSON 请求。" });
        if (!request.Headers.TryGetValue("X-ScreenNote-Extension", out var extensionId) || !Regex.IsMatch(extensionId, "^[a-p]{32}$")) return (403, new { message = "仅接受浏览器扩展连接。" });
        // Extension fetch may omit Origin. Website requests cannot set our custom header
        // without a preflight; OPTIONS is rejected and no CORS grant is ever emitted.
        if (request.Headers.TryGetValue("Origin", out var origin) && !IsExtensionOrigin(origin, extensionId)) return (403, new { message = "不允许的请求来源。" });

        lock (_gate)
        {
            if (_disposed) return (503, new { message = "桌面工具正在退出。" });
            if (request.Path == "/pair")
            {
                if (_failedPairs >= 10) return (429, new { message = "配对尝试过多，请在桌面工具中重置配对码。" });
                if (_token is not null) return (409, new { message = "已有扩展连接，请先在桌面工具中重置配对码。" });
                var code = JsonString(request.Data, "code");
                if (code != PairCode) { _failedPairs++; return (403, new { message = "配对码不正确，请查看桌面工具设置。" }); }
                var newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                try { _pairingStore?.Save(new PairingStore.Credential(newToken, extensionId)); }
                catch (Exception error) when (IsPairingStorageError(error)) { return (500, new { message = "无法保存配对，请检查桌面设置目录的写入权限后重试。" }); }
                _token = newToken;
                _extensionId = extensionId;
                _pairingLoadFailed = false;
                _lastSeen = DateTimeOffset.UtcNow;
                SetStatus("浏览器扩展已连接");
                return (200, new { token = _token, message = "已配对。" });
            }
            if (_token is null || _extensionId != extensionId || !request.Headers.TryGetValue("Authorization", out var auth) || !SecureEquals(auth, $"Bearer {_token}")) return (401, new { message = "请重新配对。" });
            _lastSeen = DateTimeOffset.UtcNow;
            if (request.Path == "/status") return (200, new { message = "已连接 ScreenNote。内容将放入网页草稿，由你检查并发送。" });
            if (request.Path == "/poll")
            {
                if (_pending is null || _pending.Claimed || _pending.Job.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return (200, new { job = (object?)null });
                _pending.Claimed = true;
                _pending.ClaimedTimestamp = Stopwatch.GetTimestamp();
                return (200, new { job = _pending.Job });
            }
            if (request.Path == "/ack")
            {
                if (_pending is null || _pending.Job.Id != JsonString(request.Data, "id")) return (200, new { message = "任务已结束。" });
                if (!_pending.Claimed) return (409, new { message = "任务尚未领取。" });
                var success = request.Data.TryGetProperty("success", out var flag) && flag.ValueKind == JsonValueKind.True;
                var message = JsonString(request.Data, "message") ?? "网页未确认接收内容。";
                if (success && _pending.Job.Kind == "images" && (!request.Data.TryGetProperty("verifiedCount", out var verified) || verified.ValueKind != JsonValueKind.Number || !verified.TryGetInt32(out var count) || count != _pending.Job.Images!.Length))
                {
                    success = false;
                    message = "网页未确认全部截图附件，请先检查页面中已有的附件，避免重复上传。";
                }
                if (message.Length > 600) message = message[..600];
                if (success && _pending.Job.Kind == "images" && !string.IsNullOrEmpty(_pending.Job.Text)
                    && (!request.Data.TryGetProperty("promptVerified", out var promptFlag) || promptFlag.ValueKind != JsonValueKind.True))
                {
                    success = false;
                    message = "截图附件已带入，但提示词未确认。请先检查网页草稿；重新加载 0.6.0 浏览器扩展后可使用翻译与总结。";
                }
                _pending.Completion.TrySetResult(new(success, message));
                return (200, new { message = "已确认。" });
            }
            if (request.Path == "/disconnect")
            {
                try { ResetPairing(); }
                catch (IOException) { return (500, new { message = "无法删除保存的配对，连接仍然保留。请检查桌面设置目录的写入权限后重试。" }); }
                return (200, new { message = "已断开。" });
            }
            return (404, new { message = "未知操作。" });
        }
    }

    private static bool SecureEquals(string value, string expected) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(expected));
    private static bool IsExtensionOrigin(string origin, string extensionId) => origin == $"chrome-extension://{extensionId}" || origin == $"extension://{extensionId}";
    private static bool IsPairingStorageError(Exception error) => error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or PlatformNotSupportedException;
    private static string? JsonString(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task HandleEventsAsync(NetworkStream stream, HttpRequest request, CancellationToken handshakeToken)
    {
        var headers = request.Headers;
        if (!headers.TryGetValue("Host", out var host) || host != $"127.0.0.1:{_listeningPort}"
            || !headers.TryGetValue("Origin", out var origin) || !Regex.IsMatch(origin, "^(?:chrome-extension|extension)://[a-p]{32}$")
            || !headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Connection", out var connectionHeader) || !connectionHeader.Split(',').Any(value => value.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            || !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13"
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key) || Convert.FromBase64String(key).Length != 16)
        {
            await WriteResponseAsync(stream, 403, new { message = "不允许的实时连接。" }, handshakeToken).ConfigureAwait(false);
            return;
        }
        var extensionId = origin[(origin.IndexOf("://", StringComparison.Ordinal) + 3)..];
        lock (_gate) { if (_disposed || _token is null || _extensionId != extensionId) return; }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), handshakeToken).ConfigureAwait(false);
        using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
        var auth = await ReceiveEventAsync(socket, handshakeToken).ConfigureAwait(false);
        var authenticated = JsonString(auth, "type") == "auth" && JsonString(auth, "extensionId") == extensionId;
        EventConnection? previous;
        var events = new EventConnection(socket, _stop.Token);
        lock (_gate)
        {
            if (!authenticated || _disposed || _token is null || _extensionId != extensionId || !SecureEquals(JsonString(auth, "token") ?? "", _token))
            {
                socket.Abort(); return;
            }
            previous = _events; _events = events; _lastSeen = DateTimeOffset.UtcNow;
        }
        previous?.Abort();
        try
        {
            await events.SendAsync(new { type = "ready" }).ConfigureAwait(false);
            NotifyPendingDelivery();
            while (!_stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                var message = await ReceiveEventAsync(socket, idle.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!ReferenceEquals(_events, events) || _token is null) break;
                    _lastSeen = DateTimeOffset.UtcNow;
                }
                if (JsonString(message, "type") != "ping") break;
                await events.SendAsync(new { type = "pong" }).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_events, events)) _events = null; }
            events.Abort();
        }
    }

    private static async Task<JsonElement> ReceiveEventAsync(WebSocket socket, CancellationToken token)
    {
        var bytes = new byte[4096];
        var used = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (used == bytes.Length) throw new InvalidDataException("实时消息过长。");
            result = await socket.ReceiveAsync(bytes.AsMemory(used), token).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException();
            used += result.Count;
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.AsMemory(0, used));
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        return json.RootElement.Clone();
    }

    private void NotifyPendingDelivery()
    {
        EventConnection? events;
        string? id, target;
        lock (_gate)
        {
            if (_pending is null || _pending.Claimed || _pending.Job.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return;
            events = _events; id = _pending.Job.Id; target = _pending.Job.Target;
        }
        if (events is not null) _ = NotifyAsync();
        async Task NotifyAsync()
        {
            try { await events!.SendAsync(new { type = "job", id, target }).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                events!.Abort();
            }
        }
    }

    private BridgeDeliveryResult WithTiming(PendingDelivery pending, BridgeDeliveryResult result)
    {
        var completed = Stopwatch.GetTimestamp();
        static long Milliseconds(long from, long to) => (long)Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        var timing = new BridgeDeliveryTiming(pending.ImageCount, pending.ImageBytes,
            Milliseconds(pending.StartedTimestamp, pending.PublishedTimestamp),
            Milliseconds(pending.PublishedTimestamp, pending.ClaimedTimestamp == 0 ? completed : pending.ClaimedTimestamp),
            pending.ClaimedTimestamp == 0 ? 0 : Milliseconds(pending.ClaimedTimestamp, completed),
            Milliseconds(pending.StartedTimestamp, completed));
        lock (_gate) _lastDeliveryTiming = timing;
        return result with { Timing = timing };
    }

    private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var chunk = new byte[1024];
        int headerEnd;
        byte[] packet;
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException();
            received.Write(chunk, 0, count);
            packet = received.ToArray();
            headerEnd = FindHeaderEnd(packet);
            if (headerEnd >= 0) break;
            if (packet.Length > 8192) throw new InvalidDataException();
        }
        if (headerEnd > 8192) throw new InvalidDataException();
        var lines = Encoding.ASCII.GetString(packet, 0, headerEnd).Split("\r\n", StringSplitOptions.None);
        var firstLine = lines[0].Split(' ');
        if (firstLine.Length != 3 || firstLine[2] != "HTTP/1.1") throw new InvalidDataException();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) throw new InvalidDataException();
        }
        if (firstLine[0] == "GET" && firstLine[1] == "/events")
        {
            if (packet.Length != headerEnd + 4 || headers.ContainsKey("Transfer-Encoding")
                || headers.ContainsKey("Content-Length")) throw new InvalidDataException();
            return new HttpRequest(firstLine[0], firstLine[1], headers, default);
        }
        if (headers.ContainsKey("Transfer-Encoding") || !headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 || length > MaximumBodyBytes) throw new InvalidDataException();
        var body = new byte[length];
        var copied = Math.Min(length, packet.Length - headerEnd - 4);
        Array.Copy(packet, headerEnd + 4, body, 0, copied);
        if (copied < length) await stream.ReadExactlyAsync(body.AsMemory(copied), cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        return new HttpRequest(firstLine[0], firstLine[1], headers, json.RootElement.Clone());
    }

    private static int FindHeaderEnd(byte[] packet)
    {
        for (var index = 0; index < packet.Length - 3; index++)
            if (packet[index] == 13 && packet[index + 1] == 10 && packet[index + 2] == 13 && packet[index + 3] == 10) return index;
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, object value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private void RefreshStatus()
    {
        lock (_gate)
        {
            if (_disposed || _listener is null || _pending is not null) return;
            SetStatus(_token is null ? _pairingLoadFailed ? "保存的配对无法读取，请重新配对" : "等待浏览器扩展配对" : DateTimeOffset.UtcNow - _lastSeen < TimeSpan.FromSeconds(45) ? "浏览器扩展已连接" : "配对已保存，等待浏览器自动连接");
        }
    }

    private void SetStatus(string value)
    {
        bool changed;
        lock (_gate) { changed = _status != value; _status = value; }
        if (changed) ThreadPool.QueueUserWorkItem(_ => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        EventConnection? events;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            events = _events; _events = null;
            _pending?.Completion.TrySetResult(new(false, "桌面工具已退出。"));
            _pending = null;
            _token = null;
        }
        events?.Abort();
        _stop.Cancel();
        _listener?.Stop();
        _statusTimer?.Dispose();
        // The cancellation source and semaphore remain valid while outstanding clients unwind.
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, JsonElement Data);
    private sealed record BridgeImage(string FileName, string ImageBase64);
    private sealed record BridgeJob(string Id, string Kind, string? Text, string? ImageBase64, string DraftMode, long ExpiresAt, string Target, BridgeImage[]? Images);
    private sealed class EventConnection(WebSocket socket, CancellationToken stop)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        public bool IsOpen => socket.State == WebSocketState.Open;
        public async Task SendAsync(object message)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _send.WaitAsync(timeout.Token).ConfigureAwait(false);
            try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions).AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false); }
            finally { _send.Release(); }
        }
        public void Abort() => socket.Abort();
    }
    private sealed class PendingDelivery(BridgeJob job, long startedTimestamp, int imageCount, long imageBytes)
    {
        public BridgeJob Job { get; } = job;
        public long StartedTimestamp { get; } = startedTimestamp;
        public long PublishedTimestamp { get; } = Stopwatch.GetTimestamp();
        public long ClaimedTimestamp { get; set; }
        public int ImageCount { get; } = imageCount;
        public long ImageBytes { get; } = imageBytes;
        public bool Claimed { get; set; }
        public TaskCompletionSource<BridgeDeliveryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
