/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; renderer-only channel for the native Windows app
 * Human guidance: leave application state with the native shell and reuse proven renderers
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */

import { findDocument } from "./document-find.js";
import { observeDocumentSelection } from "./document-selection.js";
import { fileContextTarget, fileSelection } from "./file-context.js";
import { setLanguage } from "./i18n.js";
import { MarkdownHost } from "./markdown.js";
import { checkCancelled, nextFrame, WorkspaceError } from "./path.js";
import { registerNativeWorkspace, releaseWorkspace } from "./workspaces.js";

const channel = window.chrome.webview;
const surface = document.getElementById("surface");
const readerPane = document.getElementById("reader");
const sourcePane = document.getElementById("source");
const requests = new Map();
const workspaceUsers = new Map();
let readingStyles;
let reader;
let source;
let sourceAdapter;
let exportAdapter;
let workspace;
let activeRender;
let rendered;
let previewMedia;
let focusedView = "read";
let synchronizing = false;
let positionTimer;

/** Send a renderer gesture to the native UI with the owning document's identity. */
function emit(event) {
  if (!rendered) {
    return;
  }
  const { model, workspace: info } = rendered;
  const selectedText = typeof event.selectedText === "string" ? event.selectedText.slice(0, 1048576) : undefined;
  channel.postMessage({ kind: "event", event: {
    workspaceId: info.id, documentPath: model.path, fingerprint: model.fingerprint, ...event, selectedText
  } });
}

/** Retain the shared provider while a visible document or in-flight export uses its images. */
function retain(info) {
  const users = workspaceUsers.get(info.id) ?? 0;
  if (!users) {
    registerNativeWorkspace(info);
  }
  workspaceUsers.set(info.id, users + 1);
}

/** Release a provider and its images after both the viewport and any exports have finished. */
function release(info) {
  const remaining = (workspaceUsers.get(info.id) ?? 1) - 1;
  if (remaining) {
    workspaceUsers.set(info.id, remaining);
  } else {
    workspaceUsers.delete(info.id);
    sourceAdapter?.releaseSourceWorkspace(info.id);
    releaseWorkspace(info.id);
  }
}

/** Hold visual context across theme and layout updates for an already rendered document. */
async function applyAppearance({ preferences, layout, dark }, restorePosition = true) {
  const html = document.documentElement;
  const theme = dark ? "dark" : "light";
  const readerPosition = restorePosition && !readerPane.hidden && reader?.model ? reader.capture() : null;
  const active = rendered;
  html.dataset.theme = theme;
  html.dataset.documentStyle = preferences.documentStyle;
  html.dataset.acrylic = preferences.acrylic ? "true" : "false";
  html.style.setProperty("--reading-scale", String(preferences.zoom / 100));
  if (preferences.uiFont === "system") {
    html.style.removeProperty("--ui-font");
    html.style.removeProperty("--ui-monospace");
  } else {
    html.style.setProperty("--ui-font", `"${preferences.uiFont}", "Segoe UI", system-ui, sans-serif`);
    html.style.setProperty("--ui-monospace", `"${preferences.uiFont}"`);
  }
  surface.style.setProperty("--source-width", `${layout.sourceRatio * 100}%`);
  setLanguage(preferences.language);
  for (const host of mountedHosts()) {
    host.container.dataset.theme = theme;
    host.container.dataset.documentStyle = preferences.documentStyle;
    host.container.dataset.acrylic = html.dataset.acrylic;
  }
  if (reader) {
    reader.preferences = preferences;
    reader.setExpanded(layout.readerExpanded);
    if (restorePosition && !readerPane.hidden) {
      reader.retheme(() => active === rendered);
    }
  }
  await source?.theme();
  if (readerPosition && active === rendered) {
    reader.restore(readerPosition);
  }
}

/** Mount the reader once and wait for its stylesheet before restoring source-backed scroll. */
async function getReader() {
  if (!reader) {
    reader = new MarkdownHost(readerPane, "desktop-reader", emit);
    reader.selectionObserver = observeDocumentSelection(reader, true);
    reader.scrollListener = () => scrolled("read");
    const stylesheet = reader.root.querySelector('link[rel="stylesheet"]');
    readingStyles = stylesheet.sheet ? Promise.resolve() : new Promise((resolve, reject) => {
      stylesheet.addEventListener("load", resolve, { once: true });
      stylesheet.addEventListener("error", () => reject(new WorkspaceError("reader-css", "The Markdown stylesheet could not be loaded.")), { once: true });
    });
  }
  await readingStyles;
  return reader;
}

/** Defer loading the source renderer and Monaco until Source or Split first becomes visible. */
async function getSource(signal) {
  if (!source) {
    sourceAdapter ??= await import("./source.js");
    checkCancelled(signal);
    if (!source) {
      source = new sourceAdapter.SourceHost(sourcePane, "desktop-source", emit);
      source.selectionObserver = observeDocumentSelection(source);
      source.scrollListener = () => scrolled("source");
    }
  }
  return source;
}

/** Return the current renderer hosts in a stable order. */
function mountedHosts() {
  return [reader, source].filter(Boolean);
}

/** Use the requested visible pane or the last focused visible pane. */
function visibleHost(view = focusedView) {
  if (view === "source" && !sourcePane.hidden && source) {
    return source;
  }
  return (!readerPane.hidden && reader) || (!sourcePane.hidden && source) || null;
}

/** Suppress scroll feedback while transferring one source-backed position to the other pane. */
function scrolled(view) {
  const from = view === "source" ? source : reader;
  const to = view === "source" ? reader : source;
  if (!rendered || !from || synchronizing || activeRender) {
    return;
  }

  const snapshot = rendered;
  if (snapshot.view === "split" && to && reader.model?.path === source.path && reader.workspaceId === source.workspaceId) {
    synchronizing = true;
    to.scrollToLine(from.capture().sourceLine);
    requestAnimationFrame(() => requestAnimationFrame(() => { synchronizing = false; }));
  }

  clearTimeout(positionTimer);
  positionTimer = setTimeout(() => {
    if (snapshot === rendered) {
      emit({ type: "position", path: snapshot.model.path, position: from.capture() });
    }
  }, 220);
}

/** Resolve a semantic outline element's exact line from its recorded Markdown offset. */
function outlineSourceLine(model, anchor) {
  if (!anchor) {
    return undefined;
  }
  const offset = model.elements.find(element => element.id === anchor)?.sourceStart;
  if (!Number.isInteger(offset) || offset < 0 || offset > model.source.length) {
    return undefined;
  }
  let line = 1;
  for (let index = 0; index < offset; index++) {
    if (model.source[index] === "\n") {
      line++;
    }
  }
  return line;
}

/** Update only presentation and apply the supplied effective C# document snapshot. */
async function render(args, signal) {
  checkCancelled(signal);
  const nextWorkspace = args.workspace;
  if (workspace?.id !== nextWorkspace.id) {
    resetView();
    retain(nextWorkspace);
    workspace = nextWorkspace;
  }

  const view = args.view;
  if (!["read", "source", "split"].includes(view)) {
    throw new WorkspaceError("invalid-view", "Choose an available document view.");
  }

  const { document: model, preferences, layout, anchor, position, deferMedia } = args;
  const presentation = { view, model, workspace, deferMedia };
  rendered = presentation;
  focusedView = view === "source" ? "source" : "read";
  readerPane.hidden = view === "source";
  sourcePane.hidden = view === "read";
  surface.dataset.view = view;
  const panes = await Promise.all([
    readerPane.hidden ? null : getReader(),
    sourcePane.hidden ? null : getSource(signal)
  ]);
  checkCancelled(signal);
  await applyAppearance(args, false);
  checkCancelled(signal);
  const [nextReader, nextSource] = panes;
  const sourceLine = position?.sourceLine || (nextSource &&
    (model.headings.find(heading => heading.id === anchor)?.line ?? outlineSourceLine(model, anchor)));
  if (nextReader && deferMedia && !previewMedia) {
    const { PreviewMedia } = await import("./map-preview-media.js");
    checkCancelled(signal);
    previewMedia = new PreviewMedia("desktop-map-reader");
  }
  previewMedia?.clear();
  // The graph preview keeps media idle until the user chooses each image or diagram.
  const prepareMedia = deferMedia ? fragment => previewMedia.prepare(fragment, workspace.id) : undefined;

  await Promise.all([
    nextReader?.render({ workspaceId: workspace.id, document: model, anchor, position, prepareMedia }, signal),
    nextSource?.render({ workspaceId: workspace.id, path: model.path, source: model.source, fingerprint: model.fingerprint, preferences, desktopMonaco: true }, signal)
  ]);
  checkCancelled(signal);
  if (nextSource && Number.isFinite(sourceLine)) {
    nextSource.scrollToLine(sourceLine);
  } else if (nextSource && nextReader) {
    nextSource.scrollToLine(nextReader.capture().sourceLine);
  }
  if (nextReader && layout.readerExpanded) {
    nextReader.setExpanded(true);
  }
  await nextFrame();
  checkCancelled(signal);
  return true;
}

/** Cancel renderer-owned timers, subscriptions and resource leases when the native model closes. */
function resetView() {
  rendered = undefined;
  clearTimeout(positionTimer);
  previewMedia?.clear();
  previewMedia = undefined;
  for (const host of mountedHosts()) {
    host.selectionObserver?.abort();
    host.dispose();
  }
  reader = undefined;
  readingStyles = undefined;
  source = undefined;
  readerPane.hidden = true;
  sourcePane.hidden = true;
  if (workspace) {
    release(workspace);
    workspace = undefined;
  }
}

/** Return the visible selection before a native toolbar or menu takes focus. */
function selectionSnapshot(view) {
  const host = visibleHost(view);
  if (!host) {
    return null;
  }
  const target = fileContextTarget(host, host.root.activeElement, null, false);
  return { ...target, selectedText: fileSelection(host).slice(0, 1048576) };
}

/** Collapse any current Monaco, fallback, or shadow-root selection. */
function clearSelection() {
  for (const host of mountedHosts()) {
    const position = host.editor?.getPosition();
    if (position) {
      host.editor.setSelection({ startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: position.column, endColumn: position.column });
    }
    if (host.textarea) {
      host.textarea.setSelectionRange(host.textarea.selectionEnd, host.textarea.selectionEnd);
    }
    host.root.getSelection?.()?.removeAllRanges();
  }
  document.getSelection()?.removeAllRanges();
  return true;
}

/** Prepare an export with the shared renderer while retaining its dynamically accessed image provider. */
async function exportPage(operation, args, signal) {
  const info = args.workspace ?? workspace;
  if (!info) {
    throw new WorkspaceError("workspace-unavailable", "Open the document's folder before exporting.");
  }
  retain(info);
  try {
    exportAdapter ??= await import("./export.js");
    const renderArgs = { ...args, workspaceId: info.id };
    return await (operation === "document.export"
      ? exportAdapter.exportDocument(renderArgs, signal)
      : exportAdapter.openBrowserPreview(renderArgs, signal));
  } finally {
    release(info);
  }
}

/** Route application snapshots and presentation commands through renderer-owned state only. */
async function execute(operation, args, controller) {
  const { signal } = controller;
  switch (operation) {
    case "view.render":
      activeRender?.abort();
      activeRender = controller;
      try {
        return await render(args, signal);
      } finally {
        if (activeRender === controller) {
          activeRender = undefined;
        }
      }
    case "view.clear":
      activeRender?.abort();
      resetView();
      return true;
    case "appearance.apply":
      await applyAppearance(args);
      return true;
    case "layout.split":
      if (Number.isFinite(args.ratio)) {
        surface.style.setProperty("--source-width", `${Math.min(0.8, Math.max(0.2, args.ratio)) * 100}%`);
      }
      return true;
    case "position.capture":
      return visibleHost(args.view)?.capture() ?? { blockId: "", fraction: 0, sourceLine: 1 };
    case "position.restore":
      visibleHost(args.view)?.restore(args.position);
      return true;
    case "reader.anchor":
      reader?.anchor(args.anchor);
      return true;
    case "document.find":
      return findDocument(mountedHosts(), args);
    case "document.selection":
      return selectionSnapshot(args.view);
    case "document.selection.clear":
      return clearSelection();
    case "document.export":
    case "document.browser":
      return exportPage(operation, args, signal);
    default:
      throw new WorkspaceError("unknown-renderer-operation", "The document renderer does not support this operation.");
  }
}

/** Cancel the requested operation or reply with its structured result. */
async function receive({ data }) {
  if (data?.kind === "cancel") {
    requests.get(data.id)?.abort();
    return;
  }
  if (data?.kind !== "call" || typeof data.id !== "string" || requests.has(data.id)) {
    return;
  }

  const controller = new AbortController();
  requests.set(data.id, controller);
  try {
    const value = await execute(data.operation, data.arguments ?? {}, controller);
    checkCancelled(controller.signal);
    channel.postMessage({ kind: "response", id: data.id, ok: true, value });
  } catch (error) {
    channel.postMessage({ kind: "response", id: data.id, ok: false,
      code: controller.signal.aborted ? "cancelled" : (error.code ?? "renderer-failed"),
      message: controller.signal.aborted ? "Action cancelled." : error.message });
  } finally {
    requests.delete(data.id);
  }
}

/** Dispose an embedded document page without leaving active export or render work behind. */
function close() {
  for (const request of requests.values()) {
    request.abort();
  }
  resetView();
  sourceAdapter?.disposeSourceModels();
}

readerPane.addEventListener("focusin", () => { focusedView = "read"; });
sourcePane.addEventListener("focusin", () => { focusedView = "source"; });
window.addEventListener("keydown", event => {
  if (!rendered?.deferMedia || event.defaultPrevented) {
    return;
  }
  if (event.key === "Escape") {
    event.preventDefault();
    emit({ type: "preview-close" });
  } else if (event.altKey && event.key === "ArrowLeft") {
    event.preventDefault();
    emit({ type: "preview-back" });
  }
});
window.addEventListener("pagehide", close, { once: true });
channel.addEventListener("message", receive);
channel.postMessage({ kind: "ready" });
