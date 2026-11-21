// Copyright (c) 2026 Umberto Giacobbi. SPDX-License-Identifier: MIT

use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::io::Write;
use std::path::PathBuf;
use std::sync::{mpsc, Arc};
use std::time::Duration;

const LIMIT_BYTES: u64 = 1_000_000_000;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ProcessMemory {
    pid: u32,
    parent_pid: u32,
    name: String,
    bytes: Option<u64>,
    #[serde(skip)]
    started: u64,
}

#[cfg(target_os = "linux")]
fn entries() -> Vec<ProcessMemory> {
    // SAFETY: sysconf reads the page size and has no pointer arguments.
    let page_size = unsafe { libc::sysconf(libc::_SC_PAGESIZE) };
    if page_size <= 0 {
        return Vec::new();
    }
    let Ok(directories) = std::fs::read_dir("/proc") else {
        return Vec::new();
    };
    directories.flatten().filter_map(|directory| {
        let pid = directory.file_name().to_str()?.parse().ok()?;
        let bytes = std::fs::read(directory.path().join("stat")).ok()?;
        let stat = String::from_utf8_lossy(&bytes);
        let (identity, fields) = stat.rsplit_once(')')?;
        let name = identity.split_once('(')?.1.to_owned();
        let mut fields = fields.split_ascii_whitespace();
        if fields.next()? == "Z" {
            return None;
        }
        let parent_pid = fields.next()?.parse().ok()?;
        let started = fields.nth(17)?.parse().ok()?;
        let pages = fields.nth(1)?.parse::<u64>().ok()?;
        Some(ProcessMemory { pid, parent_pid, name, started, bytes: pages.checked_mul(page_size as u64) })
    }).collect()
}

#[cfg(target_os = "windows")]
fn entries() -> Vec<ProcessMemory> {
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
    };
    let mut result = Vec::new();
    // SAFETY: PROCESSENTRY32W is a C structure initialized as required by Tool Help.
    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snapshot == INVALID_HANDLE_VALUE {
            return result;
        }
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut available = Process32FirstW(snapshot, &mut entry);
        while available != 0 {
            let end = entry.szExeFile.iter()
                .position(|character| *character == 0)
                .unwrap_or(entry.szExeFile.len());
            result.push(ProcessMemory {
                pid: entry.th32ProcessID,
                parent_pid: entry.th32ParentProcessID,
                name: String::from_utf16_lossy(&entry.szExeFile[..end]),
                started: 0,
                bytes: None,
            });
            available = Process32NextW(snapshot, &mut entry);
        }
        CloseHandle(snapshot);
    }
    result
}

#[cfg(target_os = "windows")]
fn measure(entry: &mut ProcessMemory) {
    use windows_sys::Win32::Foundation::{CloseHandle, FILETIME};
    use windows_sys::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS, PROCESS_MEMORY_COUNTERS_EX};
    use windows_sys::Win32::System::Threading::{GetProcessTimes, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};
    // SAFETY: The handle is local and closed after querying initialized C structures.
    unsafe {
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, entry.pid);
        if process.is_null() {
            return;
        }
        let mut created = FILETIME::default();
        let mut exited = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        if GetProcessTimes(process, &mut created, &mut exited, &mut kernel, &mut user) != 0
            && exited.dwLowDateTime == 0 && exited.dwHighDateTime == 0
        {
            entry.started = (u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime);
            let mut counters: PROCESS_MEMORY_COUNTERS_EX = std::mem::zeroed();
            let size = std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32;
            counters.cb = size;
            let address = (&mut counters as *mut PROCESS_MEMORY_COUNTERS_EX).cast::<PROCESS_MEMORY_COUNTERS>();
            if K32GetProcessMemoryInfo(process, address, size) != 0 {
                entry.bytes = Some(counters.PrivateUsage as u64);
            }
        }
        CloseHandle(process);
    }
}

#[cfg(not(any(target_os = "linux", target_os = "windows")))]
fn entries() -> Vec<ProcessMemory> { Vec::new() }

fn process_tree() -> Vec<ProcessMemory> {
    let root_pid = std::process::id();
    #[cfg(target_os = "windows")]
    let snapshot_started = (chrono::Utc::now().timestamp_micros() * 10 + 116_444_736_000_000_000) as u64;
    let mut children: HashMap<_, Vec<_>> = HashMap::new();
    let mut root = None;
    for entry in entries() {
        if entry.pid == root_pid {
            root = Some(entry);
        } else {
            children.entry(entry.parent_pid).or_default().push(entry);
        }
    }
    let Some(root) = root else { return Vec::new() };
    let mut pending = vec![(root, 0)];
    let mut seen = HashSet::new();
    let mut result = Vec::new();
    while let Some((entry, parent_started)) = pending.pop() {
        if !seen.insert(entry.pid) {
            continue;
        }
        #[cfg(target_os = "windows")]
        let entry = {
            let mut entry = entry;
            measure(&mut entry);
            entry
        };
        // Tool Help can return an old PID entry if the process ended during enumeration.
        #[cfg(target_os = "windows")]
        if entry.started > snapshot_started {
            continue;
        }
        if entry.started > 0 && entry.started < parent_started {
            continue;
        }
        if entry.started > 0 {
            if let Some(descendants) = children.remove(&entry.pid) {
                pending.extend(descendants.into_iter().map(|child| (child, entry.started)));
            }
        }
        result.push(entry);
    }
    result
}

fn write_log(bytes: u64, processes: &[ProcessMemory]) -> std::io::Result<()> {
    let settings = crate::paths::default_settings().map_err(|error| std::io::Error::other(error.to_string()))?;
    let directory: PathBuf = settings.with_file_name("logs");
    std::fs::create_dir_all(&directory)?;
    let path = directory.join("memory-watchdog.jsonl");
    if path.metadata().is_ok_and(|metadata| metadata.len() >= 512 * 1024) {
        std::fs::rename(&path, directory.join("memory-watchdog.jsonl.1")).or_else(|_| std::fs::remove_file(&path))?;
    }
    let event = serde_json::json!({
        "atUtc": chrono::Utc::now().to_rfc3339(), "kind": "guard.memory-limit",
        "rootPid": std::process::id(), "bytes": bytes, "limitBytes": LIMIT_BYTES,
        "metric": if cfg!(windows) { "privateBytes" } else { "rssBytes" }, "processes": processes,
    });
    let mut file = std::fs::OpenOptions::new().create(true).append(true).open(path)?;
    writeln!(file, "{event}")
}

/// Start a process-tree guard, returning a sender whose drop cancels monitoring.
pub(crate) fn start(control: Arc<crate::tray::Control>) -> std::io::Result<mpsc::Sender<()>> {
    let (sender, receiver) = mpsc::channel();
    std::thread::Builder::new().name("UlanziDeck RAM watchdog".into()).spawn(move || {
        let mut previous_over = false;
        while receiver.recv_timeout(Duration::from_secs(2)) == Err(mpsc::RecvTimeoutError::Timeout) {
            let processes = process_tree();
            let total: u64 = processes.iter().filter_map(|entry| entry.bytes).sum();
            let over = total > LIMIT_BYTES;
            if over && previous_over {
                let _ = write_log(total, &processes);
                control.stop_for_memory();
                let _ = writeln!(std::io::stderr(),
                    "UlanziDeck exceeded its memory limit. See memory-watchdog.jsonl in the local logs.");
                if receiver.recv_timeout(Duration::from_secs(5)) == Err(mpsc::RecvTimeoutError::Timeout) {
                    std::process::exit(137);
                }
                return;
            }
            previous_over = over;
        }
    })?;
    Ok(sender)
}
