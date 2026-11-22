// VBWR B
// Project: Repo Radar
// Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
// VibeWare: Human intent, AI execution, and plenty of tokens
// Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto/
// Created with AI: OpenAI Codex; shared .NET prerequisite detection for MSI.
// Human guidance: Umberto Giacobbi; use the same installer layout and runtime check across VibeWare.
// Copyright (c) 2026 Umberto Giacobbi
// SPDX-License-Identifier: MIT
// VBWR E

/** Detect a compatible machine-installed .NET 10 x64 runtime without loading .NET. */
function CheckRepoRadarRuntime() {
  Session.Property("RepoRadarDotNetReady") = "";
  var minimumPatch = Number(Session.Property("RepoRadarRuntimeVersion").split(".")[2]);

  try {
    var files = new ActiveXObject("Scripting.FileSystemObject");
    if (HasRepoRadarRuntime(files, Session.Property("REPORADAR_DOTNET_X64_NATIVE"), minimumPatch) ||
        HasRepoRadarRuntime(files, Session.Property("REPORADAR_DOTNET_X64_OTHER"), minimumPatch) ||
        HasRepoRadarRuntime(files, GetDefaultRepoRadarRuntimeRoot(files), minimumPatch)) {
      Session.Property("RepoRadarDotNetReady") = "1";
    }
  } catch (error) {
    // Leave the flag unset so the installer can report the missing prerequisite.
  }
  return 1;
}

/** Return the default x64 runtime root on either x64 or Arm64 Windows. */
function GetDefaultRepoRadarRuntimeRoot(files) {
  var programFiles = Session.Property("ProgramFiles64Folder");
  if (!programFiles) return "";

  var shell = new ActiveXObject("WScript.Shell");
  var architecture = shell.ExpandEnvironmentStrings("%PROCESSOR_ARCHITEW6432%");
  if (architecture === "%PROCESSOR_ARCHITEW6432%") {
    architecture = shell.ExpandEnvironmentStrings("%PROCESSOR_ARCHITECTURE%");
  }
  switch (architecture.toUpperCase()) {
    case "ARM64": return files.BuildPath(programFiles, "dotnet\\x64");
    case "AMD64": return files.BuildPath(programFiles, "dotnet");
    default: return "";
  }
}

/** Check the stable runtime version and look for the files required by the app host. */
function HasRepoRadarRuntime(files, root, minimumPatch) {
  if (!/^[A-Z]:\\/i.test(root)) return false;

  try {
    var sharedFramework = files.BuildPath(root, "shared\\Microsoft.NETCore.App");
    var hostResolver = files.BuildPath(root, "host\\fxr");
    if (!files.FolderExists(sharedFramework) || !files.FolderExists(hostResolver)) return false;

    for (var entries = new Enumerator(files.GetFolder(sharedFramework).SubFolders);
         !entries.atEnd(); entries.moveNext()) {
      var folder = entries.item();
      var version = /^10\.0\.([0-9]+)$/.exec(folder.Name);
      if (!version || Number(version[1]) < minimumPatch) continue;

      if (files.FileExists(files.BuildPath(folder.Path, "coreclr.dll")) &&
          files.FileExists(files.BuildPath(folder.Path, "Microsoft.NETCore.App.deps.json")) &&
          files.FileExists(files.BuildPath(folder.Path, "System.Private.CoreLib.dll"))) {
        return HasRepoRadarHostResolver(files, hostResolver);
      }
    }
  } catch (error) {
    // A missing or inaccessible root does not satisfy the installer prerequisite.
  }
  return false;
}

/** Return whether the runtime installation has an app-host resolver. */
function HasRepoRadarHostResolver(files, hostResolver) {
  for (var entries = new Enumerator(files.GetFolder(hostResolver).SubFolders);
       !entries.atEnd(); entries.moveNext()) {
    if (files.FileExists(files.BuildPath(entries.item().Path, "hostfxr.dll"))) return true;
  }
  return false;
}
