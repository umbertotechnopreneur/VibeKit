// VBWR B
//
// Project: MarkVibe
// Repository: https://github.com/umbertotechnopreneur/MarkVibe
// Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
//
// VibeWare: Human intent, AI execution, and plenty of tokens
// Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
//
// Created with AI: OpenAI Codex; shared .NET prerequisite check for MSI; 2026-11-22
// Human guidance: Umberto Giacobbi; check the shared runtime and offer Microsoft's download page
//
// Copyright (c) 2026 Umberto Giacobbi
// License: MIT (see LICENSE)
//
// VBWR E

/**
 * Detect a servicing-compatible Microsoft.NETCore.App installation without loading it.
 * Windows Installer supplies Session and hosts this immediate JScript custom action.
 */
function CheckMarkVibeRuntime() {
  Session.Property("MarkVibeRuntimeReady") = "";
  var minimum = Session.Property("MarkVibeRuntimeVersion").split(".");
  var architecture = Session.Property("MarkVibeRuntimeArchitecture");

  try {
    var files = new ActiveXObject("Scripting.FileSystemObject");
    var registryRoot64 = Session.Property("MARKVIBE_DOTNET_ROOT_64");
    var registryRoot32 = Session.Property("MARKVIBE_DOTNET_ROOT_32");
    if (HasCompatibleRuntime(files, registryRoot64, minimum) ||
        HasCompatibleRuntime(files, registryRoot32, minimum) ||
        HasCompatibleRuntime(files, GetDefaultRuntimeRoot(files, architecture), minimum)) {
      Session.Property("MarkVibeRuntimeReady") = "1";
    }
  } catch (error) {
    // The launch condition below the check reports that no compatible runtime was detected.
  }

  return 1; // msiDoActionStatusSuccess. Prerequisite handling belongs to the sequence.
}

/** Return the standard machine installation root for the package architecture. */
function GetDefaultRuntimeRoot(files, architecture) {
  var programFiles = Session.Property("ProgramFiles64Folder");
  if (!programFiles) {
    return "";
  }

  var nativeArchitecture = Session.Property("%PROCESSOR_ARCHITEW6432") ||
      Session.Property("%PROCESSOR_ARCHITECTURE") || "";
  nativeArchitecture = nativeArchitecture.toUpperCase();
  if (nativeArchitecture === "ARM64") {
    return files.BuildPath(programFiles, architecture === "x64" ? "dotnet\\x64" : "dotnet");
  }

  if (nativeArchitecture === "AMD64" && architecture === "x64") {
    return files.BuildPath(programFiles, "dotnet");
  }

  return "";
}

/** Check stable version directories and the files needed by a framework-dependent apphost. */
function HasCompatibleRuntime(files, root, minimum) {
  if (!/^[A-Z]:\\/i.test(root)) {
    return false;
  }

  try {
    var sharedFramework = files.BuildPath(root, "shared\\Microsoft.NETCore.App");
    var hostResolver = files.BuildPath(root, "host\\fxr");
    if (!files.FolderExists(sharedFramework) || !files.FolderExists(hostResolver)) {
      return false;
    }

    for (var folders = new Enumerator(files.GetFolder(sharedFramework).SubFolders);
         !folders.atEnd(); folders.moveNext()) {
      var folder = folders.item();
      var version = /^(\d+)\.(\d+)\.(\d+)$/.exec(folder.Name);
      if (!version || +version[1] !== +minimum[0] || +version[2] < +minimum[1] ||
          (+version[2] === +minimum[1] && +version[3] < +minimum[2])) {
        continue;
      }

      if (files.FileExists(files.BuildPath(folder.Path, "coreclr.dll")) &&
          files.FileExists(files.BuildPath(folder.Path, "Microsoft.NETCore.App.deps.json")) &&
          files.FileExists(files.BuildPath(folder.Path, "System.Private.CoreLib.dll"))) {
        return HasHostResolver(files, hostResolver);
      }
    }
  } catch (error) {
    // An inaccessible root cannot satisfy the prerequisite. Other roots can still be checked.
  }

  return false;
}

/** Return whether the installation contains a hostfxr library. */
function HasHostResolver(files, hostResolver) {
  for (var versions = new Enumerator(files.GetFolder(hostResolver).SubFolders);
       !versions.atEnd(); versions.moveNext()) {
    if (files.FileExists(files.BuildPath(versions.item().Path, "hostfxr.dll"))) {
      return true;
    }
  }

  return false;
}
