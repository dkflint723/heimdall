# Rove — handoff

A file manager for Fedora KDE and Windows, built to be daily-driven instead of
Dolphin and Explorer. This document is what you read to pick the project up
cold. `DECISIONS.md` has the rationale behind individual choices; `PARITY.md`
is the working gap list against Dolphin.

---

## 1. What this is, and what it is not

**Goal:** a lightweight, powerful file manager that feels at home on both KDE
and Windows, used as a daily driver by its author and a few friends. Not
commercial, not a shell replacement.

**The problems it exists to solve**, in the author's words: Explorer is slow on
huge and network folders, has no real tabs or dual-pane or keyboard navigation,
useless search, and is bad at SMB and remote shares. OneCommander was closer but
forgets open folders on restart and feels clunky.

**Binding constraint — accessibility.** This is a requirement, not polish:

- Text must be resizable. Fonts and icons scale on **independent** axes.
- The UI must work for colourblind users. **Hue never carries meaning on its
  own.** Selection has an edge bar as well as a fill; tags carry their name
  beside the swatch; file age is a lightness ramp, never a red-to-green heat
  map.

That rule paid for itself: because nothing depended on a particular hue, the
entire palette could later be handed over to the desktop's colour scheme without
weakening anything.

**Non-goals:** shell replacement, mobile, plugin marketplace, cloud sync.

---

## 2. Build and run

```bash
cd ~/dev/rove
dotnet build && dotnet run --project src/Rove.Ui

# Release, ahead-of-time, fully trimmed — the shape that actually ships
dotnet publish src/Rove.Ui -r linux-x64 -c Release /p:PublishAot=true
```

Fedora's own .NET 10 SDK, Avalonia 12.1. `Directory.Build.props` enables the
trim and AOT analysers in every project, so trim-hostile code fails the build
rather than surfacing months later in a published binary.

Diagnostics are on stderr and prefixed `[rove]`:

```bash
dotnet run --project src/Rove.Ui 2>&1 | grep -a rove
ROVE_ICON_DEBUG=1 dotnet run --project src/Rove.Ui 2>&1 | grep -a icon
```

State lives in `~/.local/state/rove/` — `session.json` (+ `.bak`),
`places.json`, `tags.json`, `instance.lock`. Scripts live in
`$XDG_DATA_HOME/rove/scripts`.

---

## 3. Architecture

```
Rove.Core     platform-agnostic. Never references InteropServices.
Rove.Linux    [assembly: SupportedOSPlatform("linux")]
Rove.Ui       Avalonia. Depends on Core; names a platform type in ONE place.
Rove.Windows  not started
```

**The platform seam is a single object.** `IPlatform` in Core bundles every
OS-specific provider — filesystem, operations, launcher, places, search,
thumbnails, metadata, properties, access editor, scripts, tags, theme, icons.
`LinuxPlatform` is the Linux composition root. `MainWindow` constructs it inside
one `OperatingSystem.IsLinux()` check and never mentions a platform type again.

For the Windows port: add `WindowsPlatform`, make the `Rove.Windows`
ProjectReference conditional, and put `#if` around that single construction. The
UI needs no other change.

`Rove.Linux` is annotated Linux-only at assembly level rather than by target
framework — **there is no `net10.0-linux` TFM**; .NET only defines OS-specific
frameworks for windows, android, ios, macos, maccatalyst, tvos and browser. The
annotation removes every per-call platform guard inside the project and pushes
the requirement onto callers, which is what forced the single-seam design. It
immediately caught a real leak: the shell had been casting to a concrete Linux
type to reach an event, now on the Core interface where it belongs.

### Patterns that recur

**Per-row work goes through attached properties on the realized control.**
Thumbnails, themed icons, inline metadata, permissions and tags all attach to
the control the list virtualization actually creates, so only visible rows pay.
`FileEntry` must stay stat-free — never widen it to carry per-row data, or
enumeration stops being fast.

**Streaming enumeration.** `System.IO.Enumeration.FileSystemEnumerable`, channel
batched, flushed to the dispatcher on a 100 ms timer. A 200,000-entry flat
directory paints its first rows in **3 ms** and completes in about 3.4 s.

**Generation counters, not just cancellation tokens.** State captured before an
`await` cannot be trusted after it. Any async handler that mutates a bound
collection re-checks a generation counter *inside* the dispatcher block.

**Derived metrics.** Every size is a `DynamicResource` computed from the font
and icon scales. Row height is `max(body × 2.1, thumb + 8)` — it cannot be a
free setting, because a row must fit the taller of its label and its icon.

---

## 4. What is built

### Navigation and layout
Tabs (per side, persisted) · split view (F3, each side independent, remembers
its folder when closed) · breadcrumb with clickable ancestors and an editable
box behind it (Ctrl+L) · back/forward/up history · Miller column strip above
either layout (F8), with Left/Right stepping and auto-scroll · list and grid
layouts, per tab · priority column dropping as a pane narrows · filter bar
(Ctrl+I) · sidebar with search, places, tags, devices and a collapsible folder
tree, all visible at once.

### Files
Copy, move, rename, trash (XDG spec), permanent delete with a confirmation that
has real buttons · undo for rename, move and trash · batch rename with live
preview · drag and drop, internal and cross-application · clipboard interop with
Dolphin · Open With from the desktop database · properties with permission
editing · scripts menu · tags in `user.xdg.tags`, the same extended attribute
Dolphin and Baloo use.

### Presentation
Thumbnails from the freedesktop cache Dolphin already fills · full XDG icon
theme support with a hand-written SVG subset renderer and no extra dependency ·
colours, fonts and accent read live from `kdeglobals` · file-age lightness
shading · inline per-type metadata (image dimensions, folder counts) ·
permissions as a column · independent font and icon scaling · Space preview.

### Robustness
Crash-safe session (survives `kill -9`; atomic temp-and-rename with a backup) ·
single instance via an exclusive file lock — .NET's named `Mutex` does not work
for this on Fedora · operation failures surfaced rather than swallowed ·
`AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`
logged so an abort prints a stack.

---

## 5. Constraints that will bite again

These are all things that cost real time. They are not hypothetical.

- **Compiled bindings are on** (Avalonia 12 default, now stated explicitly
  because AOT requires them). Every `DataTemplate` needs `x:DataType`, and
  **`Style` setters need it on the `Style` element** or they resolve against the
  window's type. Bindings inside a `ContextMenu` are built lazily — a missing
  command compiles and throws only when the menu first opens.
- **`DrawingGroup.ClipGeometry` does not affect `GetBounds()`** in Avalonia
  (issue #18512, deliberately unlike WPF), and `DrawingImage.Size` is exactly
  `Drawing.GetBounds().Size`. A clip can never correct an image's size.
- **Avalonia objects must be constructed on the UI thread.** Building a
  `DrawingImage` or reading `Application.Current.Resources` from a pool thread
  crashed the process. `Bitmap` is a plain object and is the exception.
- **`DoDragDropAsync` takes `PointerPressedEventArgs` specifically.** Holding the
  press args until a movement threshold is crossed looks wrong but is forced by
  the API. Do not "fix" it.
- **X11 clipboard is owned by a live process.** Copy, quit, paste is not
  achievable. Do not chase it.
- **`/proc/mounts` needs filtering** — exclude loop, zram and squashfs or snap
  mounts appear as drives named after revision numbers; dedupe by device or
  btrfs subvolumes appear repeatedly.
- **Never insert into `MainWindow.axaml` by matching a binding string.** The
  Miller column template binds `{Binding Entries}` exactly like the pane's list.
  Locate the enclosing `x:DataType` first.
- **`pgrep` before debugging anything backed by a shared file.** A "tabs
  duplicating" bug was two `dotnet run` instances.
- **TreeDataGrid is rejected** — needs an Avalonia Accelerate licence, and was
  the wrong control anyway.

---

## 6. In flight

- **Verify the NativeAOT publish.** It worked on day one and much has landed
  since — `LibraryImport` P/Invoke, LINQ-to-XML SVG parsing, many converters.
  Building clean is not enough; run the published binary and exercise icons,
  tags, search and properties, because trimmed-away types surface at first use.
- **One icon still renders small.** Tela's `application-x-compressed-tar.svg`
  declares a 16×16 viewBox and paints 48×56. `ROVE_ICON_DEBUG=1` dumps each
  shape's bounds and paint; the hypothesis is a shape spanning the full area
  without being visible, inflating the bounds.

---

## 7. What is planned

`PARITY.md` is the list. Summarised:

**Small** — compact view, sort by type, natural sort, grouping, duplicate, new
file from templates, copy-to-places, selection statistics.

**Medium** — information panel, per-folder view properties, settings dialog,
configurable shortcuts, checksums in properties, version control decorations,
selection mode.

**Large, and needing decisions** —

1. **Network transparency.** The choice that sets the scope of the whole
   project. Dolphin browses `sftp://`, `smb://`, `mtp://` through KIO workers,
   which cannot sanely be reimplemented. But **kio-fuse** exposes KIO URLs as
   real paths under `/run/user/$UID/kio-fuse-*`, and gvfs does the same under
   `/run/user/$UID/gvfs` — so consuming mounts instead of implementing protocols
   turns months into days, at the cost of depending on a mount helper being
   installed. **Undecided.**
2. **Terminal panel.** Dolphin embeds Konsole via KParts. An equivalent means
   writing a terminal emulator. Recommended against; F4 already opens a terminal
   in the current folder.
3. **Archive browsing as folders.** Would revive the archives decision, which
   was built once and dropped at the author's request.

**Then the Windows port**, which remains the largest unvalidated assumption in
the project. Every Core interface has exactly one implementation, and the second
is where you find out which of those shapes were really about Linux.

---

## 8. Working practices

- **Ship a full `src` snapshot, not incremental patches.** Partial archives
  repeatedly failed to land — files listed by `tar` yet unchanged on disk. This
  silently reverted a working fix once and caused several rounds of chasing a
  bug that was already fixed. Commit first, `pkill -f Rove.Ui`, extract, then
  `git status --short` to see what actually changed.
- **Verify before building.** `grep -c` for a symbol you just added is faster
  than a build cycle and catches a file that did not land.
- **Instrument before theorising.** Three separate bugs were chased with
  plausible-sounding guesses that were all wrong; each was resolved in one step
  once a diagnostic was added. When something is *invisible* rather than
  incorrect, add the trace first.
- **Prefer the desktop's own data over private equivalents.** XDG trash, the
  freedesktop thumbnail cache, `user.xdg.tags`, `kdeglobals`, shared-mime-info,
  the icon theme spec. Everything Rove writes, the rest of the desktop can read.
