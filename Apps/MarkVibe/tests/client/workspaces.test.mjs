/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native renderer resource and cancellation regression checks
 * Human guidance: retire the browser product while preserving bounded native resources
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
import test from "node:test";
import assert from "node:assert/strict";

let receive;
let respond;
const messages = [];
globalThis.window = {
  markvibeDesktop: { version: 1, capability: "synthetic-capability" },
  chrome: { webview: {
    addEventListener(name, listener) { receive = listener; },
    postMessage(message) {
      messages.push(message);
      if (respond && message.operation !== "cancel") {
        queueMicrotask(() => receive({ data: { version: 1, id: message.id, ok: true, value: respond(message) } }));
      }
    }
  } }
};
const { registerNativeWorkspace, getWorkspace, acquireAsset, releaseWorkspace } = await import("../../client/workspaces.js");
const { nativeCall } = await import("../../client/native.js");

test("native image reads preserve identity, byte bounds and canonical path validation", async () => {
  registerNativeWorkspace({ id: "fixture" });
  respond = () => Buffer.from("bounded bytes").toString("base64");
  const provider = getWorkspace("fixture");
  const blob = await provider.blob("images/cafe\u0301.png", 100);
  assert.equal(blob.type, "image/png");
  assert.equal(await blob.text(), "bounded bytes");
  const message = messages.at(-1);
  assert.equal(message.operation, "workspace.read");
  assert.equal(message.capability, "synthetic-capability");
  assert.deepEqual(message.args, { workspaceId: "fixture", path: "images/café.png" });
  await assert.rejects(provider.blob("image.png", 2), error => error.code === "file-too-large");
  const count = messages.length;
  await assert.rejects(provider.blob("../outside.png", 100));
  assert.equal(messages.length, count);
  releaseWorkspace("fixture");
  assert.throws(() => getWorkspace("fixture"), error => error.code === "workspace-unavailable");
});

test("replacing a native provider revokes its retained image leases", async context => {
  const png = Buffer.alloc(24);
  png.writeUInt32BE(0x89504e47, 0);
  png.writeUInt32BE(0x0d0a1a0a, 4);
  png.writeUInt32BE(2, 16);
  png.writeUInt32BE(3, 20);
  respond = () => png.toString("base64");
  const revoked = [];
  context.mock.method(URL, "revokeObjectURL", url => revoked.push(url));
  registerNativeWorkspace({ id: "images" });
  const lease = await acquireAsset("images", "image.png", "reader");
  assert.match(lease.url, /^blob:/u);
  registerNativeWorkspace({ id: "images" });
  assert.deepEqual(revoked, [lease.url]);
  releaseWorkspace("images");
});

test("native requests reject cancellation and forward it to the captured request", async () => {
  respond = null;
  const controller = new AbortController();
  const pending = nativeCall("workspace.read", { workspaceId: "fixture", path: "image.png" }, controller.signal);
  const request = messages.at(-1);
  controller.abort();
  await assert.rejects(pending, error => error.code === "cancelled");
  assert.equal(messages.at(-1).operation, "cancel");
  assert.equal(messages.at(-1).args.requestId, request.id);
  const count = messages.length;
  await assert.rejects(async () => nativeCall("workspace.read", {}, controller.signal), error => error.code === "cancelled");
  assert.equal(messages.length, count);
});
