/* VBWR B
 *
 * Project: MarkVibe
 * Repository: https://github.com/umbertotechnopreneur/MarkVibe
 * Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
 *
 * VibeWare: Human intent, AI execution, and plenty of tokens
 * Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
 *
 * Modified with AI: OpenAI Codex; readability revision; 2026-10-11
 * Human guidance: Umberto Giacobbi; repository readability rules and MIT licensing
 * Modified with AI: OpenAI Codex; retain only the current map sidecar in provider metadata; 2026-10-16
 * Human guidance: Umberto Giacobbi; simplify the unreleased model without backward compatibility
 * Modified with AI: OpenAI Codex; share SVG image validation with static style preservation; 2026-10-25
 * Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
 *
 * Copyright (c) 2026 Umberto Giacobbi
 * License: MIT (see LICENSE)
 *
 * VBWR E */

import { sanitizeSvg } from "./svg.js";
import { nativeCall } from "./native.js";
import { WorkspaceError, limits, normalizePath, checkCancelled, makeId } from "./path.js";

const workspaces = new Map();
const leases = new Map();
let assetBytes = 0;
let assetPixels = 0;
let pendingAssetBytes = 0;

/** A renderer resource provider delegates bounded reads to its native owner. */
class NativeWorkspace {
  /** Capture the managed workspace identity without owning files or navigation. */
  constructor(info) {
    this.id = info.id;
  }
  /** Read bounded native bytes through the desktop bridge and wrap them as a browser resource. */
  async blob(path, maximum, signal) {
    path = normalizePath(path, true);
    const base64 = await nativeCall("workspace.read", { workspaceId: this.id, path }, signal);
    if (base64.length > Math.ceil(maximum / 3) * 4 + 4) {
      throw new WorkspaceError("file-too-large", "This file exceeds its reading limit.");
    }
    const bytes = Uint8Array.from(atob(base64), character => character.charCodeAt(0));
    if (bytes.length > maximum) {
      throw new WorkspaceError("file-too-large", "This file exceeds its reading limit.");
    }
    const extension = path.split(".").at(-1).toLowerCase();
    const types = {
      svg: "image/svg+xml",
      png: "image/png",
      jpg: "image/jpeg",
      jpeg: "image/jpeg",
      gif: "image/gif",
      webp: "image/webp"
    };
    return new Blob([bytes], { type: types[extension] || "application/octet-stream" });
  }
}


/** Return a retained workspace provider or require the user to reopen it. */
export function getWorkspace(id) {
  const workspace = workspaces.get(id);
  if (!workspace) {
    throw new WorkspaceError("workspace-unavailable", "Reopen this workspace to continue reading.");
  }
  return workspace;
}

/** Retain a native resource provider and release leases from an earlier instance. */
export function registerNativeWorkspace(info) {
  releaseWorkspace(info.id);
  workspaces.set(info.id, new NativeWorkspace(info));
}

/** Validate a local image's format and dimensions before creating a displayable resource. */
async function imageBlob(blob, path) {
  if (/\.svg$/iu.test(path)) {
    return sanitizeSvg(blob);
  }
  const header = new Uint8Array(await blob.slice(0, 65536).arrayBuffer());
  const view = new DataView(header.buffer);
  let width = 0;
  let height = 0;
  let type = "";
  if (/\.png$/iu.test(path) && header.length >= 24 && view.getUint32(0) === 0x89504e47 && view.getUint32(4) === 0x0d0a1a0a) {
    width = view.getUint32(16);
    height = view.getUint32(20);
    type = "image/png";
  } else if (/\.gif$/iu.test(path) && header.length >= 10 && new TextDecoder().decode(header.subarray(0, 6)).match(/^GIF8[79]a$/u)) {
    width = view.getUint16(6, true);
    height = view.getUint16(8, true);
    type = "image/gif";
  } else if (/\.jpe?g$/iu.test(path) && header[0] === 255 && header[1] === 216) {
    let cursor = 2;
    while (cursor + 4 < header.length) {
      if (header[cursor] !== 255) {
        break;
      }
      while (header[cursor] === 255) {
        cursor++;
      }
      const marker = header[cursor++];
      if (marker === 217 || marker === 218) {
        break;
      }
      if (marker === 1 || marker >= 208 && marker <= 215) {
        continue;
      }
      if (cursor + 2 > header.length) {
        break;
      }
      const length = view.getUint16(cursor);
      if (length < 2 || cursor + length > header.length) {
        break;
      }
      if ([192, 193, 194, 195, 197, 198, 199, 201, 202, 203, 205, 206, 207].includes(marker) && length >= 7) {
        height = view.getUint16(cursor + 3);
        width = view.getUint16(cursor + 5);
        type = "image/jpeg";
        break;
      }
      cursor += length;
    }
  } else if (/\.webp$/iu.test(path) && header.length >= 30 && view.getUint32(0) === 0x52494646 && view.getUint32(8) === 0x57454250) {
    const kind = new TextDecoder().decode(header.subarray(12, 16));
    if (kind === "VP8X") {
      width = 1 + header[24] + (header[25] << 8) + (header[26] << 16);
      height = 1 + header[27] + (header[28] << 8) + (header[29] << 16);
    }
    else if (kind === "VP8 " && header[23] === 157 && header[24] === 1 && header[25] === 42) {
      width = view.getUint16(26, true) & 16383;
      height = view.getUint16(28, true) & 16383;
    }
    else if (kind === "VP8L" && header[20] === 47) {
      const bits = view.getUint32(21, true);
      width = (bits & 16383) + 1;
      height = ((bits >>> 14) & 16383) + 1;
    }
    type = "image/webp";
  }
  if (!width || !height || width > 10000 || height > 10000 || width * height > limits.imagePixels) {
    throw new WorkspaceError("invalid-image", "Choose a supported image smaller than 16 megapixels.");
  }
  return { blob: new Blob([blob], { type }), pixels: width * height };
}

/** Acquire a bounded local image lease and track its owner, byte and pixel budgets. */
export async function acquireAsset(workspaceId, path, owner, signal) {
  const workspace = getWorkspace(workspaceId);
  path = normalizePath(path);
  if (!/\.(png|jpe?g|gif|webp|svg)$/iu.test(path)) {
    throw new WorkspaceError("unsupported-image", "Choose a PNG, JPEG, GIF, WebP or SVG image.");
  }
  if (leases.size >= 96 || assetBytes + pendingAssetBytes + limits.assetBytes > limits.assetBudget) {
    throw new WorkspaceError("asset-budget", "Some images were paused to keep this workspace responsive.");
  }
  pendingAssetBytes += limits.assetBytes;
  let blob;
  try {
    blob = await workspace.blob(path, limits.assetBytes, signal);
    const image = await imageBlob(blob, path);
    blob = image.blob;
    checkCancelled(signal);
    if (leases.size >= 96 || assetBytes + blob.size > limits.assetBudget || assetPixels + image.pixels > limits.imagePixelBudget) {
      throw new WorkspaceError("asset-budget", "Some images were paused to keep this page responsive.");
    }
    const leaseId = makeId();
    const url = URL.createObjectURL(blob);
    leases.set(leaseId, { owner, workspaceId, bytes: blob.size, pixels: image.pixels, url });
    assetBytes += blob.size;
    assetPixels += image.pixels;
    return { url, leaseId, bytes: blob.size };
  } finally {
    pendingAssetBytes -= limits.assetBytes;
  }
}
/** Read a validated local image for export without retaining a reader object-URL lease. */
export async function readExportImage(workspaceId, path, signal) {
  // Return the same sanitized image representation without duplicating reader leases.
  // The export adapter enforces its own aggregate byte/pixel budget sequentially.
  path = normalizePath(path);
  if (!/\.(png|jpe?g|gif|webp|svg)$/iu.test(path)) {
    throw new WorkspaceError("unsupported-image", "This image format cannot be exported.");
  }
  return imageBlob(await getWorkspace(workspaceId).blob(path, limits.assetBytes, signal), path);
}
/** Release every asset lease associated with the specified resource owner. */
export function releaseAssets(owner) {
  for (const [id, lease] of leases)
    if (lease.owner === owner) {
      URL.revokeObjectURL(lease.url);
      assetBytes -= lease.bytes;
      assetPixels -= lease.pixels;
      leases.delete(id);
    }
}
/** Dispose the retained provider and leases for the requested workspace. */
export function releaseWorkspace(id) {
  for (const lease of [...leases.values()]) if (lease.workspaceId === id) {
    releaseAssets(lease.owner);
  }
  workspaces.delete(id);
}
