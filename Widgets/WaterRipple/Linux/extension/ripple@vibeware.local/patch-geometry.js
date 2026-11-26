// SPDX-License-Identifier: GPL-3.0-or-later

const MAX_TEXTURE_SIDE = 1024;
const TEXTURE_PADDING = 8;

export function safePatchSize(size, scale) {
    if (!Number.isFinite(scale) || scale < 1) return 0;
    return Math.max(0, Math.min(size, Math.floor(MAX_TEXTURE_SIDE / scale) - TEXTURE_PADDING));
}

// Keep the full crop where possible without sampling across the source or display edge.
export function clipRipplePatch(sourceWidth, sourceHeight, x, y, monitor, maxSize) {
    if (![sourceWidth, sourceHeight, x, y, monitor.x, monitor.y, monitor.width, monitor.height, maxSize]
        .every(Number.isFinite)) return null;
    const minX = Math.max(0, monitor.x);
    const minY = Math.max(0, monitor.y);
    const maxX = Math.min(sourceWidth, monitor.x + monitor.width);
    const maxY = Math.min(sourceHeight, monitor.y + monitor.height);
    if (x < minX || y < minY || x >= maxX || y >= maxY)
        return null;

    const width = Math.min(maxSize, maxX - minX);
    const height = Math.min(maxSize, maxY - minY);
    if (width < 16 || height < 16)
        return null;

    const left = Math.max(minX, Math.min(maxX - width, x - width / 2));
    const top = Math.max(minY, Math.min(maxY - height, y - height / 2));
    return {left, top, width, height};
}
