/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; in-place map reading and section navigation; 2026-11-20
 * Human guidance: Umberto Giacobbi; read one card and return to the same point on the map
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
import { prepareMarkdown, textElement, uiText } from "./markdown.js";
import { localize } from "./i18n.js";
import { makeId } from "./path.js";
import { PreviewMedia } from "./map-preview-media.js";
import { folderOf } from "./map-atlas.js";

const blocksPerFrame = 24;

/** Displays one document over its card. History retains positions, never previous documents or DOM trees. */
export class MapPreview {
  /** Mount controls outside the transformed world so reading and scrolling are independent of map zoom and pan. */
  constructor(host) {
    this.host = host;
    this.media = new PreviewMedia(host.id);
    this.history = [];
    // An actual map gesture supersedes a deferred restoration waiting for an off-projection card.
    const interruptReturn = () => {
      this.returnTicket = null;
    };
    for (const type of ["pointerdown", "wheel", "keydown"]) {
      host.viewport.addEventListener(type, interruptReturn, { capture: true, passive: true });
    }
    this.trigger = uiText("button", "Read here", "map-read-here");
    this.trigger.type = "button";
    this.trigger.setAttribute("aria-expanded", "false");
    this.trigger.addEventListener("click", () => this.open(host.selected));

    this.panel = document.createElement("section");
    this.panel.className = "map-reading-panel";
    this.panel.hidden = true;
    this.panel.id = "map-reading-" + host.id;
    this.trigger.setAttribute("aria-controls", this.panel.id);
    localize(this.panel, "Read here", "aria-label");
    const toolbar = document.createElement("div");
    toolbar.className = "map-reading-toolbar";
    this.title = textElement("strong", "");
    this.backButton = uiText("button", "Back");
    this.readerButton = uiText("button", "Open document");
    this.closeButton = uiText("button", "Close");
    this.backButton.addEventListener("click", () => this.back());
    this.readerButton.addEventListener("click", () => this.openReader());
    this.closeButton.addEventListener("click", () => this.close(true));
    toolbar.append(this.title, this.backButton, this.readerButton, this.closeButton);

    this.status = document.createElement("div");
    this.status.className = "map-reading-status";
    this.status.setAttribute("role", "status");
    this.article = document.createElement("article");
    this.article.className = "map-reading-page markdown";
    this.article.tabIndex = 0;
    this.article.addEventListener("click", event => this.follow(event));
    this.article.addEventListener("auxclick", event => {
      if (event.target.closest("a[data-mv-path]")) {
        event.preventDefault();
      }
    });
    this.panel.addEventListener("wheel", event => event.stopPropagation(), { passive: true });
    this.panel.addEventListener("pointerdown", event => event.stopPropagation());
    this.panel.addEventListener("keydown", event => {
      // Compact mode mounts the panel in the map's scrollable list; arrow keys belong to the document.
      event.stopPropagation();
      if (event.key === "Escape" || event.altKey && event.key === "ArrowLeft") {
        event.preventDefault();
        if (event.key === "Escape") {
          this.close(true);
        } else {
          this.back();
        }
      }
    });
    this.panel.append(toolbar, this.status, this.article);
    host.body.append(this.panel);
  }

  /** Identifies the one folder whose saved collapse state may be temporarily overridden while reading. */
  get currentPath() {
    return this.current?.path;
  }

  /** Reflect managed selection and attach the single activation button even after a projection rebuild. */
  selectionChanged(previous) {
    const { selected, cards } = this.host;
    const element = cards.get(selected)?.container;
    if (element && this.trigger.parentNode !== element) {
      element.append(this.trigger);
    } else if (!element) {
      this.trigger.remove();
    }
    if (this.current && !this.pending && previous !== selected && this.current.path !== selected) {
      this.close(false);
    }
    this.trigger.setAttribute("aria-expanded", String(this.current?.path === selected));
  }

  /** Capture scale, logical camera translation and compact-list scroll before following a link. */
  camera() {
    const host = this.host;
    return { x: host.x, y: host.y, scale: host.scale, scroll: host.viewport.scrollTop };
  }

  /** Resume the exact saved camera after updating selection, cancelling any competing animated movement. */
  restoreCamera(camera) {
    const host = this.host;
    host.cancelCamera();
    host.x = camera.x;
    host.y = camera.y;
    host.scale = camera.scale;
    host.transform();
    host.viewport.scrollTop = camera.scroll;
  }

  /** Retain only scroll and disclosure positions needed to return to this page. */
  remember() {
    const disclosures = [...this.article.querySelectorAll("details")];
    return {
      ...this.current, camera: this.camera(), scroll: this.article.scrollTop, position: this.readingPosition(),
      renderedBlocks: this.article.children.length,
      disclosures: disclosures.flatMap((detail, index) => detail.open ? [index] : [])
    };
  }

  /** Follow a card or section link, preserving the first map position separately from bounded reading history. */
  open(path, anchor = "", followLink = false) {
    const host = this.host;
    if (!path || host.suspended || host.arranging || !host.state) {
      return;
    }
    this.returnTicket = null;
    if (this.loaded === path && !followLink && !anchor) {
      this.article.focus({ preventScroll: true });
      return;
    }
    if (this.pending?.entry.path === path && this.pending.entry.anchor === anchor) {
      return;
    }
    host.cancelCamera();
    host.lastCardClick = null;
    if (!this.current) {
      this.origin = { path: host.selected, camera: this.camera() };
    } else {
      this.history.push(this.remember());
      if (this.history.length > 32) {
        this.history.shift();
      }
    }
    this.display({ path, anchor });
  }

  /** Ask C# for the current document revision. Navigation within the loaded page reuses its sanitized markup. */
  display(entry) {
    this.cancelRequest();
    const previousPath = this.currentPath;
    this.current = { path: entry.path, anchor: entry.anchor };
    this.updateFolderVisibility(previousPath, entry.path);
    this.panel.hidden = false;
    this.trigger.setAttribute("aria-expanded", String(this.host.selected === entry.path));
    const node = this.host.map?.nodes.find(candidate => candidate.path === entry.path);
    this.title.textContent = node?.title || entry.path;
    this.title.title = entry.path;
    this.article.setAttribute("aria-label", this.title.textContent);
    if (entry.camera) {
      this.restoreCamera(entry.camera);
    }
    if (this.loaded === entry.path) {
      this.finish(entry);
      return;
    }
    this.releasePage();
    this.article.scrollTop = 0;
    this.article.setAttribute("aria-busy", "true");
    this.status.replaceChildren(uiText("span", "Loading document…"));
    this.readerButton.disabled = true;
    this.place();
    this.article.focus({ preventScroll: true });
    const request = { type: "map-preview", workspaceId: this.host.workspaceId,
      stateId: this.host.state.stateId, path: entry.path, anchor: entry.anchor, requestId: makeId() };
    this.pending = { request, entry };
    void this.host.emit(request);
  }

  /** Reject superseded responses before parsing markup; only the currently open page can reach the DOM. */
  receive(args) {
    const pending = this.pending;
    if (!pending || args.requestId !== pending.request.requestId || args.path !== pending.entry.path
        || args.workspaceId !== this.host.workspaceId || args.stateId !== this.host.state?.stateId) {
      return;
    }
    this.pending = null;
    this.article.setAttribute("aria-busy", "false");
    if (args.error || !args.document) {
      this.status.replaceChildren(uiText("span", args.error || "This page could not be opened"));
      return;
    }
    let fragment;
    try {
      fragment = prepareMarkdown(args.document);
      this.media.prepare(fragment, this.host.workspaceId);
    } catch {
      this.releasePage();
      this.status.replaceChildren(uiText("span", "This page could not be opened"));
      return;
    }
    this.remaining = fragment;
    this.appendBlocks(blocksPerFrame);
    this.loaded = args.path;
    this.title.textContent = args.document.title || this.host.map?.nodes.find(node => node.path === args.path)?.title || args.path;
    this.article.setAttribute("aria-label", this.title.textContent);
    this.trigger.setAttribute("aria-expanded", String(this.host.selected === args.path));
    this.finish(pending.entry);
    this.scheduleBlocks();
  }

  /** Append complete top-level Markdown blocks in source order, in one DOM mutation per batch. */
  appendBlocks(limit) {
    const batch = document.createDocumentFragment();
    let count = 0;
    while (count < limit && this.remaining?.firstChild) {
      const node = this.remaining.firstChild;
      if (node.nodeType === Node.ELEMENT_NODE) {
        count++;
      }
      batch.append(node);
    }
    this.article.append(batch);
    if (!this.remaining?.firstChild) {
      this.remaining = null;
    }
  }

  /** Paint long documents across frames while keeping already visible paragraphs, links and selection stable. */
  scheduleBlocks() {
    if (this.remaining && !this.paintFrame) {
      this.paintFrame = requestAnimationFrame(() => {
        this.paintFrame = null;
        this.appendBlocks(blocksPerFrame);
        this.scheduleBlocks();
      });
    }
  }

  /** Immediately mount an unpainted destination and a batch below it so the requested section can scroll to the top. */
  mountThrough(target) {
    while (this.remaining?.contains(target)) {
      this.appendBlocks(blocksPerFrame);
    }
    if (this.remaining) {
      this.appendBlocks(blocksPerFrame);
    }
  }

  /** Release deferred rendering and any explicitly acquired media before loading another document or closing. */
  releasePage() {
    this.loaded = null;
    if (this.paintFrame) {
      cancelAnimationFrame(this.paintFrame);
      this.paintFrame = null;
    }
    this.remaining = null;
    this.media.clear();
    this.article.replaceChildren();
  }

  /** Restore a previous reading point or scroll the new target section into view without scrolling the map. */
  finish(entry) {
    this.status.replaceChildren();
    this.readerButton.disabled = false;
    this.place();
    if (entry.camera) {
      this.restoreCamera(entry.camera);
    } else {
      this.ensureCardVisible();
    }
    if (entry.scroll !== undefined) {
      if (this.remaining && this.article.children.length < entry.renderedBlocks) {
        this.appendBlocks(entry.renderedBlocks - this.article.children.length);
      }
      const opened = new Set(entry.disclosures);
      [...this.article.querySelectorAll("details")].forEach((detail, index) => {
        detail.open = opened.has(index);
      });
      this.article.scrollTop = entry.scroll;
      // Explicitly loaded media or a window resize can change earlier block heights between visits.
      if (entry.position) {
        const block = [...this.article.querySelectorAll("section[data-block-id]")]
          .find(element => element.dataset.blockId === entry.position.blockId);
        if (block) {
          const bounds = block.getBoundingClientRect();
          const offset = Math.min(entry.position.offset, bounds.height);
          this.article.scrollTop += bounds.top - this.article.getBoundingClientRect().top + offset - 16;
        }
      }
      this.article.focus({ preventScroll: true });
    } else {
      this.reveal(entry.anchor);
    }
  }

  /** Find a sanitized heading/outline ID and expand all parent disclosures before measuring its position. */
  reveal(anchor) {
    let target;
    if (anchor) {
      const selector = "#" + CSS.escape(anchor) + ", [data-element-id=" + CSS.escape(anchor) + "]";
      target = this.article.querySelector(selector) || this.remaining?.querySelector(selector);
    }
    if (target) {
      this.mountThrough(target);
      for (let detail = target.closest("details"); detail; detail = detail.parentElement?.closest("details")) {
        detail.open = true;
      }
      this.article.scrollTop += target.getBoundingClientRect().top - this.article.getBoundingClientRect().top - 16;
      if (!target.matches("a[href], button, input, summary, [tabindex]")) {
        target.tabIndex = -1;
      }
      target.focus({ preventScroll: true });
    } else {
      if (anchor) {
        this.status.replaceChildren(uiText("span", "This section is unavailable in the current document."));
      }
      this.article.scrollTop = 0;
      this.article.focus({ preventScroll: true });
    }
  }

  /** Intercept only document navigation; external links keep the shared reader's sanitized link policy. */
  follow(event) {
    const link = event.target.closest("a");
    if (!link || link.dataset.mvExternal) {
      return;
    }
    event.preventDefault();
    if (link.dataset.mvPath) {
      this.open(link.dataset.mvPath, link.dataset.mvAnchor || "", true);
    } else {
      this.status.replaceChildren(uiText("span", "This link is unavailable or points outside the opened workspace."));
    }
  }

  /** Restore the previous page and camera; the first Back returns directly to the original map selection. */
  back() {
    const previous = this.history.pop();
    if (previous) {
      this.display(previous);
    } else {
      this.close(true);
    }
  }

  /** Capture the source-backed position to survive changes in viewport width or media height on a return visit. */
  readingPosition() {
    const top = this.article.getBoundingClientRect().top + 16;
    let position;
    for (const block of this.article.querySelectorAll("section[data-block-id]")) {
      if (block.closest("details:not([open])")) {
        continue;
      }
      const bounds = block.getBoundingClientRect();
      if (bounds.top > top) {
        position ??= { blockId: block.dataset.blockId, fraction: 0, offset: 0, sourceLine: Number(block.dataset.sourceStart) };
        break;
      }
      const fraction = Math.min(1, Math.max(0, (top - bounds.top) / Math.max(1, bounds.height)));
      const offset = Math.max(0, top - bounds.top);
      position = { blockId: block.dataset.blockId, fraction, offset, sourceLine: Number(block.dataset.sourceStart) };
    }
    return position;
  }

  /** Use the visible source block when handing the document to the reader, which owns persistent reading history. */
  openReader() {
    if (!this.loaded) {
      return;
    }
    const visible = this.readingPosition();
    const position = visible && { blockId: visible.blockId, fraction: visible.fraction, sourceLine: visible.sourceLine };
    void this.host.emit({ type: "map-preview-open", workspaceId: this.host.workspaceId, stateId: this.host.state.stateId,
      path: this.current.path, anchor: this.current.anchor, position });
  }

  /** Center only when the destination card is outside the usable map area; ordinary reading leaves the camera alone. */
  ensureCardVisible() {
    const host = this.host;
    const card = host.cards.get(this.current.path)?.container;
    if (!card || card.hidden) {
      return;
    }
    const bounds = card.getBoundingClientRect();
    const viewport = host.viewport.getBoundingClientRect();
    const height = viewport.height;
    const inset = Math.min(150, height * .3);
    if (this.panel.parentNode === host.world) {
      const panelTop = this.panel.getBoundingClientRect().top;
      // Keep both the beginning of the page and a piece of the selected row in view when there is room.
      const usableTop = viewport.top + inset;
      if (panelTop < usableTop || panelTop + Math.min(140, height * .4) > viewport.bottom) {
        host.viewport.scrollTop += panelTop - usableTop - 16;
      }
      return;
    }
    if (bounds.right <= viewport.left || bounds.left >= viewport.right ||
        bounds.bottom <= viewport.top + inset || bounds.top >= viewport.bottom) {
      host.centerCard(this.current.path);
    }
  }

  /** Place the single surface beside a desktop card or directly after its row in the compact list. */
  place(metrics) {
    if (!this.current || this.panel.hidden) {
      return;
    }
    const host = this.host;
    const { width, height, compact } = metrics ?? host.atlas.viewportMetrics();
    const card = host.cards.get(this.current.path)?.container;
    const inline = compact && card?.parentNode === host.world;
    const focused = host.root.activeElement;
    const preserveFocus = this.panel.contains(focused);
    const enteringList = inline && this.panel.parentNode !== host.world;
    if (inline && card.nextElementSibling !== this.panel) {
      card.after(this.panel);
    } else if (!inline && this.panel.parentNode !== host.body) {
      host.body.append(this.panel);
    }
    if (preserveFocus && host.root.activeElement !== focused) {
      focused.focus({ preventScroll: true });
    }
    this.panel.classList.toggle("map-reading-inline", Boolean(inline));
    if (inline) {
      this.panel.style.width = "100%";
      this.panel.style.height = Math.max(160, Math.min(500, height * .68)) + "px";
      this.panel.style.left = "";
      this.panel.style.top = "";
      if (enteringList) {
        this.ensureCardVisible();
      }
      return;
    }
    const inset = Math.min(150, height * .3);
    const margin = 8;
    const panelWidth = Math.max(0, Math.min(460, width - 2 * margin));
    const panelHeight = Math.max(0, Math.min(500, height - inset - 2 * margin));
    const node = host.nodes.get(this.current.path);
    let left = (width - panelWidth) / 2;
    let top = height - panelHeight - margin;
    if (node && !compact) {
      const cardLeft = host.x + node.x * host.scale;
      const cardRight = cardLeft + (node.width ?? host.metrics.width) * host.scale;
      left = cardRight + margin + panelWidth <= width ? cardRight + margin : cardLeft - margin - panelWidth;
      top = host.y + node.y * host.scale;
    }
    this.panel.style.width = panelWidth + "px";
    this.panel.style.height = panelHeight + "px";
    this.panel.style.left = Math.max(margin, Math.min(left, width - panelWidth - margin)) + "px";
    this.panel.style.top = Math.max(inset, Math.min(top, height - panelHeight - margin)) + "px";
  }

  /** Cancel only the managed request associated with the panel being replaced or closed. */
  cancelRequest() {
    if (this.pending) {
      const { request } = this.pending;
      this.pending = null;
      void this.host.emit({ ...request, cancel: true });
    }
  }

  /** Revert the old temporary folder and reveal the new one even if the new document fails to load. */
  updateFolderVisibility(previousPath, nextPath) {
    const host = this.host;
    const collapsed = host.state?.layout.atlas?.collapsedFolders;
    if (!collapsed?.length || host.rendering || previousPath === nextPath) {
      return;
    }
    const before = previousPath ? folderOf(previousPath) : null;
    const after = nextPath ? folderOf(nextPath) : null;
    if (before !== after && (collapsed.includes(before) || collapsed.includes(after))) {
      host.atlas.acceptState();
      host.updateBounds();
      host.updateEdges();
    }
  }

  /** Release the only document DOM. Explicit dismissal restores the camera and the original managed selection. */
  close(restoreOrigin = false) {
    this.returnTicket = null;
    if (!this.current) {
      return;
    }
    const pendingPath = this.pending?.entry.path;
    this.cancelRequest();
    const origin = this.origin;
    const previousPath = this.currentPath;
    this.current = null;
    this.origin = null;
    this.history.length = 0;
    this.panel.hidden = true;
    if (this.panel.parentNode !== this.host.body) {
      this.host.body.append(this.panel);
    }
    this.releasePage();
    this.status.replaceChildren();
    this.trigger.setAttribute("aria-expanded", "false");
    this.updateFolderVisibility(previousPath, null);
    if (restoreOrigin && origin) {
      this.restoreCamera(origin.camera);
      void this.returnToMap(origin, pendingPath);
    }
  }

  /** Restore focus and compact-list scroll after the original card rejoins the visible projection. */
  async returnToMap(origin, pendingPath) {
    const host = this.host;
    const ticket = {};
    const workspaceId = host.workspaceId;
    const stateId = host.state.stateId;
    this.returnTicket = ticket;
    if (origin.path && (host.selected !== origin.path || pendingPath && pendingPath !== origin.path)) {
      // A cancelled read may already have sent a selection. Queue the original selection behind it.
      await host.emit({ type: "map-select", workspaceId, stateId, path: origin.path, center: false });
    }
    if (this.returnTicket !== ticket || host.workspaceId !== workspaceId || host.state?.stateId !== stateId || host.suspended) {
      return;
    }
    this.returnTicket = null;
    this.restoreCamera(origin.camera);
    const focus = host.cards.get(origin.path)?.card ?? host.viewport;
    focus.focus({ preventScroll: true });
  }
}
