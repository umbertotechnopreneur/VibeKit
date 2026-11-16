# Changes

## In development — 0.1.0

- Terminal RAM monitor derived from GPU Taskman, with system and process counters,
  Linux application grouping and persistent MIT attribution.
- Linux PSS/USS/RSS and process swap accounting; Windows RSS/USS with unsupported
  counters left unavailable. Live macOS collection is disabled and unsupported.
- On-demand Linux memory regions and experimental Windows virtual address ranges.
- Before/after comparison, saved captures and offline replay with explicit missing data.
- Optional sampling budgets, table pagination and reproducible local benchmarks.
- Native name/executable metadata reads without psutil command-line fallback or
  probes of executable files. Missing identities remain explicit.
- Ten-language dashboard, detail and region views, help, footer shortcuts, core CLI,
  backend diagnostics and capture dialogs. Technical identifiers and source OS
  messages remain unchanged; native-speaker review is welcome.
- Optional RAM hardware valuation from measured Linux PSS or Windows USS, with
  OS-visible capacity by default, optional nominal capacity and monthly allocation.
  No RSS fallback, live price feed, invented missing data or billing claim.
- F1 About with VibeWare branding, the project website and Scott Hanselman credits.
- Wheel/sdist verification with an isolated installation and offline replay.
- VibeKit path-scoped CI for Python 3.11, 3.13 and 3.14 on Linux and Windows;
  bounded native CLI smoke checks. No PyPI release.

See the [README](README.md) for current setup, platform limits and measurement
semantics. Historical validation notes in `docs/` are checkpoints, not claims that
all later integrations have been verified on the same hardware.
