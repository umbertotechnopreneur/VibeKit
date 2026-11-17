# Studio Controls shared core

This dependency-free .NET 10 library implements the OBS websocket v5 protocol and the studio action model. Windows uses it directly; the Linux controller uses the matching source names and scene definitions in `scene-catalog.json`.

Build and run the cross-platform tests from this directory:

```sh
dotnet build StudioControls.Core/StudioControls.Core.csproj
dotnet run --project StudioControls.Core.Tests/StudioControls.Core.Tests.csproj
```

The console harness returns a nonzero exit code on failure and gives each test an eight-second timeout. Its synthetic transports and temporary files do not connect to OBS, enumerate devices, access the user's OBS configuration, capture media or require test packages. A passing test run validates protocol and action semantics; it does not establish that a Windows camera, capture device, driver or plugin works on a particular computer.

## Integration

`ObsClient` accepts loopback addresses only. Keep its password in memory; the Windows adapter is responsible for OS-protected credential storage. Passwords are excluded from JSON serialization and from the settings record's string representation. The core has no telemetry, cloud reporting, background recovery or automatic recording restart.

An OBS request has a three-second absolute deadline, including queue wait, authentication, send and reply. Complete service operations also have deadlines: ten seconds for status, fifteen seconds for ordinary actions and thirty seconds for project updates. Request and action semaphores prevent concurrent command interleaving. A rejected RPC raises `ObsRequestException` and preserves the connection. An uncertain transport failure raises `ObsConnectionException`; its `RequestSent` property tells the UI that the operation may already have executed. Never automatically repeat a mutation after that failure. A later explicit operation may establish a fresh connection.

`StudioService` exposes scene selection, recording and pause, microphone/desktop mute, optional background blur, screen zoom/pan, independent screen/webcam/hardware vignettes and prepared project artwork. Status failures in optional plugins do not disable unrelated features. A confirmed recording or scene change remains successful if the subsequent status read fails; the returned `IsFresh`, `ConnectionAvailable` and `Warnings` explain the limitation. A recorded filename belongs only to the stop operation that produced it.

Screen framing checks the current scene twice and matches both item ID and source name before writing only the four crop fields. This rejects delayed slider updates after scene selection. OBS does not expose an atomic compare-and-set for scene changes, so another controller can still change the scene in the small interval after the final check; the crop remains scoped to the named scene and item.

Render and save project cards before calling `ApplyProjectSettingsAsync`. Six card files are checked before contacting OBS. Project updates span several RPCs and cannot be atomic in OBS. `ProjectApplyException.SavedLocally` distinguishes a partial remote update from lost project data. Branding and intro audio sources are optional and produce capability warnings when absent.

`SettingsStore` validates project settings and writes by staging a file beside its destination, flushing, and renaming it. Invalid existing settings are reported and preserved; loading a missing file returns defaults. No settings file should contain authentication secrets.

`ScenePackage.Prepare(templatePath, assetsRoot, outputPath)` resolves asset placeholders into a **new** import file, verifies referenced files, rejects path traversal and refuses to overwrite any existing file. It never writes the live OBS configuration. Import that file through OBS's Scene Collection menu and select the actual devices manually. The project logo is baked into cards; it does not require a separate OBS input.

## Versioned content

Commit the library, harness, shared metadata and sanitized scene template. Exclude build output, private settings, protected credential files, diagnostics, prepared machine-specific import files, recordings and captured media. Keep source aliases stable across operating systems; customize the catalog deliberately when adapting an existing scene collection.
