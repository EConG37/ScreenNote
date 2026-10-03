"use strict";

const BRIDGE_URL = "http://127.0.0.1:18473";
const AI_TARGETS = Object.freeze({
  deepseek: { name: "DeepSeek", url: "https://chat.deepseek.com/", pattern: "https://chat.deepseek.com/*" },
  chatgpt: { name: "ChatGPT", url: "https://chatgpt.com/", pattern: "https://chatgpt.com/*" },
  doubao: { name: "豆包", url: "https://www.doubao.com/chat/", pattern: "https://www.doubao.com/chat/*" }
});
const POLL_ALARM = "screennote-bridge-poll";
let pollTask = null;
let pollAgain = false;
let realtimeSocket = null;
let realtimeConnect = null;
let heartbeatTimer = null;
let reconnectTimer = null;
let reconnectDelay = 1000;
let realtimeEpoch = 0;
let realtimeRetryAfter = 0;
let credentialStoreReady = null;
let credentialMutation = null;

async function changeCredential(action) {
  const previous = credentialMutation;
  let release;
  const current = new Promise(resolve => { release = resolve; });
  credentialMutation = current;
  try {
    if (previous) await previous;
    return await action();
  } finally {
    release();
    if (credentialMutation === current) credentialMutation = null;
  }
}

function prepareCredentialStore() {
  if (!credentialStoreReady) {
    credentialStoreReady = (async () => {
      // local survives browser restarts. Do not expose the credential to AI content scripts.
      await chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
      const saved = await chrome.storage.local.get("bridgeToken");
      const legacy = await chrome.storage.session.get("bridgeToken");
      if (!saved.bridgeToken && legacy.bridgeToken) await chrome.storage.local.set({ bridgeToken: legacy.bridgeToken });
      await chrome.storage.session.remove("bridgeToken");
    })().catch(error => { credentialStoreReady = null; throw error; });
  }
  return credentialStoreReady;
}

async function pairingToken() {
  await prepareCredentialStore();
  const { bridgeToken } = await chrome.storage.local.get("bridgeToken");
  return typeof bridgeToken === "string" && bridgeToken ? bridgeToken : null;
}

async function forgetPairing(expectedToken) {
  await prepareCredentialStore();
  await changeCredential(async () => {
    // Serialize the read/delete with new pair saves. A late 401 cannot erase newer authorization.
    if (await pairingToken() !== expectedToken) return;
    stopRealtime();
    await chrome.storage.session.remove(["bridgeToken", "busyUntil", "pendingAck"]);
    await chrome.storage.local.remove("bridgeToken");
  });
}

function stopRealtime() {
  realtimeEpoch++;
  realtimeConnect = null;
  realtimeRetryAfter = 0;
  clearInterval(heartbeatTimer); heartbeatTimer = null;
  clearTimeout(reconnectTimer); reconnectTimer = null;
  const socket = realtimeSocket; realtimeSocket = null;
  if (socket) socket.close();
}

function reconnectRealtime() {
  if (reconnectTimer) return;
  const delay = reconnectDelay;
  reconnectDelay = Math.min(reconnectDelay * 2, 15000);
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    realtimeRetryAfter = 0;
    void ensureRealtime().catch(() => {});
  }, delay);
}

function ensureRealtime() {
  if (typeof WebSocket === "undefined") return Promise.resolve();
  if (realtimeSocket && realtimeSocket.readyState <= WebSocket.OPEN) return Promise.resolve();
  if (realtimeConnect) return realtimeConnect;
  if (Date.now() < realtimeRetryAfter) return Promise.resolve();
  const epoch = realtimeEpoch;
  const attempt = (async () => {
    const bridgeToken = await pairingToken();
    if (!bridgeToken || epoch !== realtimeEpoch) return;
    // Chrome logs failed WebSocket constructors even with onerror installed.
    // Confirm the authenticated companion is running before opening the socket.
    try { await post("/status", {}, bridgeToken); }
    catch (error) {
      if (!error.pairingRevoked && epoch === realtimeEpoch) {
        realtimeRetryAfter = Date.now() + reconnectDelay;
        await chrome.storage.session.set({ lastError: "桌面工具未启动或连接不可用；已保存配对，启动拾屏后自动恢复。" });
        reconnectRealtime();
      }
      return;
    }
    if (epoch !== realtimeEpoch || await pairingToken() !== bridgeToken) return;
    await chrome.storage.session.remove("lastError");
    const socket = new WebSocket("ws://127.0.0.1:18473/events");
    realtimeSocket = socket;
    let lastMessage = Date.now();
    socket.onopen = () => {
      if (socket !== realtimeSocket) return;
      socket.send(JSON.stringify({ type: "auth", token: bridgeToken, extensionId: chrome.runtime.id }));
      // Actual messages, rather than an idle open socket, keep MV3 awake.
      heartbeatTimer = setInterval(() => {
        if (socket !== realtimeSocket || socket.readyState !== WebSocket.OPEN) return;
        if (Date.now() - lastMessage > 45000) { socket.close(); return; }
        socket.send(JSON.stringify({ type: "ping" }));
      }, 20000);
    };
    socket.onmessage = event => {
      if (socket !== realtimeSocket) return;
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      lastMessage = Date.now();
      if (message.type === "ready") { reconnectDelay = 1000; realtimeRetryAfter = 0; }
      if (message.type === "ready" || message.type === "job") {
        // The notification contains no image/text; /poll still claims each job once.
        void pollBridge(true).catch(error => chrome.storage.session.set({ lastError: error.message }));
      }
    };
    socket.onclose = () => {
      if (socket !== realtimeSocket) return;
      realtimeSocket = null;
      clearInterval(heartbeatTimer); heartbeatTimer = null;
      realtimeRetryAfter = Date.now() + reconnectDelay;
      reconnectRealtime();
    };
    socket.onerror = () => { if (socket === realtimeSocket) socket.close(); };
  })().finally(() => { if (realtimeConnect === attempt) realtimeConnect = null; });
  realtimeConnect = attempt;
  return attempt;
}

async function post(path, data = {}, token = "") {
  const response = await fetch(`${BRIDGE_URL}${path}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ScreenNote-Extension": chrome.runtime.id,
      ...(token ? { Authorization: `Bearer ${token}` } : {})
    },
    body: JSON.stringify(data),
    signal: AbortSignal.timeout(5000),
    credentials: "omit",
    cache: "no-store"
  });
  if (response.status === 401) {
    await forgetPairing(token);
    const error = new Error("连接已失效，请输入桌面工具的配对码重新连接。");
    error.pairingRevoked = true;
    throw error;
  }
  const result = await response.json();
  if (!response.ok) throw new Error(result.message || `连接失败 (${response.status})`);
  return result;
}

function targetDetails(target = "deepseek") {
  if (!Object.prototype.hasOwnProperty.call(AI_TARGETS, target)) throw new Error("不支持这个 AI 网页，请在桌面工具中重新选择。");
  return AI_TARGETS[target];
}

function isAiPage(url) {
  try {
    const page = new URL(url);
    return page.protocol === "https:" && (page.hostname === "chat.deepseek.com" || page.hostname === "chatgpt.com" || page.hostname === "www.doubao.com" && page.pathname.startsWith("/chat/"));
  } catch { return false; }
}

async function ensureTargetTab(target = "deepseek") {
  const details = targetDetails(target);
  const tabs = await chrome.tabs.query({ url: details.pattern });
  const tab = tabs.find(item => item.active) || tabs[0];
  if (tab) {
    await chrome.tabs.update(tab.id, { active: true });
    await chrome.windows.update(tab.windowId, { focused: true });
    return tab;
  }
  return await chrome.tabs.create({ url: details.url, active: true });
}

async function deliverToTab(tabId, job) {
  // New tabs and SPA navigation may not have a ready composer yet.
  const expires = Math.min(Date.now() + 15000, job.expiresAt || Infinity);
  while (Date.now() < expires) {
    let ready;
    try {
      ready = await chrome.tabs.sendMessage(tabId, { type: "SCREENNOTE_READY", target: job.target || "deepseek" });
    } catch { /* The content script is still loading. */ }
    // Once a delivery is attempted, never repeat it after a navigation or lost reply.
    if (ready?.ready) return await chrome.tabs.sendMessage(tabId, { type: "SCREENNOTE_DELIVER", job });
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  return { success: false, message: `没有找到 ${targetDetails(job.target).name} 输入框。请先登录并打开聊天页面，再重试。` };
}

function pollBridge(wake = false) {
  if (pollTask) { if (wake) pollAgain = true; return pollTask; }
  pollTask = (async () => {
    do { pollAgain = false; await runPollBridge(); } while (pollAgain);
  })().finally(() => { pollTask = null; });
  return pollTask;
}

async function runPollBridge() {
  const bridgeToken = await pairingToken();
  if (!bridgeToken) return;
  const state = await chrome.storage.session.get(["busyUntil", "pendingAck"]);
  if (state.pendingAck) {
    await post("/ack", state.pendingAck, bridgeToken);
    await chrome.storage.session.remove(["pendingAck", "busyUntil"]);
  }

  // A single promise serializes this worker; the desktop's claim-once protocol
  // prevents redelivery after suspension. An abandoned session timer must not
  // delay a new job for seventy seconds after a worker restart.
  await chrome.storage.session.set({ busyUntil: Date.now() + 70000 });
  let job;
  try {
    const result = await post("/poll", {}, bridgeToken);
    job = result.job;
    if (!job) {
      await chrome.storage.session.remove("busyUntil");
      return;
    }
    if (await pairingToken() !== bridgeToken) return;
    const target = await ensureTargetTab(job.target || "deepseek");
    let delivery;
    try { delivery = await deliverToTab(target.id, job); }
    catch { delivery = { success: false, message: "网页已关闭或刷新，请检查草稿后重试。" }; }
    if (await pairingToken() !== bridgeToken) return;
    const acknowledgement = { id: job.id, success: delivery?.success === true, message: delivery?.message || "网页未确认接收内容。",
      verifiedCount: Number.isInteger(delivery?.verifiedCount) ? delivery.verifiedCount : 0,
      expectedCount: Number.isInteger(delivery?.expectedCount) ? delivery.expectedCount : 0,
      promptVerified: delivery?.promptVerified === true };
    await chrome.storage.session.set({ pendingAck: acknowledgement });
    await post("/ack", acknowledgement, bridgeToken);
    await chrome.storage.session.remove(["pendingAck", "busyUntil"]);
  } catch (error) {
    if (!job) await chrome.storage.session.remove("busyUntil");
    else if (!error.pairingRevoked && await pairingToken() === bridgeToken) {
      const { pendingAck } = await chrome.storage.session.get("pendingAck");
      if (!pendingAck) await chrome.storage.session.set({ pendingAck: { id: job.id, success: false, message: error.message || "网页未确认接收内容。" } });
    }
    await chrome.storage.session.set({ lastError: error.message });
    // A claimed job is never silently repeated; the desktop timeout asks the user to inspect it.
  }
}

async function handleMessage(message, sender) {
  const fromOwnPopup = sender.id === chrome.runtime.id && !sender.tab && sender.url === chrome.runtime.getURL("popup.html");
  const fromAiPage = sender.id === chrome.runtime.id && sender.tab && isAiPage(sender.url || "");
  if (message.type === "BRIDGE_HEARTBEAT" && fromAiPage) {
    // Only during active delivery: keep the pending response alive while the
    // user decides whether to append, replace, or cancel an existing draft.
    return { success: true };
  }
  if (message.type === "BRIDGE_POLL" && fromAiPage) {
    await ensureRealtime();
    await pollBridge();
    return { success: true };
  }
  if (!fromOwnPopup) return { success: false, message: "不允许的请求来源。" };
  if (message.type === "BRIDGE_PAIR") {
    if (!/^\d{6}$/.test(message.code || "")) throw new Error("请输入 6 位数字配对码。");
    await prepareCredentialStore();
    const result = await post("/pair", { code: message.code });
    try { await changeCredential(() => chrome.storage.local.set({ bridgeToken: result.token })); }
    catch (error) {
      // The desktop has committed its credential. Roll it back if this side cannot remember it.
      try { await post("/disconnect", {}, result.token); } catch { /* Report failure; never claim a saved pairing. */ }
      throw new Error("浏览器无法保存连接，请检查扩展存储后重新配对；必要时在桌面重置配对。");
    }
    await chrome.storage.session.remove(["pendingAck", "busyUntil", "lastError"]);
    await chrome.alarms.create(POLL_ALARM, { periodInMinutes: 0.5 });
    stopRealtime();
    await ensureRealtime();
    await pollBridge();
    return { success: true, message: "已连接。现在可以从 ScreenNote 带入文字或截图。" };
  }
  if (message.type === "BRIDGE_STATUS") {
    const bridgeToken = await pairingToken();
    if (!bridgeToken) return { success: false, message: "尚未连接，请输入桌面工具的配对码。" };
    await ensureRealtime();
    const result = await post("/status", {}, bridgeToken);
    return { success: true, message: result.message };
  }
  if (message.type === "BRIDGE_DISCONNECT") {
    const bridgeToken = await pairingToken();
    // Keep the remembered credential if the desktop cannot confirm revocation.
    // Clearing only the browser side would leave a live desktop pairing after restart.
    try { if (bridgeToken) await post("/disconnect", {}, bridgeToken); }
    catch (error) { if (!error.pairingRevoked) throw error; }
    await forgetPairing(bridgeToken);
    await chrome.storage.session.remove("lastError");
    return { success: true, message: "已断开连接。" };
  }
  if (message.type === "OPEN_DEEPSEEK" || message.type === "OPEN_TARGET") {
    const target = message.type === "OPEN_DEEPSEEK" ? "deepseek" : message.target || "deepseek";
    await ensureTargetTab(target);
    return { success: true, message: `已打开 ${targetDetails(target).name}；请先登录。` };
  }
  return { success: false, message: "未知操作。" };
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  (async () => {
    try { sendResponse(await handleMessage(message || {}, sender)); }
    catch (error) { sendResponse({ success: false, message: error.message || "桌面工具未启动，或连接不可用。" }); }
  })();
  return true;
});

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name !== POLL_ALARM) return;
  void (async () => {
    try { await ensureRealtime(); await pollBridge(); }
    catch (error) { await chrome.storage.session.set({ lastError: error.message }); }
  })();
});

chrome.runtime.onInstalled.addListener(() => {
  void chrome.alarms.create(POLL_ALARM, { periodInMinutes: 0.5 });
});

chrome.runtime.onStartup.addListener(() => {
  void chrome.alarms.create(POLL_ALARM, { periodInMinutes: 0.5 });
  void ensureRealtime().catch(() => {});
});

// Restoring a suspended/reloaded worker does not require opening an AI page.
void ensureRealtime().catch(() => {});
