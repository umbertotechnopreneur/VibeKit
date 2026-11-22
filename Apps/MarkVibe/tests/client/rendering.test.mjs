/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; synthetic rendering and source-resource regression checks; 2026-10-23
 * Human guidance: Umberto Giacobbi; optimize without regressions and run relevant JavaScript tests
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";
import { validateBundleConfiguration } from "../../scripts/client-inputs.mjs";

test("Release bundle validation rejects Debug output and source maps while retaining optimized Debug builds", () => {
  const optimized = { configuration: "Release", outputs: ["src/MarkVibe.Windows/obj/desktop-js/release/markvibe.js"] };
  const debug = { configuration: "Debug", outputs: [...optimized.outputs, optimized.outputs[0] + ".map"] };
  assert.doesNotThrow(() => validateBundleConfiguration(optimized, "Release"));
  assert.doesNotThrow(() => validateBundleConfiguration(optimized, "Debug"));
  assert.doesNotThrow(() => validateBundleConfiguration(debug, "Debug"));
  assert.throws(() => validateBundleConfiguration(debug, "Release"), /Debug client assets/);
  assert.throws(() => validateBundleConfiguration(debug), /Debug client assets/);
  assert.throws(() => validateBundleConfiguration({ ...debug, configuration: "Release" }, "Release"), /source maps/);
  assert.throws(() => validateBundleConfiguration({ outputs: optimized.outputs }, "Release"), /configuration evidence/);
});

// Bundle the real hosts with inert DOM/Monaco boundaries; no browser or desktop is started.
const result = await build({
  stdin: {
    contents: `export { SourceHost, disposeSourceModels } from "./client/source.js";
      export { models } from "./client/monaco-adapter.js";`,
    resolveDir: fileURLToPath(new URL("../../", import.meta.url))
  },
  bundle: true,
  write: false,
  format: "esm",
  platform: "node",
  plugins: [{
    name: "synthetic-host-boundaries",
    /** Substitute only browser integration boundaries, retaining host lifecycle and drawing code. */
    setup(builder) {
      builder.onResolve({ filter: /\/(markdown|workspaces|monaco-adapter)\.js$/ }, args => ({ path: args.path.split("/").at(-1), namespace: "fixture" }));
      builder.onLoad({ filter: /markdown\.js$/, namespace: "fixture" }, () => ({ contents: `
        export function createRoot() { throw new Error("DOM mounting is outside this unit check"); }
        export function prepareMarkdown() { throw new Error("Document rendering is outside this unit check"); }
        export function textElement() { return {}; }
        export function uiText() { return {}; }
      ` }));
      builder.onLoad({ filter: /workspaces\.js$/, namespace: "fixture" }, () => ({ contents: `
        export function acquireAsset() { throw new Error("No asset reads in this check"); }
        export function releaseAssets() { throw new Error("No asset owners in this check"); }
      ` }));
      builder.onLoad({ filter: /monaco-adapter\.js$/, namespace: "fixture" }, () => ({ contents: `
        export const models = [];
        export const monaco = {
          Uri: { parse(value) { return value; } },
          editor: {
            createModel(source) {
              const model = { disposed: false, getValue() { return source; }, dispose() { this.disposed = true; } };
              models.push(model);
              return model;
            },
            create() {
              return {
                layout() {}, dispose() {}, saveViewState() { return {}; }, restoreViewState() {},
                onDidScrollChange() { return { dispose() {} }; },
                onDidChangeCursorSelection() { return { dispose() {} }; }
              };
            }
          }
        };
      ` }));
    }
  }]
});
const { SourceHost, disposeSourceModels, models } = await import(
  "data:text/javascript;base64," + Buffer.from(result.outputFiles[0].text).toString("base64")
);

// Node has no animation or document surface. Inert globals are scoped to this test process.
globalThis.requestAnimationFrame = () => { throw new Error("Unexpected animation request"); };
globalThis.cancelAnimationFrame = () => {};
globalThis.matchMedia = () => ({ matches: false });
globalThis.document = {
  documentElement: { dataset: { theme: "dark" } },
  createElement() { throw new Error("Unexpected DOM element creation"); },
  createElementNS() { throw new Error("Unexpected SVG element creation"); }
};

/** Provide only the source-host state needed to exercise actual asynchronous render and disposal paths. */
function sourceHost() {
  return Object.assign(Object.create(SourceHost.prototype), {
    revision: 0,
    container: { clientWidth: 800, isConnected: true },
    content: { replaceChildren() {} },
    capture() { return { sourceLine: 1 }; },
    emit() {}
  });
}

test("source model eviction skips a visible model and still releases older unused documents", async context => {
  context.mock.method(globalThis, "matchMedia", () => ({ matches: false }));
  const persistent = sourceHost();
  const browsing = sourceHost();
  context.after(() => disposeSourceModels());
  await persistent.render({ workspaceId: "fixture", path: "visible.md", source: "# Visible", preferences: {} });
  for (let index = 0; index < 5; index++) {
    await browsing.render({ workspaceId: "fixture", path: `page-${index}.md`, source: `# Page ${index}`, preferences: {} });
  }
  assert.equal(models.length, 6);
  assert.equal(models[0].disposed, false);
  assert.equal(models.filter(model => !model.disposed).length, 3);
  assert.equal(models[1].disposed, true);
  assert.equal(models.at(-1).disposed, false);
  persistent.saveEditor();
  browsing.saveEditor();
});

test("replacing source releases detached fallback rows and editor subscriptions", () => {
  const released = [];
  const host = Object.assign(sourceHost(), {
    textarea: { value: "large old document" },
    lineElements: [{}],
    pane: {},
    editor: { dispose() { released.push("editor"); } },
    scrollSubscription: { dispose() { released.push("scroll"); } },
    selectionSubscription: { dispose() { released.push("selection"); } }
  });
  host.saveEditor();
  assert.deepEqual(released, ["editor", "scroll", "selection"]);
  for (const name of ["editor", "scrollSubscription", "selectionSubscription", "textarea", "lineElements", "pane"]) {
    assert.equal(host[name], null);
  }
});
