/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; regression check for the painted reader viewport
 * Human guidance: continuous paper under window edges and scrollbars
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { chromium } from "playwright";

const assets = new URL("../../src/MarkVibe.Windows/Rendering/web/", import.meta.url);
const sheet = await readFile(new URL("css/content.css", assets), "utf8");
const desktop = await readFile(new URL("desktop.css", assets), "utf8");
// Keep classic scrollbars visible even on headless Chromium, which normally suppresses them.
const browser = await chromium.launch({ channel: "msedge", headless: true, ignoreDefaultArgs: ["--hide-scrollbars"] });
try {
  const page = await browser.newPage({ viewport: { width: 700, height: 400 } });
  for (const theme of ["light", "dark"]) {
    const image = await readFile(new URL(`images/paper-ivory-${theme}.png`, assets));
    const tile = "data:image/png;base64," + image.toString("base64");
    for (const view of ["read", "split"]) {
      await page.setContent(`<style>${desktop}</style><main id="surface" data-view="${view}">
        <div id="source" ${view === "split" ? "" : "hidden"}></div><div id="reader"></div></main>`);
      await page.evaluate(async ({ theme, sheet, tile }) => {
        document.documentElement.dataset.theme = theme;
        const host = document.createElement("div");
        host.style.cssText = "position:absolute;inset:0;overflow:hidden";
        host.dataset.paper = "ivory";
        host.style.setProperty("--reader-paper", `url("${tile}")`);
        document.querySelector("#reader").append(host);
        const root = host.attachShadow({ mode: "open" });
        const style = document.createElement("style");
        style.textContent = sheet;
        root.append(style);
        const reader = document.createElement("div");
        reader.className = "mv-view reader";
        reader.tabIndex = 0;
        reader.innerHTML = "<article class='markdown'>A short synthetic document.</article>";
        root.append(reader);
        const reference = new Image();
        reference.src = tile;
        await reference.decode();
      }, { theme, sheet, tile });

      for (const long of [false, true]) {
        const scroller = page.locator(".mv-view.reader");
        const box = await scroller.evaluate((reader, tall) => {
          reader.firstElementChild.style.minHeight = tall ? "1800px" : "0";
          const style = getComputedStyle(reader);
          const rect = reader.getBoundingClientRect();
          const overflows = reader.scrollHeight > reader.clientHeight;
          return { x: rect.x, y: rect.y, width: rect.width, height: rect.height,
            gutter: reader.offsetWidth - reader.clientWidth, radius: style.borderTopRightRadius,
            overflow: style.overflowY, overflows, thumb: style.scrollbarColor };
        }, long);
        assert.equal(box.radius, "0px");
        assert.equal(box.overflow, "auto");
        assert.equal(box.overflows, long);
        if (long) assert.ok(box.gutter > 0, "The fixture needs a classic scrollbar to check its track");
        else assert.equal(box.gutter, 0);
        assert.doesNotMatch(box.thumb, /^rgba\(0, 0, 0, 0\) /);
        // Inspect rendered pixels, including the classic scrollbar track below the thumb.
        const screenshot = (await page.screenshot()).toString("base64");
        const differences = await page.evaluate(async ({ screenshot, tile, box }) => {
          /** Decodes an image to a readable pixel surface without altering the test page. */
          async function pixels(source) {
            const image = new Image();
            image.src = source;
            await image.decode();
            const canvas = document.createElement("canvas");
            canvas.width = image.width;
            canvas.height = image.height;
            const context = canvas.getContext("2d");
            context.drawImage(image, 0, 0);
            return context;
          }
          const screen = await pixels("data:image/png;base64," + screenshot);
          const paper = await pixels(tile);
          return [[1, 1], [1, box.height - 2], [box.width - 2, box.height * .73]].map(([x, y]) => {
            const observed = screen.getImageData(box.x + x, box.y + y, 1, 1).data;
            const expected = paper.getImageData(x % 256, y % 256, 1, 1).data;
            return Math.max(...[0, 1, 2].map(channel => Math.abs(observed[channel] - expected[channel])));
          });
        }, { screenshot, tile, box });
        assert.ok(differences.every(delta => delta <= 2), JSON.stringify({ theme, view, long, box, differences }));
      }
      await page.keyboard.press("Tab");
      const focusedRadius = await page.locator(".mv-view.reader").evaluate(reader =>
        reader.matches(":focus-visible") ? getComputedStyle(reader).borderTopRightRadius : null);
      assert.equal(focusedRadius, "0px");
      await page.emulateMedia({ forcedColors: "active" });
      const contrast = await page.locator(".mv-view.reader").evaluate(reader => {
        const style = getComputedStyle(reader);
        return [style.backgroundImage, style.scrollbarColor];
      });
      assert.deepEqual(contrast, ["none", "auto"]);
      await page.emulateMedia({ forcedColors: "none" });
      await page.emulateMedia({ media: "print" });
      assert.equal(await page.locator(".mv-view.reader").evaluate(reader => getComputedStyle(reader).backgroundImage), "none");
      await page.emulateMedia({ media: "screen" });
    }
  }
  console.log("Reader edges, paper, overflow and contrast passed in light and dark Read and Split views.");
} finally {
  await browser.close();
}
