/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; dismissible startup presentation; 2026-10-25
 * Human guidance: Umberto Giacobbi; one extra second, immediate click dismissal
 * Modified with AI: OpenAI Codex; load packaged build metadata into the startup model; 2026-10-25
 * Modified with AI: OpenAI Codex; reveal the first readable document immediately; 2026-11-20
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */

/** Own the loading, failure and build identity shown before the first usable screen. */
class StartupService {
  /** Publish startup state through a renderer supplied by the host. */
  constructor(render) {
    this.state = { visible: true, failed: false, buildInfo: null };
    this.render = render;
  }

  /** Load packaged build-host facts without delaying or blocking application startup. */
  async loadBuildInfo() {
    try {
      const response = await fetch(new URL("build-info.json", document.baseURI), { cache: "no-store" });
      if (!response.ok) {
        return;
      }
      const info = await response.json();
      const fields = ["version", "configuration", "architecture", "machineName", "builtAtLocalDisplay", "timeZoneId", "utcOffset"];
      if (!fields.every(field => typeof info[field] === "string"
        && info[field].length > 0 && info[field].length <= 160
        && !/[\u0000-\u001f]/u.test(info[field]))) {
        return;
      }
      this.state.buildInfo = {
        version: `${info.version} · ${info.configuration} ${info.architecture}`,
        machine: info.machineName,
        built: `${info.builtAtLocalDisplay} ${info.utcOffset} · ${info.timeZoneId}`
      };
      this.render(this.state);
    } catch {
      // Unpackaged development hosts have no release build identity. Never
      // substitute the viewer's hostname or current time as compilation facts.
    }
  }

  /** Release the overlay as soon as the reader or a workspace recovery screen is ready. */
  ready() {
    if (this.state.failed || !this.state.visible) {
      return;
    }
    this.state.visible = false;
    this.render(this.state);
  }

  /** Leave the startup error and reload control visible if runtime loading fails. */
  fail() {
    this.state.visible = true;
    this.state.failed = true;
    this.render(this.state);
  }
}

/** Keep build metadata and recovery feedback visible until the first usable screen. */
export function attachStartup() {
  const overlay = document.getElementById("startup");
  const build = document.getElementById("startup-build");
  const buildVersion = document.getElementById("startup-build-version");
  const buildMachine = document.getElementById("startup-build-machine");
  const buildTime = document.getElementById("startup-build-time");
  const service = new StartupService(state => {
    overlay.hidden = !state.visible;
    build.hidden = !state.buildInfo;
    if (state.buildInfo) {
      buildVersion.textContent = state.buildInfo.version;
      buildMachine.textContent = state.buildInfo.machine;
      buildTime.textContent = state.buildInfo.built;
    }
  });
  window.addEventListener("markvibe-startup-ready", () => service.ready(), { once: true });
  void service.loadBuildInfo();
  return service;
}
