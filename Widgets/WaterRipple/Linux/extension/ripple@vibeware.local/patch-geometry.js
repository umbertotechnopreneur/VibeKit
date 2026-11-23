// SPDX-License-Identifier: GPL-3.0-or-later

// Preserve the normal source crop, then restrict the sampled area to the selected monitor.
export function clipRipplePatch(sourceWidth, sourceHeight, x, y, monitor, maxSize) {
    if (x < 0 || y < 0 || x >= sourceWidth || y >= sourceHeight)
        return null;

    const width = Math.min(maxSize, sourceWidth);
    const height = Math.min(maxSize, sourceHeight);
    const originX = Math.max(0, Math.min(sourceWidth - width, x - width / 2));
    const originY = Math.max(0, Math.min(sourceHeight - height, y - height / 2));
    const left = Math.max(originX, monitor.x);
    const top = Math.max(originY, monitor.y);
    const clippedWidth = Math.min(originX + width, monitor.x + monitor.width) - left;
    const clippedHeight = Math.min(originY + height, monitor.y + monitor.height) - top;
    if (clippedWidth < 16 || clippedHeight < 16 || x < left || y < top ||
        x >= left + clippedWidth || y >= top + clippedHeight)
        return null;

    return {left, top, width: clippedWidth, height: clippedHeight};
}
