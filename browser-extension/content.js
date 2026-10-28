(() => {
"use strict";
if (globalThis.__screenNoteContentVersion === "0.7.0") return;
globalThis.__screenNoteContentVersion = "0.7.0";


let screenNoteDelivering = false;
let screenNotePolling = false;

function screenNoteTargetId() {
  if (location.protocol !== "https:") return null;
  if (location.hostname === "chat.deepseek.com") return "deepseek";
  if (location.hostname === "chatgpt.com") return "chatgpt";
  if (location.hostname === "www.doubao.com" && location.pathname.startsWith("/chat/")) return "doubao";
  return null;
}

function screenNoteTargetName(target) {
  return ({ deepseek: "DeepSeek", chatgpt: "ChatGPT", doubao: "豆包" })[target] || "AI";
}

function screenNoteVisible(element) {
  if (!element || element.disabled || element.readOnly) return false;
  const rect = element.getBoundingClientRect();
  const style = getComputedStyle(element);
  return rect.width > 0 && rect.height > 0 && style.display !== "none" && style.visibility !== "hidden";
}

function screenNoteFindEditor() {
  const candidates = [...document.querySelectorAll('textarea, [contenteditable="true"]')];
  const valid = candidates.filter(element => {
    const hint = ["placeholder", "data-placeholder", "aria-label", "id", "data-testid"].map(name => element.getAttribute(name) || "").join(" ");
    return screenNoteVisible(element) && !/搜索|search/i.test(hint) && !element.closest('[data-screennote-ui], [role="dialog"], [role="search"]');
  });
  valid.sort((left, right) => {
    const score = element => {
      const hint = [element.getAttribute("placeholder"), element.getAttribute("data-placeholder"), element.getAttribute("aria-label")].join(" ");
      return (/发送|询问|消息|提问|send|message|ask|prompt/i.test(hint) || element.id === "prompt-textarea" ? 1000 : 0) + element.getBoundingClientRect().bottom;
    };
    return score(right) - score(left);
  });
  return valid[0] || null;
}

function screenNoteEditorText(editor) {
  return editor instanceof HTMLTextAreaElement ? editor.value : (editor.innerText || editor.textContent || "");
}

async function screenNoteSetEditor(editor, text) {
  editor.focus();
  const before = new InputEvent("beforeinput", { bubbles: true, cancelable: true, inputType: "insertText", data: text });
  if (!editor.dispatchEvent(before)) return false;
  await new Promise(resolve => requestAnimationFrame(() => {
    if (editor instanceof HTMLTextAreaElement) {
      // Use the prototype setter so controlled framework inputs observe the change.
      const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set;
      setter.call(editor, text);
      editor.setSelectionRange(text.length, text.length);
    } else {
      editor.textContent = text;
      const selection = window.getSelection();
      const range = document.createRange();
      range.selectNodeContents(editor);
      range.collapse(false);
      selection.removeAllRanges();
      selection.addRange(range);
    }
    editor.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: text }));
    editor.dispatchEvent(new Event("change", { bubbles: true }));
    resolve();
  }));
  await new Promise(resolve => setTimeout(resolve, 200));
  return screenNoteEditorText(editor).replace(/\r\n/g, "\n") === text.replace(/\r\n/g, "\n");
}

function screenNoteShowToast(message) {
  const host = document.createElement("div");
  host.dataset.screennoteUi = "toast";
  const shadow = host.attachShadow({ mode: "closed" });
  const style = document.createElement("style");
  style.textContent = ":host{position:fixed;right:24px;bottom:24px;z-index:2147483647}.toast{max-width:380px;padding:14px 18px;border-radius:12px;background:#203d32;color:white;font:14px/1.5 system-ui,sans-serif;box-shadow:0 12px 40px #0003}";
  const toast = document.createElement("div");
  toast.className = "toast";
  toast.setAttribute("role", "status");
  toast.textContent = message;
  shadow.append(style, toast);
  document.documentElement.append(host);
  setTimeout(() => host.remove(), 6000);
}

function screenNoteChooseDraftMode() {
  return new Promise(resolve => {
    const host = document.createElement("div");
    host.dataset.screennoteUi = "draft";
    const shadow = host.attachShadow({ mode: "closed" });
    const style = document.createElement("style");
    style.textContent = ":host{position:fixed;inset:0;z-index:2147483647;background:#12221d70;display:grid;place-items:center}.panel{width:360px;box-sizing:border-box;padding:24px;border-radius:18px;background:#f7faf8;color:#243b31;box-shadow:0 20px 80px #0004;font:14px/1.65 system-ui,sans-serif}h2{font-size:18px;margin:0 0 10px}p{margin:0 0 18px;color:#607368}.buttons{display:flex;gap:8px;flex-wrap:wrap}button{padding:10px 14px;border:0;border-radius:8px;background:#237a60;color:white;cursor:pointer;font:inherit}.secondary{background:#e0e9e3;color:#365846}button:focus-visible{outline:2px solid #42a384;outline-offset:3px}";
    const panel = document.createElement("div");
    panel.className = "panel";
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-modal", "true");
    const title = document.createElement("h2");
    title.textContent = "输入框里已有草稿";
    const paragraph = document.createElement("p");
    paragraph.textContent = "选择如何放入这次的文字。内容不会自动发送；30 秒未选择将取消。";
    const buttons = document.createElement("div");
    buttons.className = "buttons";
    let finished = false;
    const finish = mode => {
      if (finished) return;
      finished = true;
      clearTimeout(timeout);
      document.removeEventListener("keydown", onKey, true);
      host.remove();
      resolve(mode);
    };
    const onKey = event => {
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); finish("cancel"); }
      if (event.key === "Tab") {
        const controls = [...buttons.querySelectorAll("button")];
        const current = controls.indexOf(shadow.activeElement);
        event.preventDefault();
        controls[(current + (event.shiftKey ? controls.length - 1 : 1)) % controls.length].focus();
      }
    };
    for (const [mode, label] of [["append", "追加到末尾"], ["replace", "替换草稿"], ["cancel", "取消"]]) {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = label;
      if (mode !== "append") button.className = "secondary";
      button.addEventListener("click", () => finish(mode));
      buttons.append(button);
    }
    panel.append(title, paragraph, buttons);
    shadow.append(style, panel);
    document.documentElement.append(host);
    document.addEventListener("keydown", onKey, true);
    const timeout = setTimeout(() => finish("cancel"), 30000);
    buttons.querySelector("button").focus();
  });
}

async function screenNoteDeliverText(job) {
  let editor = screenNoteFindEditor();
  const targetName = screenNoteTargetName(job.target || "deepseek");
  if (!editor) return { success: false, message: `没有找到可用输入框，请先登录 ${targetName}。` };
  let existing = screenNoteEditorText(editor);
  let mode = job.draftMode === "replace" ? "replace" : "append";
  if (existing.trim()) mode = await screenNoteChooseDraftMode();
  if (mode === "cancel") return { success: false, message: "已取消，网页原草稿保持不变。" };
  if (job.expiresAt && job.expiresAt <= Date.now()) return { success: false, message: "这次传递已过期，网页草稿保持不变。请回到桌面工具重试。" };
  editor = screenNoteFindEditor();
  if (!editor) return { success: false, message: "输入框已变化，请重试。" };
  existing = screenNoteEditorText(editor);
  const text = mode === "append" && existing.trim() ? `${existing}\n\n${job.text}` : job.text;
  const verified = await screenNoteSetEditor(editor, text);
  return verified
    ? { success: true, message: `文字已放入 ${targetName} 草稿，请检查后发送。` }
    : { success: false, message: "网页未保留完整文字，请检查输入框并使用复制粘贴。" };
}

function screenNoteAttachmentVisible(fileName) {
  // A file input's files property is not evidence of upload success.
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let node;
  while ((node = walker.nextNode())) {
    if (!node.nodeValue.includes(fileName)) continue;
    const parent = node.parentElement;
    if (parent && !parent.closest("script, style, input, [data-screennote-ui]") && screenNoteVisible(parent)) return true;
  }
  const images = [...document.querySelectorAll("img[alt], [title]")];
  return images.some(element => screenNoteVisible(element) && [element.getAttribute("alt"), element.getAttribute("title")].includes(fileName));
}

function screenNoteFindUpload(editor) {
  const inputs = [...document.querySelectorAll('input[type="file"]')].filter(input => {
    const accept = (input.accept || "").toLowerCase();
    return !input.disabled && (!accept || /image|\.(?:png|jpe?g|webp)|\*/.test(accept));
  });
  const composer = editor.closest('form, [data-testid="composer"], [data-testid="chat-input"]');
  const nearby = composer ? inputs.filter(input => composer.contains(input)) : [];
  return nearby.length === 1 ? nearby[0] : inputs.length === 1 ? inputs[0] : null;
}

function screenNoteImageFiles(job) {
  const images = Array.isArray(job.images) ? job.images : typeof job.imageBase64 === "string"
    ? [{ fileName: `screennote-${job.id.slice(0, 8)}.png`, imageBase64: job.imageBase64 }] : [];
  if (images.length === 0 || images.length > 10) throw new Error("一次需要选择 1 至 10 张截图。");
  const names = new Set();
  let total = 0;
  return images.map((image, index) => {
    const fileName = image?.fileName || `screennote-${job.id.slice(0, 8)}-${String(index + 1).padStart(2, "0")}.png`;
    if (!/^screennote-[A-Za-z0-9_-]{1,100}\.png$/.test(fileName) || names.has(fileName)) throw new Error("截图文件名无效或重复。");
    names.add(fileName);
    if (typeof image?.imageBase64 !== "string" || image.imageBase64.length > 18000000 || !/^[A-Za-z0-9+/]*={0,2}$/.test(image.imageBase64)) throw new Error("图片数据无效或过大。");
    const binary = atob(image.imageBase64);
    total += binary.length;
    if (total > 12 * 1024 * 1024) throw new Error("所选截图合计超过 12 MB，请减少图片后重试。");
    if (binary.length < 8 || ![137, 80, 78, 71, 13, 10, 26, 10].every((byte, offset) => binary.charCodeAt(offset) === byte)) throw new Error(`第 ${index + 1} 张截图不是 PNG 图片。`);
    return new File([Uint8Array.from(binary, character => character.charCodeAt(0))], fileName, { type: "image/png" });
  });
}

function screenNoteImageResult(files, target) {
  const confirmedNames = files.filter(file => screenNoteAttachmentVisible(file.name)).map(file => file.name);
  const missingNames = files.filter(file => !confirmedNames.includes(file.name)).map(file => file.name);
  const success = missingNames.length === 0;
  return {
    success, verifiedCount: confirmedNames.length, expectedCount: files.length, confirmedNames, missingNames,
    message: success
      ? `${screenNoteTargetName(target)} 网页已显示全部 ${files.length} 张截图附件，请检查上传状态并自行发送。`
      : `网页仅确认 ${confirmedNames.length}/${files.length} 张截图附件；未确认：${missingNames.join("、")}。请先检查页面，避免重复上传。`
  };
}

function screenNoteAttachFiles(input, files) {
  const transfer = new DataTransfer();
  for (const file of files) transfer.items.add(file);
  input.files = transfer.files;
  input.dispatchEvent(new Event("change", { bubbles: true }));
}

async function screenNoteWaitAttachments(files, deadline) {
  while (Date.now() < deadline) {
    if (files.every(file => screenNoteAttachmentVisible(file.name))) return true;
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  return false;
}

async function screenNoteDeliverImage(job) {
  const target = job.target || "deepseek";
  const editor = screenNoteFindEditor();
  if (!editor) return { success: false, message: `请先登录 ${screenNoteTargetName(target)} 并打开聊天页面。` };
  const files = screenNoteImageFiles(job);
  let input = screenNoteFindUpload(editor);
  if (!input) {
    // DeepSeek can mount its file input only after opening the attachment menu.
    // The composer already supports pasting PNGs; preserve any text draft.
    if (typeof ClipboardEvent === "function") {
      const transfer = new DataTransfer();
      for (const file of files) transfer.items.add(file);
      editor.focus();
      editor.dispatchEvent(new ClipboardEvent("paste", { bubbles: true, cancelable: true, clipboardData: transfer }));
      await screenNoteWaitAttachments(files, Math.min(Date.now() + 10000, job.expiresAt || Infinity));
      // Never retry a paste with a file input: a slow upload could be duplicated.
      return screenNoteImageResult(files, target);
    }
    return { success: false, verifiedCount: 0, expectedCount: files.length, message: "网页尚未显示图片上传入口。请先登录 DeepSeek 并打开新对话，再重试；截图仍保留在历史中。" };
  }
  const deadline = Math.min(Date.now() + 35000, job.expiresAt || Infinity);
  if (input.multiple || files.length === 1) {
    screenNoteAttachFiles(input, files);
    await screenNoteWaitAttachments(files, Math.min(deadline, Date.now() + 10000));
  } else {
    // Single-file composers must visibly retain each preceding attachment.
    // Stop on the first missing attachment; retrying could duplicate uploads.
    for (let index = 0; index < files.length && Date.now() < deadline; index++) {
      input = screenNoteFindUpload(screenNoteFindEditor() || editor);
      if (!input) break;
      screenNoteAttachFiles(input, [files[index]]);
      if (!await screenNoteWaitAttachments(files.slice(0, index + 1), Math.min(deadline, Date.now() + 6000))) break;
    }
  }
  return screenNoteImageResult(files, target);
}

async function screenNoteDeliverJob(job) {
  if (job.kind === "image" || job.kind === "images") {
    const hasPrompt = typeof job.text === "string" && job.text.length > 0;
    if (hasPrompt) {
      const draft = await screenNoteDeliverText(job);
      if (!draft.success) return { ...draft, promptVerified: false, verifiedCount: 0, expectedCount: job.images?.length || 1 };
    }
    const images = await screenNoteDeliverImage(job);
    return hasPrompt ? { ...images, promptVerified: true, message: `${images.message} 翻译／总结提示词已放入草稿，请检查后发送。` } : images;
  }
  return (job.kind === "text" || job.kind === "target-text") && typeof job.text === "string"
    ? await screenNoteDeliverText(job) : { success: false, message: "不支持的内容类型。" };
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id) return;
  if (message.type === "SCREENNOTE_READY") {
    sendResponse({ ready: screenNoteTargetId() === (message.target || "deepseek") && !!screenNoteFindEditor() });
    return;
  }
  if (message.type !== "SCREENNOTE_DELIVER") return;
  if (screenNoteDelivering) {
    sendResponse({ success: false, message: "网页正在处理上一条内容，请稍后重试。" });
    return;
  }
  screenNoteDelivering = true;
  (async () => {
    try {
      const job = message.job;
      if (!job || typeof job.id !== "string" || !/^[A-Za-z0-9_-]{1,80}$/.test(job.id)) throw new Error("传入内容格式无效。");
      if (screenNoteTargetId() !== (job.target || "deepseek")) throw new Error("当前网页与选定的 AI 不一致，内容未放入。");
      if (!Number.isFinite(job.expiresAt) || job.expiresAt <= Date.now()) throw new Error("这次传递已过期，请回到桌面工具重试。");
      const result = await screenNoteDeliverJob(job);
      screenNoteShowToast(result.message);
      sendResponse(result);
    } catch (error) { sendResponse({ success: false, message: error.message || "放入内容失败，请手动复制。" }); }
    finally { screenNoteDelivering = false; }
  })();
  return true;
});

async function screenNotePoll() {
  if (document.hidden) return;
  if (screenNoteDelivering) {
    try { await chrome.runtime.sendMessage({ type: "BRIDGE_HEARTBEAT" }); }
    catch { /* The desktop will time out if the extension was reloaded. */ }
    return;
  }
  if (screenNotePolling) return;
  screenNotePolling = true;
  try { await chrome.runtime.sendMessage({ type: "BRIDGE_POLL" }); }
  catch { /* Extension reload or closed desktop app: the popup shows the connection status. */ }
  finally { screenNotePolling = false; }
}

setInterval(() => { void screenNotePoll(); }, 1000);
document.addEventListener("visibilitychange", () => { void screenNotePoll(); });
void screenNotePoll();

})();
