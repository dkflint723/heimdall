# Dolphin parity — gap analysis

Target: everything Dolphin does, plus the OneCommander features deliberately
adopted (Miller column strip, inline per-type metadata, priority columns).

Grounded in the Dolphin Handbook (docs.kde.org/stable_kf6/en/dolphin) and the KDE
UserBase pages for Dolphin/File_Management, not from memory.

**This file is the only authoritative record of what is and is not built.**
`HANDOFF.md` describes the project and deliberately carries no status, because
keeping the same list in two documents is exactly how both went stale.

Status verified against the tree, July 2026. Session schema v12.
Settings schema v1.

---

## At parity

| Dolphin | Heimdall |
|---|---|
| Tabs | ✅ per side, persisted |
| Split view | ✅ F3, each side independent |
| Breadcrumb + editable location (Ctrl+L) | ✅ double-click to edit, Tab completes, Enter goes, Escape or click-away cancels |
| Places panel | ✅ XDG dirs, mounts, Dolphin/GTK bookmark import |
| Folders (tree) panel | ✅ collapsible section |
| Filter bar | ✅ Ctrl+I |
| Search (Baloo) | ✅ with walk fallback |
| Thumbnails / previews | ✅ freedesktop cache, shared with Dolphin |
| Icon themes | ✅ full XDG theme resolution, SVG subset renderer |
| Copy / move / rename / trash | ✅ XDG trash spec |
| Undo | ✅ rename, move, trash |
| Drag and drop | ✅ internal and cross-app, move/copy modifiers |
| Open With (desktop database) | ✅ |
| Open terminal here | ✅ F4 |
| Properties + permissions editing | ✅ incl. recursive with chmod's X rule |
| Free space in status bar | ✅ |
| Zoom | ✅ text and icons on independent axes, per pane |
| Show hidden | ✅ |
| Tags | ✅ `user.xdg.tags`, the same xattr Dolphin and Baloo use |
| Colour scheme follows the desktop | ✅ live via `kdeglobals` |
| Service-menu equivalent | ✅ scripts menu (our design, same purpose) |
| **Compact view** | ✅ third layout, `WrapPanel Orientation="Vertical"` |
| **Sort by type** | ✅ `SortField.Kind`, reachable from the menu — there is no type column to click |
| **Natural sort** | ✅ `file2` before `file10` |
| **Grouping** | ✅ by name initial, size band, date band or type |
| **Duplicate** | ✅ copy alongside with a suffix |
| **New file from template** | ✅ reads `~/Templates` via `XdgTemplates` |
| **Copy To / Move To** | ✅ targets the sidebar Places |
| **Selection statistics** | ✅ `Summary` reports selected count and total size |
| **Miller keyboard navigation** | ✅ Left/Right between columns, auto-scroll on descent |
| **Information panel (F11)** | ✅ `InfoPanelViewModel`, **per split side** |
| **Batch rename** | ✅ live preview, in Core, all-or-nothing |
| **Network transparency** | ✅ both directions — see below |
| **Settings dialog** | ✅ six pages — see below |
| **Folders panel navigation** | ✅ — the tree listed and expanded but clicking did nothing until July 2026 |
| **Type-ahead** | ✅ — listed in v1 scope, never built until July 2026; nothing handled typed characters at all |

**Beyond Dolphin:** Miller column strip, per-type inline metadata, permissions as
a list column, priority-based column dropping, file-age shading, per-pane
independent font and icon scaling, folder sharing over the network.

### Grouping — how it works
Bands, not values. The group is applied as a **primary sort key**, otherwise
bands interleave. Headers live *inside the row template*, so `Entries` stays a
flat list of `FileEntry` and virtualization is unaffected.

### Information panel — how it works
Reuses `IPropertiesProvider`, so it can never disagree with the properties
window. Fills from the listing first, then refines asynchronously behind a
`_generation` guard. It lives on `PaneGroupViewModel`, **per split side, not per
window** — a single shared panel swapped content as focus moved between halves,
which defeats comparing two folders. Each side's tab bar has its own toggle; F11
acts on the focused side.

### Network — decided: consume, do not reimplement
Reimplementing KIO was never on the table. Both directions now work by consuming
machinery that already exists.

*Inbound.* `IRemoteMounts` / `LinuxRemoteMounts` reads gvfs and kio-fuse mount
points straight off the filesystem (`/run/user/$UID/gvfs`, `kio-fuse-*`), so
**every protocol the desktop supports works for the cost of a directory
listing**. `gio mount` connects; `gio mount -u` disconnects, and kio-fuse mounts
decline to unmount because KIO owns their lifetime. A dead mount is labelled
*offline* rather than listing as empty. `INetworkDiscovery` / `AvahiDiscovery`
browses `avahi-browse -prt` for webdav, smb, sftp, ftp and http, mapping each to
the right mount scheme. **mDNS finds Samba and macOS but not Windows**, which
uses WS-Discovery; `wsdd` would bridge that if it ever matters.

*Outbound.* `IFileSharing` / `CopypartyShare` drives
[copyparty](https://github.com/9001/copyparty) (MIT) as a **subprocess** to serve
a folder over HTTP/WebDAV. It is located, not bundled — PATH, then
`python3 -m copyparty`, then a downloaded sfx — and the feature hides itself when
none is found. Embedding a Python runtime would have cost the trimmed-AOT
single-binary story for a feature most sessions never use.

Read-only and read-write are **separate commands, not a toggle**, because the
difference is "people can look" versus "people can overwrite". Active shares stay
visible in the sidebar while running and are torn down on window close — a folder
open to the network must not be something you have to remember.

Confinement was wrong twice and the details matter: the volume is declared in a
generated **config file**, never `-v src:dst:perm` (that syntax is
colon-separated, so a folder named `notes:2026` parses into something else); the
volflags **`xvol` and `xdev`** stop symlinks leaving the folder and stop crossing
filesystems; the process CWD is **a temp directory, not the shared folder**,
because copyparty with no volume serves its CWD read-write and a config failure
would otherwise be quietly permissive; sharing `/` is refused. Share the
**right-clicked** folder, not `pane.CurrentPath` — that bug exposed every sibling
of the intended folder.

---

## Open gaps

### 1. Large folders in the tile layouts — HALF CLOSED, July 2026

**Answered and built for GRID; still open for COMPACT.**

A custom `VirtualizingWrapPanel` was written (the first option below). Measured
on 100,000 items: **grid renders in ~20 ms with 48 containers realized, constant
regardless of folder size**, against 6,841 ms for 20,000 un-virtualized. The
guard is now split — `CanUseGrid` is always true, and the drop-back to list view
fires for compact only.

**Compact is still the plain `WrapPanel`: 100,000 items takes ~32 seconds.** It
wraps *vertically into columns*, so closing it needs an orientation mode on the
panel — the row arithmetic becomes column arithmetic and `GetControl` swaps its
axes. Same shape of work, not a swap. **This is now the only place Heimdall
refuses something Dolphin does.**

`HEIMDALL_TILE_DEBUG=1` prints the realized count, index range and viewport on
every measure; the realized count is the ground truth for whether virtualization
is actually happening, and it is how grid was proved.

Two ways it could have been closed, for the record:

- a custom `VirtualizingPanel` that wraps — preserves ListBox selection and
  keyboard navigation, but hard to get right. **This is what was built.**
- chunk items into rows and virtualize the rows — easier, but breaks ListBox
  selection semantics.

**Unverified:** stale icons on recycled tiles under hard scrolling at 100k. A
container count cannot reveal it.

### 2. Newly identified — found while building the settings dialog

Neither of these was on any list before July 2026. Both were discovered by
asking what a Dolphin setting would actually control here and finding nothing.

- **No multi-window support at all.** `App.axaml.cs` creates exactly one
  `MainWindow`. Dolphin opens as many as you like, and "Open in new window" is a
  standard context-menu entry. This is why that entry is absent rather than
  merely hidden.
- **The context menu was missing five of Dolphin's nine standard entries.**
  Three have since been added — Open in new tab, Add to places, Copy location.
  Two remain: Open in new window (needs the above) and View mode (lives in the
  toolbar and view flyout here, which is arguably the better place).

### 3. Medium — days each

- **Per-folder view properties.** Dolphin remembers view mode, sort and zoom per
  directory (`.directory` files or a central store). Also the OneCommander
  behaviour deferred earlier. Needs a decision on where it is stored, and it
  changes the session schema. **Its General → Behavior toggle is written and
  waiting**; the setting ships with the feature.
- **Configurable shortcuts and toolbar.** Implies a settings surface and a command
  registry, neither of which exists.
- **Checksum tab in properties** (MD5/SHA1/SHA256). Easy computation; wants
  progress and cancellation on large files.
- **Version control decorations.** Git status per row. Dolphin does this through
  plugins; here it means running `git status --porcelain` per repository and
  caching. Rows already support per-entry async decoration.
- **Selection mode.** Dolphin's touch-friendly checkbox selection.

### 4. Large — need a decision first

- **Terminal panel** (F4 docked Konsole). Dolphin embeds Konsole via KParts, which
  has no equivalent here. Doing it properly means running a PTY and writing a
  terminal emulator — a project in itself. **Recommended against**; F4 already
  opens a terminal in the current folder.
- **Archive browsing as folders.** Dolphin enters `.zip` and `.tar.gz` as if they
  were directories. Archives were built once and dropped at the author's request;
  this would revive that decision in a different form. Do not start it unbidden.

---

## Settings dialog (done)

Six pages against Dolphin's seven: **General** (Behaviour, Previews,
Confirmations, Status bar), **Startup**, **View modes**, **Navigation**,
**Context menu**, **Trash**. `settings.json` lives beside `session.json` and is
read *first*, because the startup setting decides whether the session is
consulted at all.

**Dropped from Dolphin's set, with reasons:** *User Feedback* is KDE telemetry
and this application sends nothing anywhere; *Open archives as folder* would
reopen the archives decision by the back door; *service menus* are replaced by
the scripts menu; *Show zoom slider* has no slider to toggle.

**The governing rule is that a setting ships WITH its feature, never before it.**
A dialog full of toggles that do nothing is worse than no dialog. Written into
the model but deliberately not shown until their feature exists: per-folder view
style, expandable folders, selection markers, VCS decorations, spring-loaded
folders, previews inside folder icons, folder size by contents, inline-vs-dialog
rename, the executable-open choice, full path in the location bar, and opening
external folders in tabs.

---

## Recent Files and Recent Locations (done, July 2026)

Dolphin's two `recentlyused:/` entries, reproduced. **Deliberate divergence: the
data is Heimdall's own, not KDE's.** `ldd` on Dolphin's `recentlyused.so` shows
both its lists come from the KActivities database — consuming it would have meant
bundling SQLite into a trimmed NativeAOT binary against a schema that is not a
public API, and contributing to it would have meant D-Bus. The presentation was
what mattered, so `IRecentStore` / `JsonRecentStore` records opens in
`recents.json` instead: files and folders separately, trimmed by time, bounded at
200 each.

**Recorded on user-initiated opens only** — `NavigateAsync` under its existing
`IsLoaded` guard (so back, forward, refresh and session restore are excluded for
free), plus `OpenAsync` and `OpenWith`, which are the same act.

**The listings are virtual paths** — `heimdall:recent-files` and
`heimdall:recent-locations` — that `LoadListingAsync` branches on. The listing
source returns the same `IAsyncEnumerable` shape as the filesystem provider, so
sorting, filtering, grouping, all three layouts and selection work unchanged. Six
places had to learn that a path may not be on disk: the Miller strip, the file
watcher, the breadcrumb, the visit recorder, the tab title, and the column set.

Entries carry their **access** time in `LastWriteTime`, which is what gives
Today/Yesterday banding and the timestamp column for free. That field means
something different in these two listings than anywhere else, and it is commented
where it is set.

A **Path column** appears only here, sharing its grid column with the metadata
column rather than adding a seventh. Without it a bare filename identifies
nothing, since entries span the whole filesystem.

**Forget** on the context menu drops the record and never the file, mirroring
`ITagStore.ForgetKnown`.

**Superseded:** the frequent-folders list and its visit store were removed at the
user's request once recency existed. Two ranked lists of folders in one sidebar
is one too many.

## Deliberately not doing

- **Service menus** (`.desktop` files in `servicemenus/`) — the scripts menu covers
  the same need with less ceremony. Revisit only if interoperability with existing
  KDE service menus is wanted.
- **Baloo ratings and comments** — a Baloo concept with little value outside it.
  Tags are already shared.
- **Konqueror-style embedded viewers.**

---

## Proposed order

1. ~~Settings dialog.~~ **Done, July 2026.**
2. ~~Tile-layout virtualization.~~ **Grid done, July 2026.**
3. ~~Per-folder view properties.~~ ~~Checksums.~~ ~~Type-ahead.~~ **All done.**
4. ~~Recent Files and Recent Locations.~~ **Done, July 2026** — Heimdall's own
   recency store, not KDE's; see below.
5. **Virtualize compact** — an orientation mode on `VirtualizingWrapPanel`. The
   last place this application refuses something Dolphin does, and therefore the
   only remaining violation of the 100% parity goal.
6. **A Trash place in the sidebar.** Dolphin has one and Heimdall has none —
   `LinuxPlacesProvider` emits no `trash` token, though the icon is drawn. It
   needs restore and empty to be honest, so it is its own piece of work rather
   than a one-line place. `XdgTrash` and `XdgTrashMaintenance` already exist.
7. Version control decorations, then selection mode and configurable shortcuts.
8. Multi-window, which also unlocks the last context-menu entry.
9. Terminal panel last, or never.

The Windows port sits outside this order and was deferred explicitly. It is not a
parity item; it is the point at which twenty single-implementation interfaces get
tested.
