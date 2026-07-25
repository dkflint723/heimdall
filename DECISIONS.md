# Rove — decisions

`Rove` is a placeholder codename. Rename the namespace before it spreads.

A cross-platform file manager for Windows and Fedora KDE. Personal daily driver,
not a shell replacement. This file is the record of what was decided and why;
update it when a decision changes rather than relying on memory.

---

## Stack

| Choice | Decision | Why |
|---|---|---|
| Language | C# / .NET 10 | Already fluent in it; `IAsyncEnumerable` fits streaming enumeration exactly |
| UI | Avalonia 12.1+ | Only mature cross-platform .NET UI. 12.1 is the floor — native Wayland |
| MVVM | CommunityToolkit.Mvvm | Source-generated, AOT-safe. ReactiveUI is reflection-heavy and fights trimming |
| Lists & tree | ListBox (pane) + TreeView (sidebar) | TreeDataGrid rejected — see below |
| Publish | NativeAOT + trimming, from day one | Startup and footprint. Retrofitting AOT later is misery |
| Bindings | Compiled (`x:CompileBindings="True"`) | Required by AOT; good discipline regardless |
| Windows interop | Vanara.Windows.Shell, CsWin32 for gaps | Wraps IShellItem / IContextMenu / IFileOperation already |
| Linux interop | Tmds.DBus.Protocol | udisks2, Baloo, notifications |
| Serialization | System.Text.Json, source-generated | Reflection-based JSON breaks under AOT |

### Why not TreeDataGrid

Initially chosen, then rejected on two counts.

Licensing: since 11.2.0 it requires an Avalonia Accelerate licence. Dual-licensed
AGPL-3 for open source, paid otherwise (~EUR150/yr/seat). The free Community
Licence covers the tooling, not framework components. Pulls in
`AvaloniaUI.Licensing` and wants an `<AvaloniaUILicenseKey>` in the executable.

More importantly it was the wrong control anyway. TreeDataGrid exists for views
needing hierarchy *and* columns simultaneously. The file pane is flat with
columns; the sidebar tree is hierarchical without them. Neither needs it.

Instead: `ListBox` with a `Grid` item template and shared column widths for the
pane, `TreeView` for the sidebar tree. Both MIT, both virtualized, and both give
far more control over dense custom rows. Cost is writing column resize, reorder
and sort plumbing ourselves — bounded, roughly a week, and we wanted custom
chrome regardless.

### Window chrome

Draw our own titlebar on **both** platforms. KWin supports `xdg-decoration`, so
server-side decorations are available on Plasma — this is a choice, not a
constraint. Consistency wins over per-platform nativeness because the stated goal
is one interface that feels at home in both places.

### Look vs behaviour

Look is ours and identical everywhere: one type scale, one icon set, one shipped
font. Behaviour is platform-specific: hidden-file rules (dotfile vs attribute),
trash (IFileOperation vs XDG spec), roots (drive letters vs mount points),
accent colour (`kdeglobals` vs `UISettings`).

---

## Architecture

```
Rove.Core          platform-agnostic. No InteropServices reference, ever.
Rove.Ui            Avalonia. Depends only on Core.
Rove.Windows       IFileSystemProvider etc. via the broker.  net10.0-windows
Rove.Linux         same interfaces via syscalls + D-Bus.
Rove.Shell.Broker  separate .exe. All COM lives here.        net10.0-windows
```

Providers are resolved through DI at startup. `Rove.Ui` never names a platform
type. The Windows projects are conditionally referenced so `dotnet build` works
on Fedora:

```xml
<ProjectReference Include="..\Rove.Windows\Rove.Windows.csproj"
                  Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
```

### Why the broker is a separate process

Shell extensions are third-party COM DLLs that load in-process. A bad one from
some vendor's sync client can hang or crash the host. Isolating them means a
crash costs a restarted broker instead of the user's session. It also gives COM
a dedicated STA thread with a real message pump, which the UI thread should not
be providing.

Transport: named pipe, length-prefixed JSON, source-generated serializer.

### Context menus

Do **not** `TrackPopupMenu` — a Win32 menu inside an Avalonia window looks wrong.
The broker calls `QueryContextMenu` into an off-screen HMENU, walks it with
`GetMenuItemInfo` + `GetCommandString`, and returns a plain data tree. The UI
renders it in our own style and sends back a verb index for `InvokeCommand`.

Cost: owner-drawn extensions using `IContextMenu3::HandleMenuMsg2` lose custom
drawing. We get their text and icon, which is nearly always enough.

### File operations

Always `IFileOperation` on Windows. Never a hand-rolled copy loop — that is where
recycle bin semantics, collision prompts, UAC elevation and undo live. On Linux,
the XDG trash spec, and our own copy engine with the same progress contract.

---

## Performance rules

These are requirements, not optimisations. A 200k-file directory is the test case.

- Enumerate with `System.IO.Enumeration.FileSystemEnumerable<T>`, projecting
  straight from the native directory entry into `FileEntry`. No `FileInfo`
  allocation per file.
- Stream in batches of ~500 through `IAsyncEnumerable`. First screenful visible
  in milliseconds; never materialise before display.
- Never re-`stat` what enumeration already gave us. A `GetFileAttributes` per
  file is invisible locally and catastrophic over SMB.
- Thumbnails and overlay icons are viewport-driven only, cancelled on scroll,
  cached on disk by path+mtime+size.
- Every navigation carries a `CancellationToken` that fires when the user moves
  on. Nothing touching a remote path may block the UI thread — including the
  initial connect, which can take 30s against a dead host.

---

## Spike results (2026-07-25, Fedora KDE)

200,000 files in one flat directory, ListBox + VirtualizingStackPanel:

- **First paint: 3 ms.** Streaming architecture confirmed — the user never waits
  on enumeration, regardless of directory size.
- **Total: 3,394 ms** before tuning. Two causes, measured separately:
  - `stat` per entry ~1.2 s (`ls -f` 0.17 s vs `ls -l` 1.39 s). On Linux
    `readdir` returns name and type only; `FileSystemEntry.Length`,
    `.LastWriteTimeUtc` and `.Attributes` are lazy and each trigger a stat.
    On Windows these come free inside `WIN32_FIND_DATA`. The "never re-stat"
    rule therefore does not mean the same thing on both platforms.
  - Dispatcher hops ~2 s. 500-item batches meant 400 UI thread round-trips,
    each triggering a Reset. Fixed by flushing on a 100 ms timer instead, which
    makes hop count independent of directory size.

**Lazy stat: deferred, not rejected.** The stat cost is ~6 us/file — 30 ms on a
5,000-file directory, which is what real directories look like. Making `Length`
and `LastWriteTime` viewport-driven would complicate every consumer to fix a
case only reachable synthetically. Revisit if a real directory ever hurts; the
fix is a capability flag on the provider plus lazy population, same shape as
thumbnails.

Also noted: `entry.ToFullPath()` allocates a full path string per entry, so 200k
entries hold 200k strings sharing a prefix. Storing the directory once per
listing would cut this substantially. Same reasoning — deferred.

---

## Session model

The gripe that started this project was a file manager forgetting its open
folders. Treated as a first-class feature.

- **Save continuously**, not on exit. Debounce ~1s after any navigation, tab or
  layout change. Hook `SystemEvents.SessionEnding` for reboots.
- **Write atomically**: serialise to `.tmp`, flush, rename over. Keep the prior
  file as `.bak` and fall back if the primary won't parse. A corrupt session file
  must never prevent startup.
- **Restore lazily**: recreate every tab from its saved path immediately, but
  enumerate only the active tab. Background tabs load on first activation. One
  dead share must not cost 30 seconds of startup.
- **Dead paths stay visible**: keep the tab, show "couldn't reach this — retry",
  offer the nearest existing ancestor. Silently dropping a tab or redirecting to
  home is what "it forgot" feels like.

Persisted: per tab — path, scroll, selection, sort, view mode, column widths,
back/forward history. Per pane — tab list, active index. Per window — position,
monitor, maximised, split ratio, active sidebar panel, panel width, rail state.

---

## Sidebar

Rail + switchable panel (VS Code activity bar). The rail switches what the panel
shows; each panel gets full height instead of competing for it.

Ship four: places, tree, recents, search. Deferred but requiring no redesign:
preview, git status, transfer queue. Settings is a dialog, not a panel.

A panel earns a rail slot only if it is a place you navigate *from* or a result
set that should survive navigation. Everything else is a palette command.

`Ctrl+B` cycles full → rail only → hidden. `Ctrl+1..4` jumps to a panel; the same
key again focuses it. **Escape always returns focus to the active pane** — a
panel that traps focus makes the whole app feel like it's fighting you.

Import existing places on first run: `~/.local/share/user-places.xbel` (Dolphin),
`~/.config/gtk-3.0/bookmarks` (GTK), Quick Access on Windows.

---

## Search

One `ISearchProvider`, two backends that already exist — Everything's IPC on
Windows, Baloo over D-Bus on Fedora KDE. Do not build an indexer. Revisit an
MFT/USN reader only if Everything proves insufficient.

Results live in a panel, not a modal, so they survive navigation and can stream
in as the index answers.

---

## Remote

Mount-based. `kio-fuse` is usually present on Fedora KDE and exposes `sftp://`
and `smb://` as real paths; check for it before writing any SFTP support. On
Windows, mapped drives and UNC paths. No protocol implementations in v1.

---

## Scope

**v1 — the bar for daily driving.** Navigation with history, editable path bar,
multi-select, type-ahead, copy/move/delete/rename with progress and undo, drag
and drop, native context menus, hidden-file toggle, sort and columns, tabs,
split view, session restore, sidebar with places, free space, open-with,
thumbnails, filter bar, search panel, command palette.

**v1.1.** Preview panel, archive browsing, batch rename, folder sizes on demand,
recents panel, tree panel.

**v2 — the reasons this exists rather than being a Dolphin clone.** Transfer
queue with pause/resume/reorder. Git status inline. Embedded terminal. Dual-pane
compare and sync. Space-usage treemap panel. Portable tags that work on both
machines. Named workspaces.

---

## Build order

1. Core contracts + broker handshake
2. Enumeration + TreeDataGrid, tested against 200k files before anything else
3. Navigation, panes, tabs, keymap, command palette
4. Session persistence
5. `IFileOperation` + Linux copy engine
6. Thumbnails
7. Context menus
8. Sidebar rail + places panel
9. Search panel

Set up the Fedora build on day one, not once it works on Windows. Publish AOT
early and often — reflection-heavy dependencies break trimming in ways that only
appear at runtime in a published build. Keep the heavyweight Windows packages
inside the broker, where binary size costs nothing on Fedora.

---

## Licensing

Read Dolphin (GPL-2.0+) for *behaviour* — keyboard maps, what the filter bar does
on Escape, how places handles a drop. Do not port its code; that would make this
GPL the day it goes public.

Files (files-community/Files) is MIT and C#. Its shell interop is directly
portable with attribution. That is the reference implementation to lean on.

Check the licence on Avalonia's Wayland backend before distributing — the team
publicly floated AGPL dual-licensing for it. Irrelevant for personal use.

---

## QA pass (2026-07-25)

### The "duplicating tabs" investigation

Not a code bug. Multiple concurrent `dotnet run` instances, each restoring the
same session file and each writing back to it. Confirmed by `pgrep` after four
turns spent theorising about the view layer. **Check process count before
debugging state that lives in a shared file.**

Fixed structurally with a named mutex in `Program.cs` — two file managers
fighting over one session file is a real failure mode, not just a dev annoyance.
The current implementation simply exits; raising the existing window instead is
the proper behaviour and is still to do.

### Fixed in this pass

- Navigation history was **write-only** — `ToTabState` saved the stacks,
  `RestoreFrom` never read them. Restored tabs came back with a dead back button.
- `OnClosing` was `async void` without `e.Cancel`, so the process could exit with
  the flush still in flight. Now cancels, flushes, then closes for real.
- `Start` is idempotent. Restoring is once-per-process and must not rely on
  nobody calling it twice.
- `ShowHidden` now persists, set under suppression during restore so it doesn't
  trigger the reload that lazy restore exists to avoid.
- Window geometry persists. Session loads **synchronously** so size and position
  are applied before first paint — an async load restores them after the window
  is already on screen, which is a visible jump every launch.
- Sorting by kind compared `Extension.ToString()`, allocating a string per
  comparison — millions per sort at 200k entries. Now compares spans.
- `DisposeAsync` drains the write lock before disposing the semaphore; tearing it
  down mid-write threw into a swallowing catch and lost the final save.

### Schema trimmed

`TabState` and `WindowSession` declared `ScrollOffset`, `Selection`, `View`,
`ColumnWidths`, `MonitorId`, `SplitRatio` and the sidebar fields, none of which
were read or written. A session file that appears to store scroll position and
doesn't is worse than one that never claimed to. They return with the features
that own them. Schema version bumped to 2, so v1 files are ignored on load.

### Known and deliberate

- `IFileSystemProvider.Watch` and `IsReachableAsync` are implemented but unused.
  `IsReachableAsync` is the dead-path guard lazy restore should eventually use.
- Multi-select is enabled on the list but only `SelectedEntry` is wired.
- **NativeAOT has never been published.** `PublishAot` was a founding decision
  and remains unexercised. `dotnet publish -r linux-x64 -c Release
  /p:PublishAot=true` is the next thing worth running — it either works or
  reveals a trim problem while the codebase is still small.
