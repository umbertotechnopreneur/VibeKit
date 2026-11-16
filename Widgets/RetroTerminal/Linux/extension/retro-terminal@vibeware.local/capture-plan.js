// SPDX-License-Identifier: MIT

// Keep history textures bounded independently of the monitor capture scale.
// The overlay still covers the original logical desktop; only its history is
// downsampled. Ceil matches the framebuffer coverage required by paint scale.
export function planCapture(logicalWidth, logicalHeight, nativeScale, maxPixels, maxDimension = 8192) {
    if (![logicalWidth, logicalHeight, nativeScale, maxPixels, maxDimension].every(n => Number.isFinite(n) && n > 0) ||
        !Number.isInteger(maxPixels) || !Number.isInteger(maxDimension))
        throw Error('Invalid afterglow capture dimensions');
    const size = scale => ({width: Math.max(1, Math.ceil(logicalWidth * scale)),
        height: Math.max(1, Math.ceil(logicalHeight * scale))});
    const fits = scale => {
        const {width, height} = size(scale);
        return width <= maxDimension && height <= maxDimension && width * height <= maxPixels;
    };
    let scale = Math.min(nativeScale, Math.sqrt(maxPixels / logicalWidth / logicalHeight),
        maxDimension / logicalWidth, maxDimension / logicalHeight);
    // Rounded dimensions can exceed the continuous pixel budget by one row or
    // column. Find a fitting scale instead of allocating beyond the limit.
    if (!fits(scale)) {
        let low = 0;
        let high = scale;
        for (let i = 0; i < 48; i++) {
            const middle = (low + high) / 2;
            if (fits(middle)) low = middle;
            else high = middle;
        }
        scale = low;
    }
    if (!(scale > 0) || !fits(scale)) throw Error('Cannot size the afterglow capture surface');
    return {...size(scale), scale, reduced: scale < nativeScale};
}
