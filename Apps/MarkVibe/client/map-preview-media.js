/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; load graph preview images and diagrams on request
 * Human guidance: Umberto Giacobbi; keep one graph document open at a time
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
import { localize } from "./i18n.js";
import { acquireAsset, releaseAssets } from "./workspaces.js";

/** Keeps media off the graph's loading path until the reader explicitly requests an image or diagram. */
export class PreviewMedia {
  /** Names each page's resource owner independently of the full reader and earlier pages on this host. */
  constructor(hostId) {
    this.hostId = hostId;
    this.revision = 0;
    this.page = null;
  }

  /** Replaces media in the sanitized Markdown fragment with lightweight controls before it is mounted. */
  prepare(fragment, workspaceId) {
    this.clear();
    const page = {
      workspaceId,
      owner: this.hostId + "/preview/" + (++this.revision),
      lifecycle: new AbortController()
    };
    this.page = page;

    for (const image of fragment.querySelectorAll("img")) {
      this.prepareImage(image, page);
    }
    for (const diagram of fragment.querySelectorAll(".mv-diagram")) {
      // The parser already replaced oversized diagrams with an explanation.
      if (diagram.querySelector("code.language-mermaid")) {
        this.prepareDiagram(diagram, page);
      }
    }
  }

  /** Keeps linked-image navigation accessible while offering a separate control for loading the local asset. */
  prepareImage(image, page) {
    const path = image.dataset.mvAsset;
    const label = image.alt.trim() || path?.split("/").pop();
    const placeholder = document.createElement("span");
    placeholder.className = "map-reading-image";
    if (image.dataset.elementId) {
      placeholder.dataset.elementId = image.dataset.elementId;
    }
    if (label) {
      placeholder.textContent = label;
    } else {
      localize(placeholder, "Image unavailable");
    }

    const link = image.closest("a");
    image.replaceWith(placeholder);
    if (!path) {
      localize(placeholder, "Remote or unsafe image not loaded.", "title");
      return;
    }

    const button = document.createElement("button");
    button.type = "button";
    const compactLabel = label.length > 60 ? label.slice(0, 59) + "…" : label;
    localize(button, "Show image · {0}", "textContent", compactLabel);
    localize(button, "Show image · {0}", "aria-label", label);
    if (link) {
      // Interactive buttons must sit outside anchors, including links that wrap more than the image.
      link.after(button);
    } else {
      placeholder.replaceChildren(button);
    }

    const signal = page.lifecycle.signal;
    button.addEventListener("click", async () => {
      button.disabled = true;
      button.setAttribute("aria-busy", "true");
      try {
        const lease = await acquireAsset(page.workspaceId, path, page.owner, signal);
        if (signal.aborted) {
          releaseAssets(page.owner);
          return;
        }
        image.addEventListener("error", () => {
          localize(placeholder, "This image could not be displayed.");
          image.replaceWith(placeholder);
        }, { once: true, signal });
        signal.addEventListener("abort", () => image.removeAttribute("src"), { once: true });
        image.src = lease.url;
        button.remove();
        placeholder.replaceWith(image);
      } catch (error) {
        if (!signal.aborted) {
          button.remove();
          localize(placeholder, error.message || "This image could not be displayed.");
        }
      }
    }, { once: true, signal });
  }

  /** Imports the isolated renderer only after a click and discards a late completion after the page changes. */
  prepareDiagram(diagram, page) {
    const button = localize(document.createElement("button"), "Show diagram");
    button.type = "button";
    if (diagram.dataset.elementId) {
      button.dataset.elementId = diagram.dataset.elementId;
    }
    diagram.replaceWith(button);

    const signal = page.lifecycle.signal;
    button.addEventListener("click", async () => {
      button.disabled = true;
      button.setAttribute("aria-busy", "true");
      try {
        const { renderDiagrams } = await import("./diagrams.js");
        if (signal.aborted) {
          return;
        }
        await renderDiagrams([diagram], () => !signal.aborted, undefined, signal);
        if (!signal.aborted) {
          button.replaceWith(diagram);
        }
      } catch {
        if (!signal.aborted) {
          const message = localize(document.createElement("span"), "Diagram unavailable");
          button.replaceWith(message);
        }
      }
    }, { once: true, signal });
  }

  /** Aborts pending work, detaches media listeners and revokes this page's object URLs synchronously. */
  clear() {
    const page = this.page;
    if (page) {
      this.page = null;
      page.lifecycle.abort();
      releaseAssets(page.owner);
    }
  }
}
