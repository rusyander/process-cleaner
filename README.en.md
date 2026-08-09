# Windows Process Cleaner

[🇷🇺 Русский](README.md) · 🇬🇧 English

Windows maintenance in a single window: **disk cleanup**, **terminating forgotten
processes**, **freeing RAM**, **updating installed programs**, managing **startup items**,
uninstalling programs, and **Docker** cleanup.

Written in **C# + WinForms** and built with the **compiler that ships with
Windows, `csc.exe`** (.NET Framework 4.x). **Nothing to install** — no Node.js,
no Rust, no Visual Studio. The result is one self-contained `.exe` you can just carry
around in a folder.

---

## What this program is for

One tool instead of a pile of third-party "optimizers": kill processes left hanging, free
up RAM, wipe accumulated disk junk and install the updates that have shipped — without
installing anything into the system, with a preview before every deletion, a Russian and
English UI, tray operation and a schedule.

## What the window contains

| Tab | What it does |
|---|---|
| **Scan** | finds abandoned processes and terminates them, purges Standby Memory |
| **Dev Cleanup** | bulk-kills dev runtimes and frees busy dev ports |
| **Disk Cleanup** | analyzes and deletes junk by category, optional winapp2 rules |
| **Docker** | disk-usage overview, removal of unused data, vhdx compaction |
| **Programs** | installed software list, uninstall via the program's own uninstaller |
| **Updates** | finds outdated programs and updates them via winget / Chocolatey |
| **Startup** | what launches with Windows, enable and disable |
| **Settings** | all thresholds, lists and parameters |
| **History** | what was cleaned and when |

## What it can do

- **Processes.** Finds forgotten/abandoned processes (dev runtimes, or — in global
  mode — any of yours) and terminates them safely: gracefully first (WM_CLOSE), then
  forcefully. Shows for each: PID, PPID, path, uptime, CPU, RAM, windows, TCP ports,
  child processes, owner.
- **RAM.** Purges Standby Memory via WinAPI (`NtSetSystemInformation`), no third-party exe.
- **Dev Cleanup.** Bulk-terminates Node/Python/Java/Vite/Webpack/npm/pnpm/yarn and frees
  busy dev ports (3000, 5173, 8080, 4200 …).
- **Disk cleanup.** Analyzes and deletes known junk: dev caches (npm/pnpm/yarn/pip/
  gradle/cargo/go/NuGet), system junk (temp, Recycle Bin, Windows Update, dumps),
  browser and app caches (Discord/Slack/Teams/Spotify), old logs, driver installer
  leftovers and `Windows.old`. Optionally plugs in **winapp2 rules** — an open database
  covering thousands of programs. Shows sizes first, deletes only after confirmation.
- **Docker.** Shows disk usage (`docker system df`) and removes unused data: stopped
  containers, images, volumes, build cache, everything at once.
- **Programs.** Lists installed software and uninstalls it via its own uninstaller.
- **Updates.** Scans the machine for programs with newer versions available, rates how big
  each update is (major / minor / patch) and installs the selected ones — using **winget**
  and **Chocolatey**, several packages per command.
- **Startup.** Lists all installed programs with a "in Windows startup" toggle: check to
  add, uncheck to remove (registry `Run` keys + the Startup folder).
- **Automation.** Process auto-clean timer (every 1–24 h), start with Windows (via Task
  Scheduler), tray operation, cleanup history, flexible settings.
- **Look & feel.** Dark/light theme (or system) + dark title bar; RU/EN UI language.

## What it does NOT do (safety boundaries)

- **Doesn't break Windows.** System processes (SYSTEM/services), components under
  `C:\Windows`, critical and protected processes (shell, clouds, drivers, messengers)
  are never eligible for termination in global mode; the guards fail safe.
- **Doesn't kill active things by mistake.** A scan candidate is picked only when all
  criteria match at once (dead parent + idle + no windows/ports/children), with
  confirmation. The exception is the Dev Cleanup buttons — a deliberate by-name sledgehammer.
- **Doesn't do disk-wide duplicate search** and never deletes anything from your projects,
  code, `DriverStore`, System32 or drive roots. Only known junk paths are cleaned.
- **Doesn't clean disk, uninstall programs or update them on a schedule** — manual only,
  with preview and confirmation. The timer only runs process cleanup.
- **Doesn't touch the registry when updating programs.** The package manager (winget /
  Chocolatey) performs the install; the app only lists what is available and hands it
  the command.
- **Docker: removes only unused data** (`prune`) — running containers and used images are
  never touched. Kubernetes is not included.

---

## Installation

### What you need

| Requirement | Note |
|---|---|
| Windows 10 or 11 (x64) | Windows 8.1 works too |
| .NET Framework 4.x | **already part of Windows**, nothing to install |
| Administrator rights | required to purge Standby Memory |

Visual Studio, the .NET SDK, Node.js and any other package are **not required** — the
program is built by `csc.exe`, which already sits inside Windows.

Optional, and only for the corresponding tabs:

- **winget** — for the Updates tab. Windows 11 and recent Windows 10 ship with it; if you
  don't have it, install "App Installer" from the Microsoft Store.
- **Chocolatey** — an optional second update source. Without it the tab simply works
  through winget alone.
- **Docker Desktop** — for the Docker tab (a running daemon is needed).

### Steps

1. **Get the files.** Either `git clone` the repository, or download the ZIP and extract it
   anywhere, e.g. `C:\Tools\cleaner`.

2. **If you downloaded a ZIP, unblock it.** Windows marks files that came from the
   internet, and the build may refuse to run. Right-click the ZIP → **Properties** →
   tick **Unblock** at the bottom → OK, and only then extract. Already extracted? Run this
   in PowerShell inside the project folder:

   ```powershell
   Get-ChildItem -Recurse | Unblock-File
   ```

3. **Double-click `run.bat`.** On first launch it builds `WindowsProcessCleaner.exe` and
   opens it right away. After that you can run the `.exe` directly.

   Build only, without running:

   ```
   build.bat
   ```

4. **Accept the UAC prompt.** The app always runs as administrator — that's needed to purge
   Standby Memory. A prompt on every launch is expected.

That's it. Nothing is installed into the system: `WindowsProcessCleaner.exe` is
self-contained and can simply live in a folder you carry around.

### If something goes wrong

**"Windows protected your PC" (SmartScreen).** The file was built locally on your machine
and is not signed with a certificate, so Windows doesn't recognise it. Click **More info**
→ **Run anyway**.

**Antivirus complains about the freshly built `.exe`.** A false positive on an unsigned
binary. Add the project folder to exclusions, or rebuild.

**`[ERROR] csc.exe of .NET Framework 4.x not found`.** .NET Framework 4.x is missing
(happens on heavily stripped Windows builds). Install .NET Framework 4.8 from Microsoft
and retry.

**`[ERROR] Build failed`.** Usually the app is already running and holding its own `.exe`.
Close it — including from the tray (right-click the icon → Exit) — and build again.

**No window appeared after launch.** The app is probably already running: it is
single-instance and just brings the existing window to front. Check the tray by the clock.

**Won't build from a very long path.** Move the project closer to the drive root, e.g.
`C:\Tools\cleaner`.

### How to remove it

1. In settings, uncheck **Start with Windows** — the `WindowsProcessCleaner` task is
   removed from Task Scheduler.
2. Close the program (tray → Exit).
3. Delete the project folder and the settings folder `%APPDATA%\WindowsProcessCleaner\`.

Nothing else is left behind: the app doesn't register itself in the registry and doesn't
create services.

---

## Features

### Scanning
For each process it collects: name, PID, PPID, path, uptime, CPU %, RAM, whether
it has a window, listening TCP ports, whether it has child processes, and owner.

### "Abandoned" criteria
A process becomes a termination candidate only when **all** conditions hold:
1. the parent process has exited;
2. CPU below the threshold for longer than the idle time;
3. no user windows;
4. not listening on TCP ports;
5. no child processes;
6. not in the whitelist and older than the minimum lifetime.

### Two scan modes
- **Dev mode** (default) — only processes from the watchlist (node, python, java,
  vite, webpack, npm, pnpm, yarn, bun, cargo, go, deno, ruby, php…).
- **Global mode** (the "All processes (global)" checkbox) — scans **every**
  process. Here **strengthened guards** apply: only processes owned by the **current
  user**, **not** in Windows system folders and **not** in `Program Files` (installed
  software, configurable), idle for **≥ 30 minutes** (configurable), and not in the
  expanded protected list (Windows core, shell, clouds, messengers, drivers, launchers,
  password managers). This is how orphaned groups are caught — those whose parent died
  long ago while the children keep hanging around, burning CPU/RAM while being used by
  nothing.

### Cleanup buttons
- **Clean selected** — terminate the checked rows.
- **Auto-clean all inactive** — terminate every found candidate in one click
  (with a confirmation and a list).
- **Select all / Clear selection** — checkbox control.
- **Purge memory** — Standby Memory purge only, without terminating processes.

Termination itself: first gracefully (WM_CLOSE), wait up to 3 seconds, then force.

### Dev Cleanup
Bulk termination by group: all Node / Python / Java / Vite / Webpack / npm / pnpm /
yarn·bun / Docker Compose / Go·Cargo·Deno. Plus a list of processes holding popular
dev ports (3000, 5173, 8080, 4200 …) that you can terminate.

> Dev Cleanup terminates **by name regardless of activity** (except the whitelist) —
> a deliberate sledgehammer.

### Disk cleanup (manual only)
A dedicated tab. Flow: **Analyze → preview with sizes → delete selected**. There is
intentionally no automatic disk cleanup (files are less reversible than processes).
Only **known junk paths** are cleaned — no disk-wide duplicate search. Categories:
- **Dev caches** — npm / pnpm / yarn / pip / gradle / cargo / go / NuGet (regenerated).
- **System junk** — `%TEMP%`, `Windows\Temp`, Recycle Bin, Windows Update cache,
  crash dumps, error reports.
- **Browser caches** — Chrome / Edge / Brave / Firefox, cache only (no passwords/cookies).
- **App caches** — Discord / Slack / Teams / Spotify, cache only.
- **Old logs** — CBS/DISM logs, npm/yarn, install reports.
- **Old drivers + Windows.old** — NVIDIA/AMD installer leftovers, old Windows folder.

Guards: locked files and reparse points (junctions) are skipped; `DriverStore`,
System32, drive roots, code and projects are never touched. Files modified within the last
N minutes are kept (N is configurable, 10 by default). Every cleanup is written to
`clean-YYYY-MM.log` — the "Log" button opens it.

**winapp2 rules (optional).** The "winapp2 rules" button downloads
[Winapp2.ini](https://github.com/MoscaDotTo/Winapp2) — an open database with thousands of
cleanup rules for specific programs — and adds them to the built-in categories. Only
**file-deletion rules** are taken from it: sections carrying a warning (`Warning=`) or
exclusions (`ExcludeKey`) are skipped whole, and registry rules are ignored — this app never
cleans the registry. winapp2 results are never checked automatically.

### Programs (uninstall)
A tab listing installed programs (name, version, publisher, size). Check and uninstall
them through the app — the program's own uninstaller is launched.

### Program updates
This tab finds outdated programs across the machine and updates them. Flow:
**Check for updates → tick what you want → Update selected**.

**Where the data comes from.** Package managers are queried: **winget** (Microsoft's
official catalogue, tens of thousands of packages) and, when installed, **Chocolatey**.
Matching an installed program to a package and comparing versions is done by the manager
itself — the app keeps no version database of its own, which would inevitably fall behind
reality.

**The "Impact" column** shows the size of the version jump:

| Value | When |
|---|---|
| **major** | the leading version part changes (14.1.3 → 15.1.1), behaviour may change |
| **minor** | new features, usually still compatible (2.5.1 → 2.7.3) |
| **patch** | fixes and build tweaks (3.14.2 → 3.14.7) |
| **unknown** | the manager doesn't report the exact installed version |

Major updates are listed first and highlighted, so you never have to scroll to find them.
For `0.x` versions a change in the second part also counts as major — under semver that's
exactly where breaking changes land in such projects.

> ⚠️ **"Impact" is the size of the version change, not a security rating.** Neither winget
> nor Chocolatey exposes CVE or severity data, so real criticality cannot be derived from
> them, and the app does not pretend otherwise. If you need to know whether an update
> closes a vulnerability, read the program's own release notes.

**Batch updating.** Ticked packages are handed to the manager in groups rather than one by
one (5 per command by default, configurable 1..20; 1 means strictly one at a time). That
saves one process start and one source-index load per package.

The installers still run **sequentially**, and that's not a limitation of this app: Windows
Installer holds the machine-wide `_MSIExecute` mutex and Chocolatey takes its own global
lock, so two installers launched at once simply fail.

**How the result is determined.** After each group the app re-asks the manager what is
still outdated and diffs the list. That makes the per-package result correct regardless of
system language — parsing the localized output text would be guesswork.

**Also on this tab:**

- **Duplicates.** The same software can be visible through both winget and Chocolatey. Such
  rows are marked and dimmed, and the "All" button skips them — there's no point updating
  the same thing with two managers. The mark is advisory only: you can still tick the row
  manually.
- **"Never offer"** — adds the selected packages to the exclusion list (settings) so they
  stop showing up in future checks.
- **"Log"** — opens `updates-YYYY-MM.log`: what was updated, when, from which version to
  which, and with what result.
- **Nothing is pre-checked.** Updating is your call, so "Update selected" first shows the
  list of what will be updated, with versions, and asks for confirmation.
- Packages whose installed version the manager cannot determine are shown by default; this
  can be turned off in settings.

> An installer may close and restart the program it updates. Save your work before updating
> something that is open.

### Startup
A tab listing all installed programs with a checkbox toggle. Checked = the program is in
Windows startup; check to add, uncheck to remove. Reads and writes autostart from the
registry (`HKCU\...\Run`, `HKLM\...\Run`) and the Startup folders (user + common). Startup
entries that aren't in the installed-programs list (scripts, shortcuts) are marked orange
and can be disabled too. Adding always goes to the per-user `HKCU\...\Run`; by default, if
a program isn't in startup, the toggle is off.

### Docker
A tab for Docker cleanup (requires the Docker CLI + a running daemon). Buttons: disk
usage overview (`docker system df`), remove stopped containers, unused images, unused
volumes, clear build cache, full cleanup of everything unused. Only **unused** data is
removed (`prune`) — running containers and used images are never touched.

> ⚠️ **Important about disk space.** Docker Desktop stores everything in one growing
> WSL2 virtual disk (`docker_data.vhdx`). `prune` frees space **inside** that disk, but
> the file on Windows **doesn't shrink**. To actually reclaim Windows disk space, use
> the **"Compact Docker disk"** button: it stops Docker (`wsl --shutdown`) and compacts
> the vhdx via `diskpart compact vdisk`, showing size before/after. All running
> containers are stopped in the process.

Kubernetes is not included (its cleanup affects a live cluster).

### Interface language
Russian / English — switchable in settings (applied after restart). The documentation
is bilingual too: [README.md](README.md) / [README.en.md](README.en.md).

### Auto-clean timer
Set a number of hours (1..24). Every N hours the app scans **processes** (in the chosen
mode), terminates candidates, purges memory and writes to history. Disk cleanup and
uninstallation are **never** run by the timer — manual only.

### System tray
The tray icon changes color: green — clean, orange — candidates found. Double-click
opens the window. Right-click menu: Scan, Clean, Purge Standby Memory, toggle
auto-clean, restart as administrator, exit. Closing the window minimizes the app to
tray (it keeps running in the background).

### Start with Windows
A checkbox in settings. Implemented via **Task Scheduler** with highest privileges
(`schtasks /RL HIGHEST`), so there is no UAC prompt at logon.

### Themes
Light / dark / **system** (default — follows the Windows theme). Switchable in
settings, applied immediately, including the window title bar.

### Settings (saved)

- **Abandonment criteria** — CPU threshold, idle time, minimum lifetime, idle time for
  global mode.
- **Automation** — process auto-clean interval (1..24 h), auto-clean on/off, "global: don't
  touch Program Files", start with Windows, start minimized to tray.
- **Performance** — background CPU monitoring and its period (5..300 s, 15 by default),
  emptying the working sets of all processes (off by default — it slows the system down).
- **Disk cleanup** — "keep files newer than N minutes", cleanup logging, list of paths to
  never clean.
- **Program updates** — show packages with an unknown installed version, query Chocolatey,
  how many packages to hand the manager per command (1..20), list of packages to never
  offer for update.
- **Lists** — watchlist, whitelist, dev ports.
- **Look & feel** — theme and UI language.

### History
After each cleanup it saves date/time, number of terminated processes, amount of
freed memory and the list of processes.

---

## Is it safe?

**The scan / auto-clean mode — yes.** A candidate is picked only when **all**
criteria match at once, so an active process (with a window, listening on a port,
using CPU, or with a live parent) will never be selected. Global mode additionally
protects system processes and other users' processes. Termination always asks for
confirmation.

**Dev Cleanup — no**, it is intentionally a sledgehammer: the buttons hit everything
by name (except the whitelist). Use consciously.

---

## Where data lives

Everything in one folder — `%APPDATA%\WindowsProcessCleaner\`. The "Data folder" button in
settings opens it.

| File | What it is |
|---|---|
| `config.json` | all settings |
| `history.json` | process-cleanup history |
| `clean-YYYY-MM.log` | disk-cleanup log (what was deleted and how much was freed) |
| `updates-YYYY-MM.log` | program-update log |
| `winapp2.ini` | the downloaded winapp2 rule database, if you fetched it |

---

## Administrator rights

The app always runs as administrator. This is required to purge Standby Memory via
`ntdll!NtSetSystemInformation`. Terminating the current user's processes also works.

---

## Technical notes

- **WinAPI:** `CreateToolhelp32Snapshot`, `EnumWindows`, `GetExtendedTcpTable`,
  `OpenProcessToken`/`GetTokenInformation` (process owner),
  `SendMessageTimeout(WM_CLOSE)`, `TerminateProcess` (via `Process.Kill`),
  `NtSetSystemInformation` (Standby Memory), `GlobalMemoryStatusEx`,
  `DwmSetWindowAttribute` (dark title bar).
- **Program updates:** `winget.exe` and `choco.exe` are invoked. `winget upgrade` has no
  machine-readable output (a table only), so the table is parsed by the column start
  positions taken from the header line and mapped by their **order**, not by header names —
  that way parsing also works on a localized winget.
- **Single-instance** — a named `Mutex` (`Local\WindowsProcessCleaner.singleinstance`). The
  local TCP port **49876** is only used to ask an already running instance to show its
  window; if the port is taken, the app still starts normally.
- **Monitoring** runs on a background thread (not the UI one) with a configurable period
  (5..300 s, 15 by default). Process data is read directly via `OpenProcess` +
  `GetProcessTimes`/`GetProcessMemoryInfo` rather than `Process.GetProcessById` — the latter
  takes a full system snapshot per call, and on 600+ processes a single pass took tens of
  seconds.
- Consequently "5 minutes idle" is counted from the moment the app started observing the
  process, not from when it launched.
- **Command-line switches:** `/tray` — start minimized, `/auto` — run a process auto-clean
  and exit, `/analyze` — measure disk junk without deleting anything.

## Project files

| File | Purpose |
|------|---------|
| `ProcessCleaner.cs` | the entire application code |
| `app.manifest` | manifest (requireAdministrator, DPI) |
| `icon.ico` | application icon (embedded into the exe) |
| `build.bat` | build via the built-in csc.exe |
| `run.bat` | build if needed and run |
| `README.md` / `README.en.md` | documentation (RU / EN) |
