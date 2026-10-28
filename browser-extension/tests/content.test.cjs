"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const fs = require("node:fs");
const path = require("node:path");

const PNG_BASE64 = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 0, 1, 2]).toString("base64");

function harness(initial = "", host = "chat.deepseek.com") {
  class FakeTextArea {
    constructor() { this._value = initial; this.disabled = false; this.readOnly = false; this.events = []; }
    get value() { return this._value; }
    set value(value) { this._value = value; }
    getBoundingClientRect() { return { width: 400, height: 80, bottom: 700 }; }
    getAttribute(name) { return name === "placeholder" ? "发送消息" : ""; }
    closest() { return null; }
    focus() { this.focused = true; }
    setSelectionRange(start, end) { this.selection = [start, end]; }
    dispatchEvent(event) { this.events.push(event.type); return !this.cancelBeforeInput || event.type !== "beforeinput"; }
  }
  class FakeEvent { constructor(type, options) { this.type = type; Object.assign(this, options); } }
  const editor = new FakeTextArea();
  const uploads = [];
  const attachments = new Set();
  const editors = [editor];
  const listeners = [];
  const document = {
    hidden: true,
    querySelectorAll: selector => selector.startsWith("textarea") ? editors : selector === 'input[type="file"]' ? uploads : [],
    addEventListener() {},
    body: {},
    createTreeWalker: () => ({ nextNode: () => null })
  };
  const context = vm.createContext({
    document, HTMLTextAreaElement: FakeTextArea, InputEvent: FakeEvent, Event: FakeEvent,
    getComputedStyle: () => ({ display: "block", visibility: "visible" }),
    requestAnimationFrame: callback => callback(), setTimeout, clearTimeout, setInterval() {},
    chrome: { runtime: { id: "a".repeat(32), onMessage: { addListener: listener => listeners.push(listener) } } },
    location: { protocol: "https:", hostname: host, pathname: host === "www.doubao.com" ? "/chat/" : "/" },
    File: class { constructor(parts, name, options) { this.parts = parts; this.name = name; this.type = options.type; } },
    DataTransfer: class { constructor() { this.files = []; this.items = { add: file => this.files.push(file) }; } },
    atob: value => Buffer.from(value, "base64").toString("binary"), Uint8Array,
    NodeFilter: { SHOW_TEXT: 4 }, Date, Number, console
  });
  const source = fs.readFileSync(path.join(__dirname, "..", "content.js"), "utf8");
  const names = [...source.matchAll(/(?:async )?function (screenNote\w+)\(/g)].map(match => match[1]);
  vm.runInContext(source.replace(/\}\)\(\);\s*$/, `${names.map(name => `Object.defineProperty(globalThis, "${name}", { get: () => ${name}, set: value => { ${name} = value; }, configurable: true });`).join("\n")}\n})();`), context);
  context.screenNoteShowToast = () => {};
  return { context, editor, editors, listeners, uploads, attachments };
}

test("Chinese multiline text survives native setter and input verification, without keypress or submit", async () => {
  const { context, editor } = harness();
  const text = "产品规划\nOCR 编辑\nEnglish 123";
  const result = await context.screenNoteDeliverText({ text, draftMode: "append" });
  assert.equal(result.success, true);
  assert.equal(editor.value, text);
  assert.deepEqual(editor.events, ["beforeinput", "input", "change"]);
  assert.deepEqual(editor.selection, [text.length, text.length]);
});

test("existing draft append, replace, and cancel preserve the chosen behavior", async () => {
  for (const [mode, expected] of [["append", "原草稿\n\n新内容"], ["replace", "新内容"], ["cancel", "原草稿"]]) {
    const { context, editor } = harness("原草稿");
    context.screenNoteChooseDraftMode = async () => mode;
    const result = await context.screenNoteDeliverText({ text: "新内容", draftMode: "append" });
    assert.equal(editor.value, expected);
    assert.equal(result.success, mode !== "cancel");
    if (mode === "cancel") assert.equal(editor.events.length, 0);
  }
});

test("framework rejection is returned as failure, not successful delivery", async () => {
  const { context, editor } = harness();
  const original = editor.dispatchEvent.bind(editor);
  editor.dispatchEvent = event => { const result = original(event); if (event.type === "input") editor.value = ""; return result; };
  const result = await context.screenNoteDeliverText({ text: "不能保留" });
  assert.equal(result.success, false);
});

test("cancelled beforeinput does not modify the input", async () => {
  const { context, editor } = harness();
  editor.cancelBeforeInput = true;
  const result = await context.screenNoteDeliverText({ text: "受保护输入框" });
  assert.equal(result.success, false);
  assert.equal(editor.value, "");
});

test("hidden or read-only inputs are not chosen", () => {
  const { context, editor } = harness();
  editor.readOnly = true;
  assert.equal(context.screenNoteFindEditor(), null);
  editor.readOnly = false;
  editor.getBoundingClientRect = () => ({ width: 0, height: 0, bottom: 0 });
  assert.equal(context.screenNoteFindEditor(), null);
});

test("missing upload entry reports failure; a file property alone never confirms attachment", async () => {
  const { context } = harness();
  const result = await context.screenNoteDeliverImage({ id: "abc", imageBase64: PNG_BASE64 });
  assert.equal(result.success, false);
  assert.equal(context.screenNoteAttachmentVisible("screennote-test.png"), false);
});

test("DeepSeek lazy upload composer accepts a PNG paste and preserves the existing text draft", async () => {
  const h = harness("保留现有草稿");
  h.context.ClipboardEvent = class { constructor(type, options) { this.type = type; Object.assign(this, options); } };
  let pastes = 0;
  const dispatch = h.editor.dispatchEvent.bind(h.editor);
  h.editor.dispatchEvent = event => {
    if (event.type === "paste") { pastes++; for (const file of event.clipboardData.files) h.attachments.add(file.name); }
    return dispatch(event);
  };
  h.context.screenNoteAttachmentVisible = name => h.attachments.has(name);
  const result = await h.context.screenNoteDeliverImage(imageJob(2));
  assert.equal(result.success, true);
  assert.equal(result.verifiedCount, 2);
  assert.equal(pastes, 1);
  assert.equal(h.editor.value, "保留现有草稿");
  assert.ok(!h.editor.events.includes("submit"));
});

test("unhandled image paste is not acknowledged and is not retried as a duplicate upload", async () => {
  const h = harness();
  h.context.ClipboardEvent = class { constructor(type, options) { this.type = type; Object.assign(this, options); } };
  const job = imageJob(1); job.expiresAt = Date.now() + 1;
  const result = await h.context.screenNoteDeliverImage(job);
  assert.equal(result.success, false);
  assert.equal(result.verifiedCount, 0);
  assert.equal(h.editor.events.filter(type => type === "paste").length, 1);
});

test("reinjection does not register another listener or reset the delivery lock", () => {
  const h = harness();
  const source = fs.readFileSync(path.join(__dirname, "..", "content.js"), "utf8");
  vm.runInContext(source, h.context);
  assert.equal(h.listeners.length, 1);
});

function addUpload(h, multiple, onChange) {
  const input = { accept: "image/*", multiple, disabled: false, files: [], dispatchEvent(event) {
    assert.equal(event.type, "change");
    onChange([...input.files]);
    return true;
  } };
  h.uploads.push(input);
  h.context.screenNoteAttachmentVisible = name => h.attachments.has(name);
  return input;
}

function imageJob(count, target = "deepseek") {
  return { id: "batch123", kind: "images", target, expiresAt: Date.now() + 60000,
    images: Array.from({ length: count }, (_, index) => ({ fileName: `screennote-batch123-${index + 1}.png`, imageBase64: PNG_BASE64 })) };
}

test("multiple-file composer receives separate ordered PNGs in one change and confirms every filename", async () => {
  const h = harness();
  const deliveries = [];
  addUpload(h, true, files => { deliveries.push(files); for (const file of files) h.attachments.add(file.name); });
  const result = await h.context.screenNoteDeliverImage(imageJob(3));
  assert.equal(result.success, true);
  assert.equal(result.verifiedCount, 3);
  assert.equal(deliveries.length, 1);
  assert.deepEqual(deliveries[0].map(file => file.name), ["screennote-batch123-1.png", "screennote-batch123-2.png", "screennote-batch123-3.png"]);
  assert.equal(new Set(deliveries[0].map(file => file.type)).has("image/png"), true);
});

test("single-file composer appends one image at a time without replacing preceding attachments", async () => {
  const h = harness();
  const deliveries = [];
  addUpload(h, false, files => { deliveries.push(files); for (const file of files) h.attachments.add(file.name); });
  const result = await h.context.screenNoteDeliverImage(imageJob(3));
  assert.equal(result.success, true);
  assert.equal(result.verifiedCount, 3);
  assert.deepEqual(deliveries.map(files => files.length), [1, 1, 1]);
});

test("partial batch reports exact missing filenames and is never acknowledged as complete", async () => {
  const h = harness();
  addUpload(h, true, files => h.attachments.add(files[0].name));
  const job = imageJob(3);
  job.expiresAt = Date.now() + 5;
  const result = await h.context.screenNoteDeliverImage(job);
  assert.equal(result.success, false);
  assert.equal(result.verifiedCount, 1);
  assert.equal(result.expectedCount, 3);
  assert.equal(Array.from(result.missingNames).join(","), "screennote-batch123-2.png,screennote-batch123-3.png");
  assert.match(result.message, /1\/3/);
});

test("single-file composer which replaces an earlier image stops and reports the lost attachment", async () => {
  const h = harness();
  const deliveries = [];
  addUpload(h, false, files => { deliveries.push(files); h.attachments.clear(); h.attachments.add(files[0].name); });
  const job = imageJob(3);
  job.expiresAt = Date.now() + 5;
  const result = await h.context.screenNoteDeliverImage(job);
  assert.equal(result.success, false);
  assert.equal(deliveries.length, 2);
  assert.equal(result.verifiedCount, 1);
  assert.equal(Array.from(result.missingNames).includes("screennote-batch123-1.png"), true);
});

test("legacy single-image payload still uploads with its generated filename", async () => {
  const h = harness();
  addUpload(h, false, files => h.attachments.add(files[0].name));
  const result = await h.context.screenNoteDeliverImage({ id: "oldjob12", imageBase64: PNG_BASE64 });
  assert.equal(result.success, true);
  assert.equal(Array.from(result.confirmedNames).join(","), "screennote-oldjob12.png");
});

test("oversized, duplicate-name, empty, and invalid PNG batches are rejected before any upload", async () => {
  const h = harness();
  let uploads = 0;
  addUpload(h, true, () => uploads++);
  const duplicate = imageJob(2);
  duplicate.images[1].fileName = duplicate.images[0].fileName;
  for (const job of [imageJob(0), imageJob(11), duplicate, { ...imageJob(1), images: [{ imageBase64: "eA==" }] }]) {
    await assert.rejects(h.context.screenNoteDeliverImage(job));
  }
  assert.equal(uploads, 0);
});

test("search fields are excluded even when their data-placeholder is lower than the composer", () => {
  const h = harness();
  const search = Object.create(Object.getPrototypeOf(h.editor));
  search.getAttribute = name => name === "data-placeholder" ? "Search chats" : "";
  search.getBoundingClientRect = () => ({ width: 400, height: 80, bottom: 1500 });
  h.editors.push(search);
  assert.equal(h.context.screenNoteFindEditor(), h.editor);
});

test("selected AI target must match current page before the draft changes", async () => {
  const h = harness("", "chatgpt.com");
  const result = await new Promise(resolve => h.listeners[0]({ type: "SCREENNOTE_DELIVER", job: { id: "crossjob", kind: "text", text: "不可跨站", target: "deepseek", expiresAt: Date.now() + 60000 } }, { id: "a".repeat(32) }, resolve));
  assert.equal(result.success, false);
  assert.equal(h.editor.value, "");
});

test("target-specific text protocol delivers to the selected ChatGPT composer", async () => {
  const h = harness("", "chatgpt.com");
  const result = await new Promise(resolve => h.listeners[0]({ type: "SCREENNOTE_DELIVER", job: { id: "chatjob", kind: "target-text", text: "选定目标文字", target: "chatgpt", expiresAt: Date.now() + 60000 } }, { id: "a".repeat(32) }, resolve));
  assert.equal(result.success, true);
  assert.equal(h.editor.value, "选定目标文字");
  assert.match(result.message, /ChatGPT/);
});

test("expired job is rejected before content is inserted", async () => {
  const { editor, listeners } = harness();
  const result = await new Promise(resolve => listeners[0]({ type: "SCREENNOTE_DELIVER", job: { id: "abc", kind: "text", text: "过期", expiresAt: Date.now() - 1 } }, { id: "a".repeat(32) }, resolve));
  assert.equal(result.success, false);
  assert.equal(editor.value, "");
});

test("translation prompt and every image are verified together without submitting the chat", async () => {
  const h = harness();
  addUpload(h, true, files => files.forEach(file => h.attachments.add(file.name)));
  const result = await h.context.screenNoteDeliverJob({ ...imageJob(2), text: "请翻译附图", draftMode: "append" });
  assert.equal(result.success, true);
  assert.equal(result.promptVerified, true);
  assert.equal(result.verifiedCount, 2);
  assert.equal(h.editor.value, "请翻译附图");
  assert.ok(!h.editor.events.includes("submit"));
});

test("cancelled prompt keeps the existing draft and does not start any image upload", async () => {
  const h = harness("已有草稿");
  h.context.screenNoteChooseDraftMode = async () => "cancel";
  let uploads = 0;
  addUpload(h, true, () => uploads++);
  const result = await h.context.screenNoteDeliverJob({ ...imageJob(2), text: "总结" });
  assert.equal(result.success, false);
  assert.equal(result.promptVerified, false);
  assert.equal(h.editor.value, "已有草稿");
  assert.equal(uploads, 0);
});

test("partial attachments keep the prompt but never report the combined task as complete", async () => {
  const h = harness();
  h.context.screenNoteDeliverImage = async () => ({ success: false, verifiedCount: 1, expectedCount: 2, message: "只有一张已确认" });
  const result = await h.context.screenNoteDeliverJob({ ...imageJob(2), text: "总结" });
  assert.equal(result.success, false);
  assert.equal(result.promptVerified, true);
  assert.equal(result.verifiedCount, 1);
  assert.equal(h.editor.value, "总结");
});
