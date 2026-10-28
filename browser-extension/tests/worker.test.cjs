"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const fs = require("node:fs");
const path = require("node:path");

function workerHarness({ realtime = false, paired = false, savedLocal = null, savedSession = null } = {}) {
  const state = savedSession || {};
  const localState = savedLocal || (paired ? { bridgeToken: "token" } : {});
  const accessLevels = [];
  const requests = [];
  const tabEvents = [];
  const alarms = [];
  const sockets = [];
  const intervals = new Map();
  const timeouts = new Map();
  let timerId = 0;
  const callbacks = {};
  const extensionId = "a".repeat(32);
  const data = { job: null, unauthorized: false, tabs: [{ id: 42, windowId: 8, active: true }], delivery: { success: true, message: "草稿已放入" }, deliveryError: false };
  const chrome = {
    runtime: {
      id: extensionId, getURL: name => `chrome-extension://${extensionId}/${name}`,
      onMessage: { addListener: listener => { callbacks.message = listener; } },
      onInstalled: { addListener: listener => { callbacks.install = listener; } },
      onStartup: { addListener: listener => { callbacks.startup = listener; } }
    },
    storage: { local: {
      async get(keys) { return Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, localState[key]])); },
      async set(values) { if (data.storageFailure) throw new Error("storage unavailable"); Object.assign(localState, values); },
      async remove(keys) { if (data.removePause) { const pause = data.removePause; data.removePause = null; await pause; } for (const key of (Array.isArray(keys) ? keys : [keys])) delete localState[key]; },
      async setAccessLevel(options) { accessLevels.push(options.accessLevel); }
    }, session: {
      async get(keys) { return Object.fromEntries((Array.isArray(keys) ? keys : [keys]).map(key => [key, state[key]])); },
      async set(values) { Object.assign(state, values); },
      async remove(keys) { for (const key of (Array.isArray(keys) ? keys : [keys])) delete state[key]; },
      async clear() { for (const key of Object.keys(state)) delete state[key]; }
    } },
    alarms: {
      async create(name, settings) { alarms.push({ name, settings }); },
      onAlarm: { addListener: listener => { callbacks.alarm = listener; } }
    },
    tabs: {
      async query(query) { tabEvents.push(["query", query]); return data.tabs; },
      async update(id, props) { tabEvents.push(["update", id, props]); },
      async create(props) { tabEvents.push(["create", props]); return { id: 43, windowId: 8 }; },
      async sendMessage(id, message) {
        tabEvents.push(["message", id, message.type, message]);
        if (message.type === "SCREENNOTE_DELIVER" && data.deliveryError) throw new Error("tab reloaded after upload");
        return message.type === "SCREENNOTE_READY" ? { ready: true } : data.delivery;
      }
    },
    windows: { async update(id, props) { tabEvents.push(["window", id, props]); } }
  };
  const context = vm.createContext({
    chrome, AbortSignal, Date, console, URL,
    setTimeout: (callback, delay) => { if (!realtime) return setTimeout(callback, delay); const id = ++timerId; timeouts.set(id, { callback, delay }); return id; },
    clearTimeout: id => { timeouts.delete(id); if (!realtime) clearTimeout(id); },
    setInterval: (callback, delay) => { const id = ++timerId; intervals.set(id, { callback, delay }); return id; },
    clearInterval: id => intervals.delete(id),
    ...(realtime ? { WebSocket: class FakeWebSocket {
      static OPEN = 1;
      constructor(url) { this.url = url; this.readyState = 0; this.sent = []; sockets.push(this); }
      open() { this.readyState = 1; this.onopen?.(); }
      send(message) { this.sent.push(JSON.parse(message)); }
      emit(message) { this.onmessage?.({ data: JSON.stringify(message) }); }
      close() { this.readyState = 3; this.onclose?.(); }
    } } : {}),
    fetch: async (url, options) => {
      requests.push({ url, options, body: JSON.parse(options.body) });
      const route = new URL(url).pathname;
      if (data.fetchPause) { const pause = data.fetchPause; data.fetchPause = null; await pause; }
      if (data.offline) throw new Error("desktop offline");
      if (data.unauthorized) return { status: 401, ok: false, async json() { return { message: "expired" }; } };
      if (route === "/disconnect" && data.disconnectFailure) return { status: 500, ok: false, async json() { return { message: "cannot revoke saved pairing" }; } };
      const result = route === "/pair" ? { token: "token", message: "paired" }
        : route === "/poll" ? { job: data.job }
          : { message: "connected" };
      if (route === "/poll") data.job = null;
      if (route === "/poll" && data.pollPause) { const pause = data.pollPause; data.pollPause = null; await pause; }
      return { status: 200, ok: true, async json() { return result; } };
    }
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "service-worker.js"), "utf8"), context);
  return { context, state, localState, accessLevels, requests, tabEvents, alarms, callbacks, data, extensionId, sockets, intervals, timeouts };
}

test("pairing is popup-only and transport is scoped loopback without cookies", async () => {
  const { context, state, localState, accessLevels, requests, alarms, extensionId } = workerHarness();
  const rejected = await context.handleMessage({ type: "BRIDGE_PAIR", code: "123456" }, { id: extensionId, tab: { id: 1 }, url: "https://chat.deepseek.com/" });
  assert.equal(rejected.success, false);
  assert.equal(requests.length, 0);
  const result = await context.handleMessage({ type: "BRIDGE_PAIR", code: "123456" }, { id: extensionId, url: `chrome-extension://${extensionId}/popup.html` });
  assert.equal(result.success, true);
  assert.equal(localState.bridgeToken, "token");
  assert.equal(state.bridgeToken, undefined);
  assert.deepEqual(accessLevels, ["TRUSTED_CONTEXTS"]);
  assert.equal(alarms[0].settings.periodInMinutes, 0.5);
  assert.equal(requests[0].url, "http://127.0.0.1:18473/pair");
  assert.equal(requests[0].options.credentials, "omit");
  assert.equal(requests[0].options.headers["X-ScreenNote-Extension"], extensionId);
});

test("the extension-owned connection page also pairs when opened as a Quark tab", async () => {
  const h = workerHarness();
  h.context.chrome.runtime.getURL = name => `extension://${h.extensionId}/${name}`;
  const result = await h.context.handleMessage({ type: "BRIDGE_PAIR", code: "123456" }, { id: h.extensionId, tab: { id: 18 }, url: `extension://${h.extensionId}/popup.html` });
  assert.equal(result.success, true); assert.equal(h.localState.bridgeToken, "token");
  const blocked = await h.context.handleMessage({ type: "BRIDGE_PAIR", code: "123456" }, { id: h.extensionId, tab: { id: 18 }, url: "https://chat.deepseek.com/popup.html" });
  assert.equal(blocked.success, false);
});

test("batch count and explicit target survive delivery and acknowledgement", async () => {
  const h = workerHarness();
  h.localState.bridgeToken = "token";
  const images = [{ fileName: "one.png", imageBase64: "one" }, { fileName: "two.png", imageBase64: "two" }];
  h.data.job = { id: "images123", kind: "images", target: "chatgpt", images, expiresAt: Date.now() + 60000 };
  h.data.delivery = { success: true, verifiedCount: 2, expectedCount: 2, message: "全部 2 张附件" };
  await h.context.pollBridge();
  assert.equal(h.tabEvents.find(event => event[0] === "query")[1].url, "https://chatgpt.com/*");
  const delivered = h.tabEvents.find(event => event[2] === "SCREENNOTE_DELIVER")[3].job;
  assert.equal(delivered.target, "chatgpt");
  assert.equal(delivered.images, images);
  const ack = h.requests.find(request => new URL(request.url).pathname === "/ack").body;
  assert.equal(ack.success, true);
  assert.equal(ack.verifiedCount, 2);
});

test("partial batch remains failure with confirmed count intact", async () => {
  const h = workerHarness();
  h.localState.bridgeToken = "token";
  h.data.job = { id: "partial", kind: "images", target: "doubao", expiresAt: Date.now() + 60000 };
  h.data.delivery = { success: false, verifiedCount: 1, expectedCount: 3, message: "只确认 1/3，缺 two.png、three.png" };
  await h.context.pollBridge();
  const ack = h.requests.find(request => new URL(request.url).pathname === "/ack").body;
  assert.equal(ack.success, false);
  assert.equal(ack.verifiedCount, 1);
  assert.equal(ack.expectedCount, 3);
  assert.match(ack.message, /two\.png/);
});

test("target without a tab opens only that official AI website", async () => {
  const h = workerHarness();
  h.data.tabs = [];
  await h.context.ensureTargetTab("doubao");
  assert.equal(h.tabEvents.find(event => event[0] === "create")[1].url, "https://www.doubao.com/chat/");
  await assert.rejects(h.context.ensureTargetTab("https://attacker.example/"));
  assert.equal(h.tabEvents.filter(event => event[0] === "create").length, 1);
});

test("an existing AI tab without a receiver is reconnected once before delivery", async () => {
  const h = workerHarness();
  let injected = 0, delivered = 0;
  h.context.chrome.scripting = { async executeScript(options) { injected++; assert.equal(options.target.tabId, 42); assert.deepEqual(Array.from(options.files), ["content.js"]); } };
  h.context.chrome.tabs.sendMessage = async (_, message) => {
    if (message.type === "SCREENNOTE_READY") { if (injected === 0) throw new Error("Receiving end does not exist"); return { ready: true }; }
    delivered++; return { success: true, verifiedCount: 1 };
  };
  const result = await h.context.deliverToTab(42, { id: "existingtab", target: "deepseek", expiresAt: Date.now() + 3000 });
  assert.equal(result.success, true); assert.equal(injected, 1); assert.equal(delivered, 1);
});

test("failed delivery reply after a page refresh does not attempt the batch twice", async () => {
  const h = workerHarness();
  h.localState.bridgeToken = "token";
  h.data.job = { id: "refresh", kind: "images", target: "deepseek", expiresAt: Date.now() + 60000 };
  h.data.deliveryError = true;
  await h.context.pollBridge();
  assert.equal(h.tabEvents.filter(event => event[2] === "SCREENNOTE_DELIVER").length, 1);
  assert.equal(h.requests.find(request => new URL(request.url).pathname === "/ack").body.success, false);
});

test("content script sender must use an exact approved origin and Doubao chat path", async () => {
  const h = workerHarness();
  for (const url of ["https://chatgpt.com/", "https://www.doubao.com/chat/thread/123", "https://chat.deepseek.com/"]) {
    const result = await h.context.handleMessage({ type: "BRIDGE_HEARTBEAT" }, { id: h.extensionId, tab: { id: 42 }, url });
    assert.equal(result.success, true);
  }
  for (const url of ["https://chatgpt.com.attacker.example/", "http://chatgpt.com/", "https://www.doubao.com/legal/", "https://attacker.example/"]) {
    const result = await h.context.handleMessage({ type: "BRIDGE_HEARTBEAT" }, { id: h.extensionId, tab: { id: 42 }, url });
    assert.equal(result.success, false);
  }
  assert.equal(h.requests.length, 0);
});

test("dormant polling focuses existing tab, inserts job, then acknowledges observable result", async () => {
  const { context, state, localState, requests, tabEvents, data } = workerHarness();
  localState.bridgeToken = "token";
  data.job = { id: "job123", kind: "text", text: "内容", expiresAt: Date.now() + 60000 };
  await context.pollBridge();
  assert.equal(tabEvents.find(event => event[0] === "update")[1], 42);
  assert.equal(tabEvents.filter(event => event[0] === "create").length, 0);
  assert.equal(requests.map(request => new URL(request.url).pathname).join(","), "/poll,/ack");
  assert.equal(requests[1].body.id, "job123");
  assert.equal(requests[1].body.success, true);
  assert.equal(state.pendingAck, undefined);
  assert.equal(state.busyUntil, undefined);
});

test("authentication failure removes durable and transient credentials and locks", async () => {
  const { context, state, localState, data } = workerHarness();
  localState.bridgeToken = "token";
  Object.assign(state, { bridgeToken: "legacy", busyUntil: 1, pendingAck: { id: "old" } });
  data.unauthorized = true;
  await assert.rejects(context.post("/status", {}, "token"));
  assert.equal(state.bridgeToken, undefined);
  assert.equal(state.pendingAck, undefined);
  assert.equal(state.busyUntil, undefined);
  assert.equal(localState.bridgeToken, undefined);
});

test("visible active delivery heartbeat has no desktop or AI requests", async () => {
  const { context, requests, extensionId } = workerHarness();
  const result = await context.handleMessage({ type: "BRIDGE_HEARTBEAT" }, { id: extensionId, tab: { id: 42 }, url: "https://chat.deepseek.com/" });
  assert.equal(result.success, true);
  assert.equal(requests.length, 0);
});

test("real-time notification opens target without an AI page or waiting for an alarm", async () => {
  const h = workerHarness({ realtime: true });
  h.data.tabs = [];
  await h.context.handleMessage({ type: "BRIDGE_PAIR", code: "123456" }, { id: h.extensionId, url: `chrome-extension://${h.extensionId}/popup.html` });
  const socket = h.sockets[0];
  assert.equal(socket.url, "ws://127.0.0.1:18473/events");
  assert.equal(new URL(socket.url).search, "");
  socket.open();
  assert.deepEqual(socket.sent[0], { type: "auth", token: "token", extensionId: h.extensionId });
  h.data.job = { id: "immediate", kind: "images", target: "deepseek", expiresAt: Date.now() + 60000 };
  h.data.delivery = { success: true, verifiedCount: 2, expectedCount: 2, message: "两张附件" };
  socket.emit({ type: "job", id: "immediate", target: "deepseek" });
  await h.context.pollBridge();
  assert.equal(h.tabEvents.find(event => event[0] === "create")[1].url, "https://chat.deepseek.com/");
  assert.equal(h.tabEvents.filter(event => event[2] === "SCREENNOTE_DELIVER").length, 1);
  assert.equal(h.requests.find(request => new URL(request.url).pathname === "/ack").body.verifiedCount, 2);
  assert.equal(h.timeouts.size, 0);
});

test("MV3 exchanges a heartbeat every twenty seconds and cleans up on disconnect", async () => {
  const h = workerHarness({ realtime: true, paired: true });
  h.localState.bridgeToken = "token";
  await h.context.ensureRealtime();
  h.sockets[0].open();
  const heartbeat = [...h.intervals.values()][0];
  assert.equal(heartbeat.delay, 20000);
  heartbeat.callback();
  assert.deepEqual(h.sockets[0].sent[1], { type: "ping" });
  await h.context.handleMessage({ type: "BRIDGE_DISCONNECT" }, { id: h.extensionId, url: `chrome-extension://${h.extensionId}/popup.html` });
  assert.equal(h.intervals.size, 0);
  assert.equal(h.timeouts.size, 0);
  assert.equal(h.sockets[0].readyState, 3);
  assert.equal(h.state.bridgeToken, undefined);
  assert.equal(h.localState.bridgeToken, undefined);
});

test("browser restart restores the trusted local credential without pairing again", async () => {
  const first = workerHarness({ realtime: true });
  const sender = { id: first.extensionId, url: `chrome-extension://${first.extensionId}/popup.html` };
  await first.context.handleMessage({ type: "BRIDGE_PAIR", code: "123456" }, sender);
  first.context.stopRealtime();
  const restarted = workerHarness({ realtime: true, savedLocal: first.localState });
  await restarted.context.ensureRealtime();
  assert.equal(restarted.sockets.length, 1);
  restarted.sockets[0].open();
  assert.deepEqual(restarted.sockets[0].sent[0], { type: "auth", token: "token", extensionId: restarted.extensionId });
  assert.deepEqual(restarted.accessLevels, ["TRUSTED_CONTEXTS"]);
  assert.equal(restarted.requests.filter(request => new URL(request.url).pathname === "/pair").length, 0);
  assert.equal(restarted.state.bridgeToken, undefined);
  restarted.context.stopRealtime();
});

test("old session credential migrates once without overwriting a saved local pairing", async () => {
  const legacy = workerHarness({ realtime: true, savedSession: { bridgeToken: "legacy" } });
  await legacy.context.ensureRealtime();
  assert.equal(legacy.localState.bridgeToken, "legacy");
  assert.equal(legacy.state.bridgeToken, undefined);
  legacy.context.stopRealtime();
  const current = workerHarness({ realtime: true, savedLocal: { bridgeToken: "current" }, savedSession: { bridgeToken: "obsolete" } });
  await current.context.ensureRealtime();
  assert.equal(current.localState.bridgeToken, "current");
  assert.equal(current.state.bridgeToken, undefined);
  current.context.stopRealtime();
});

test("offline desktop preserves remembered pairing for automatic reconnect", async () => {
  const h = workerHarness({ realtime: true, paired: true });
  await h.context.ensureRealtime();
  h.sockets[0].open();
  h.data.offline = true;
  await assert.rejects(h.context.handleMessage({ type: "BRIDGE_STATUS" }, { id: h.extensionId, url: `chrome-extension://${h.extensionId}/popup.html` }), /offline/);
  assert.equal(h.localState.bridgeToken, "token");
  h.sockets[0].close();
  assert.equal([...h.timeouts.values()][0].delay, 1000);
  h.context.stopRealtime();
});

test("offline startup checks native status without creating an erroring WebSocket and recovers with saved pairing", async () => {
  const h = workerHarness({ realtime: true });
  h.localState.bridgeToken = "token";
  h.data.offline = true;
  await h.context.ensureRealtime();
  assert.equal(h.sockets.length, 0);
  assert.equal(h.localState.bridgeToken, "token");
  assert.match(h.state.lastError, /已保存配对/);
  const attempts = h.requests.length;
  await h.context.ensureRealtime();
  assert.equal(h.requests.length, attempts, "page polling respects reconnect backoff");
  h.data.offline = false;
  const reconnect = [...h.timeouts.values()][0];
  reconnect.callback();
  await h.context.ensureRealtime();
  assert.equal(h.sockets.length, 1);
  assert.equal(h.state.lastError, undefined);
  assert.equal(h.requests.filter(request => new URL(request.url).pathname === "/pair").length, 0);
  h.context.stopRealtime();
});

test("revoked pairing never opens a WebSocket after authenticated preflight", async () => {
  const h = workerHarness({ realtime: true });
  h.localState.bridgeToken = "token";
  h.data.unauthorized = true;
  await h.context.ensureRealtime();
  assert.equal(h.sockets.length, 0);
  assert.equal(h.localState.bridgeToken, undefined);
  assert.equal(h.timeouts.size, 0);
});

test("401 clears persistent authorization and stops socket heartbeat and reconnect", async () => {
  const h = workerHarness({ realtime: true, paired: true });
  await h.context.ensureRealtime();
  h.sockets[0].open();
  h.data.unauthorized = true;
  await assert.rejects(h.context.post("/status", {}, "token"), /失效/);
  await h.context.ensureRealtime();
  assert.equal(h.localState.bridgeToken, undefined);
  assert.equal(h.sockets.length, 1);
  assert.equal(h.intervals.size, 0);
  assert.equal(h.timeouts.size, 0);
});

test("late unauthorized response cannot erase a newer pairing", async () => {
  const h = workerHarness({ paired: true });
  let release;
  h.data.fetchPause = new Promise(resolve => { release = resolve; });
  const request = h.context.post("/status", {}, "token");
  await new Promise(resolve => setImmediate(resolve));
  h.localState.bridgeToken = "new-token";
  h.data.unauthorized = true;
  release();
  await assert.rejects(request);
  assert.equal(h.localState.bridgeToken, "new-token");
});

test("credential removal and a new saved pairing serialize without losing the new token", async () => {
  const h = workerHarness({ paired: true });
  await h.context.prepareCredentialStore();
  let release;
  h.data.removePause = new Promise(resolve => { release = resolve; });
  const removal = h.context.forgetPairing("token");
  await new Promise(resolve => setImmediate(resolve));
  const save = h.context.changeCredential(() => h.context.chrome.storage.local.set({ bridgeToken: "new-token" }));
  release();
  await Promise.all([removal, save]);
  assert.equal(h.localState.bridgeToken, "new-token");
});

test("failed native revocation preserves local authorization and reports failure", async () => {
  const h = workerHarness({ paired: true });
  h.data.disconnectFailure = true;
  await assert.rejects(h.context.handleMessage({ type: "BRIDGE_DISCONNECT" }, { id: h.extensionId, url: `chrome-extension://${h.extensionId}/popup.html` }), /cannot revoke/);
  assert.equal(h.localState.bridgeToken, "token");
  h.data.disconnectFailure = false;
  const result = await h.context.handleMessage({ type: "BRIDGE_DISCONNECT" }, { id: h.extensionId, url: `chrome-extension://${h.extensionId}/popup.html` });
  assert.equal(result.success, true);
  assert.equal(h.localState.bridgeToken, undefined);
});

test("unsaved browser pairing is rolled back instead of reporting successful persistence", async () => {
  const h = workerHarness();
  h.data.storageFailure = true;
  await assert.rejects(h.context.handleMessage({ type: "BRIDGE_PAIR", code: "123456" }, { id: h.extensionId, url: `chrome-extension://${h.extensionId}/popup.html` }), /无法保存/);
  assert.equal(h.localState.bridgeToken, undefined);
  assert.deepEqual(h.requests.map(request => new URL(request.url).pathname), ["/pair", "/disconnect"]);
});

test("overlapping alarm, page, and realtime polls claim and upload only once", async () => {
  const h = workerHarness();
  h.localState.bridgeToken = "token";
  h.data.job = { id: "once", kind: "image", target: "deepseek", expiresAt: Date.now() + 60000 };
  await Promise.all([h.context.pollBridge(), h.context.pollBridge(), h.context.pollBridge()]);
  assert.equal(h.requests.filter(request => new URL(request.url).pathname === "/poll").length, 1);
  assert.equal(h.tabEvents.filter(event => event[2] === "SCREENNOTE_DELIVER").length, 1);
});

test("socket reconnect restores pending delivery without automatic duplicate upload", async () => {
  const h = workerHarness({ realtime: true, paired: true });
  h.localState.bridgeToken = "token";
  await h.context.ensureRealtime();
  h.sockets[0].open();
  h.sockets[0].close();
  const retry = [...h.timeouts.values()][0];
  assert.equal(retry.delay, 1000);
  retry.callback();
  await h.context.ensureRealtime();
  assert.equal(h.sockets.length, 2);
  h.sockets[1].open();
  h.data.job = { id: "recovered", kind: "image", target: "deepseek", expiresAt: Date.now() + 60000 };
  h.sockets[1].emit({ type: "ready" });
  h.sockets[1].emit({ type: "job" });
  await h.context.pollBridge();
  assert.equal(h.tabEvents.filter(event => event[2] === "SCREENNOTE_DELIVER").length, 1);
  h.context.stopRealtime();
});

test("notification during an empty poll is not lost until the next alarm", async () => {
  const h = workerHarness({ realtime: true, paired: true });
  await h.context.ensureRealtime();
  h.sockets[0].open();
  let release;
  h.data.pollPause = new Promise(resolve => { release = resolve; });
  h.sockets[0].emit({ type: "ready" });
  await new Promise(resolve => setImmediate(resolve));
  h.data.job = { id: "arrivedDuringPoll", kind: "image", target: "deepseek", expiresAt: Date.now() + 60000 };
  h.sockets[0].emit({ type: "job" });
  release();
  await h.context.pollBridge();
  assert.equal(h.tabEvents.filter(event => event[2] === "SCREENNOTE_DELIVER").length, 1);
  assert.equal(h.requests.filter(request => new URL(request.url).pathname === "/poll").length, 2);
  h.context.stopRealtime();
});

test("abandoned worker session timer does not block a fresh native job", async () => {
  const h = workerHarness();
  h.localState.bridgeToken = "token";
  h.state.busyUntil = Date.now() + 70000;
  h.data.job = { id: "afterRestart", kind: "image", target: "deepseek", expiresAt: Date.now() + 60000 };
  await h.context.pollBridge();
  assert.equal(h.tabEvents.filter(event => event[2] === "SCREENNOTE_DELIVER").length, 1);
});
