/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; connect the shared reader directly to the WinUI host
 * Human guidance: preserve local rendering and load Mermaid when a diagram is visible
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */

import { MarkdownHost } from "../../../client/markdown.js";
import { registerNativeWorkspace, releaseWorkspace } from "../../../client/workspaces.js";

const channel = window.chrome.webview;
const pane = document.getElementById("reader");
let mount;
let workspaceId;
let active;

/** Send only navigation and reader feedback for the visible native render generation. */
function forward(event) {
  if (!active) {
    return;
  }
  if (event.type === "navigate") {
    channel.postMessage({ kind: "navigate", renderId: active.id, path: event.path, anchor: event.anchor });
  } else if (event.type === "status") {
    channel.postMessage({ kind: "status", renderId: active.id, message: event.message });
  }
}

/** Mount the shared renderer and wait for its stylesheet before positioning a heading. */
function mountReader() {
  const host = new MarkdownHost(pane, "winui-reader", forward);
  const stylesheet = host.root.querySelector('link[rel="stylesheet"]');
  const styles = new Promise((resolve, reject) => {
    if (stylesheet.sheet) {
      resolve();
      return;
    }
    stylesheet.addEventListener("load", resolve, { once: true });
    stylesheet.addEventListener("error", () => reject(new Error("The reader stylesheet could not be loaded.")), { once: true });
  });
  return { host, styles };
}

/** Cancel the active generation and release its DOM, images and native workspace adapter. */
function clear() {
  active?.controller.abort();
  active = undefined;
  mount?.host.dispose();
  mount = undefined;
  if (workspaceId) {
    releaseWorkspace(workspaceId);
    workspaceId = undefined;
  }
}

/** Install the native snapshot and acknowledge only the generation that still owns the pane. */
async function render(message) {
  active?.controller.abort();
  const request = { id: message.renderId, controller: new AbortController() };
  active = request;

  try {
    const dark = message.dark ?? matchMedia("(prefers-color-scheme: dark)").matches;
    const theme = dark ? "dark" : "light";
    document.documentElement.dataset.theme = theme;

    if (workspaceId !== message.workspace.id) {
      if (workspaceId) {
        releaseWorkspace(workspaceId);
      }
      registerNativeWorkspace(message.workspace);
      workspaceId = message.workspace.id;
    }

    const reader = mount ??= mountReader();
    await reader.styles;
    request.controller.signal.throwIfAborted();
    reader.host.container.dataset.theme = theme;
    await reader.host.render({ document: message.document, workspaceId, anchor: message.anchor }, request.controller.signal);
    if (active === request) {
      channel.postMessage({ kind: "rendered", renderId: request.id });
    }
  } catch (error) {
    if (active === request) {
      clear();
      channel.postMessage({ kind: "error", renderId: request.id, message: error.message || "The document could not be rendered." });
    }
  }
}

/** Accept the native host's render and clear messages on the trusted WebView2 channel. */
function receive(event) {
  const message = event.data;
  if (message?.kind === "render") {
    void render(message);
  } else if (message?.kind === "clear") {
    clear();
  }
}

channel.addEventListener("message", receive);
window.addEventListener("pagehide", clear, { once: true });
channel.postMessage({ kind: "ready" });
