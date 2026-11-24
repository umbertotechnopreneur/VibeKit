// SPDX-License-Identifier: GPL-3.0-or-later

// Keep the full crop where possible without sampling across the source or display edge.
export function clipRipplePatch(sourceWidth, sourceHeight, x, y, monitor, maxSize) {
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
