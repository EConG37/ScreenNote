"use strict";

const statusElement = document.getElementById("status");
const connectButton = document.getElementById("connect");

async function request(message) {
  const result = await chrome.runtime.sendMessage(message);
  if (!result) throw new Error("连接扩展失败，请重新加载扩展。");
  return result;
}

async function refreshStatus() {
  try {
    const result = await request({ type: "BRIDGE_STATUS" });
    statusElement.textContent = result.message;
  } catch (error) { statusElement.textContent = error.message; }
}

document.getElementById("pair-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  connectButton.disabled = true;
  try {
    const code = document.getElementById("pair-code").value.trim();
    if (!/^\d{6}$/.test(code)) throw new Error("请输入 6 位数字配对码。");
    const result = await request({ type: "BRIDGE_PAIR", code });
    statusElement.textContent = result.message;
    if (result.success) document.getElementById("pair-code").value = "";
  } catch (error) { statusElement.textContent = error.message; }
  finally { connectButton.disabled = false; }
});

document.getElementById("open").addEventListener("click", async () => {
  try {
    const result = await request({ type: "OPEN_TARGET", target: document.getElementById("target").value });
    statusElement.textContent = result.message;
  } catch (error) { statusElement.textContent = error.message; }
});

document.getElementById("disconnect").addEventListener("click", async () => {
  try {
    const result = await request({ type: "BRIDGE_DISCONNECT" });
    statusElement.textContent = result.message;
  } catch (error) { statusElement.textContent = error.message; }
});

void refreshStatus();
