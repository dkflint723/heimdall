# Dolphin parity — gap analysis

Target: everything Dolphin does, plus the OneCommander features deliberately
adopted (Miller column strip, inline per-type metadata, priority columns).

Grounded in the Dolphin Handbook (docs.kde.org/stable_kf6/en/dolphin) and the KDE
UserBase pages for Dolphin/File_Management, not from memory.

**This file is the only authoritative record of what is and is not built.**
`HANDOFF.md` describes the project and deliberately carries no status, because
keeping the same list in two documents is exactly how both went stale.

Status verified against the tree, July 2026. Session schema v12.

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

### 1. Large folders in the tile layouts — a genuine regression against Dolphin

The only place Heimdall currently *refuses* something Dolphin does. Grid and
compact use `WrapPanel` and **Avalonia has no virtualizing wrap panel**, so every
item they are given is realized. Above `UnvirtualizedLimit = 5000` the tile
layouts are disabled and navigating into a huge folder while already in one drops
back to list view. Refusing rather than truncating, because a file manager that
silently omits files is dangerous.

Dolphin handles 200k in icon view. Two ways to close it:

- a custom `VirtualizingPanel` that wraps — preserves ListBox selection and
  keyboard navigation, but hard to get right;
- chunk items into rows and virtualize the rows — easier, but breaks ListBox
  selection semantics.

**Undecided.** Worth answering first: does the guard actually bite in daily use,
or only in benchmarks? Grid and compact are picture-and-document layouts, and a
200k-entry folder viewed as tiles may not be a real workflow.

### 2. Medium — days each

- **Per-folder view properties.** Dolphin remembers view mode, sort and zoom per
  directory (`.directory` files or a central store). Also the OneCommander
  behaviour deferred earlier. Needs a decision on where it is stored, and it
  changes the session schema.
- **Settings dialog.** There is a per-pane view flyout and nothing else. Dolphin
  has startup, view modes, navigation, services, trash and general pages. This is
  the recommended next piece of work: the application is a daily driver with no
  way to change anything except by editing session JSON.
- **Configurable shortcuts and toolbar.** Implies a settings surface and a command
  registry, neither of which exists.
- **Checksum tab in properties** (MD5/SHA1/SHA256). Easy computation; wants
  progress and cancellation on large files.
- **Version control decorations.** Git status per row. Dolphin does this through
  plugins; here it means running `git status --porcelain` per repository and
  caching. Rows already support per-entry async decoration.
- **Selection mode.** Dolphin's touch-friendly checkbox selection.

### 3. Large — need a decision first

- **Terminal panel** (F4 docked Konsole). Dolphin embeds Konsole via KParts, which
  has no equivalent here. Doing it properly means running a PTY and writing a
  terminal emulator — a project in itself. **Recommended against**; F4 already
  opens a terminal in the current folder.
- **Archive browsing as folders.** Dolphin enters `.zip` and `.tar.gz` as if they
  were directories. Archives were built once and dropped at the author's request;
  this would revive that decision in a different form. Do not start it unbidden.

---

## Deliberately not doing

- **Service menus** (`.desktop` files in `servicemenus/`) — the scripts menu covers
  the same need with less ceremony. Revisit only if interoperability with existing
  KDE service menus is wanted.
- **Baloo ratings and comments** — a Baloo concept with little value outside it.
  Tags are already shared.
- **Konqueror-style embedded viewers.**

---

## Proposed order

1. **Settings dialog.** The largest gap between what the application can do and
   what its user can reach.
2. **Per-folder view properties.** Shares the session-schema change with anything
   else that persists per-directory state, so it belongs near the settings work.
3. **Decide the tile-layout virtualization question** — after establishing whether
   the 5,000 guard bites in practice.
4. Checksums, then version control decorations, then selection mode and
   configurable shortcuts.
5. Terminal panel last, or never.

The Windows port sits outside this order and was deferred explicitly. It is not a
parity item; it is the point at which twenty single-implementation interfaces get
tested.
