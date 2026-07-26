# Dolphin parity — gap analysis

Target: everything Dolphin does, plus the OneCommander features we deliberately
adopted (Miller column strip, inline per-type metadata, priority columns).

Grounded in the Dolphin Handbook (docs.kde.org/stable_kf6/en/dolphin) and the
KDE UserBase pages for Dolphin/File_Management, not from memory.

Status as of the Linux v1 build.

---

## Already at parity

| Dolphin | Heimdall |
|---|---|
| Tabs | ✅ per side, persisted |
| Split view | ✅ F3, each side independent |
| Breadcrumb + editable location (Ctrl+L) | ✅ |
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
| Zoom | ✅ text and icons on independent axes |
| Show hidden | ✅ |
| Tags | ✅ `user.xdg.tags`, same xattr Dolphin uses |
| Colour scheme follows the desktop | ✅ live via kdeglobals |
| Service-menu equivalent | ✅ scripts menu (our design, same purpose) |

Beyond Dolphin: Miller column strip, per-type inline metadata, permissions as a
list column, priority-based column dropping, file-age shading.

---

## Small gaps — hours each

1. **Compact view** — Dolphin's third view mode: name-only, wrapping into
   columns. We have Details and Grid; this is a third `ItemsPanel`.
2. **Sort by type**, and **natural sort** (`file2` before `file10`). We sort by
   name/size/modified only, ordinal.
3. **Grouping** — Dolphin groups the listing by name initial, size band, date
   band or type, with headers.
4. **Duplicate** (Ctrl+D in some setups) — copy alongside with a suffix.
5. **New file from template** — Dolphin reads `~/Templates`; we only create
   folders.
6. **Copy To / Move To** menus targeting Places, not just the other pane.
7. **Selection statistics** — Dolphin's status bar reports the selected count
   and total size; ours reports the folder only.
8. **Miller keyboard navigation** — left/right between columns, auto-scroll on
   descent. Flagged when built, never done.

## Medium gaps — days each

9. **Information panel** (F11) — metadata and a large preview of the selection,
   docked right. We have the Space overlay; this is the persistent version.
10. **Per-folder view properties** — Dolphin remembers view mode, sort and zoom
    per directory (`.directory` files or a central store). Also the OneCommander
    behaviour we deferred. Needs a decision on where it's stored.
11. **Batch rename** — the last of the four extras. Dolphin does numbered
    sequences and find/replace; we planned regex.
12. **Settings dialog** — we have a per-pane view flyout and nothing else.
    Dolphin has startup, view modes, navigation, services, trash and general
    pages.
13. **Configurable shortcuts and toolbar** — implies a settings surface and a
    command registry, which we don't have.
14. **Checksum tab** in properties (MD5/SHA1/SHA256) — easy computation, but
    wants progress and cancellation on large files.
15. **Version control decorations** — git status per row. Dolphin does this via
    plugins; for us it means running `git status --porcelain` per repo and
    caching. Rows already support per-entry async decoration.
16. **Selection mode** — Dolphin's touch-friendly checkbox selection.

## Large gaps — weeks, need a decision first

17. **Network transparency (KIO).** ✅ **DECIDED: consume, do not reimplement.** Dolphin browses
    `sftp://`, `smb://`, `ftp://`, `webdav://`, `mtp://`, `gphoto://` and
    `archive:` URLs natively through KIO workers, with credential handling and
    Places integration.

    Reimplementing KIO is out of the question. The realistic path is to consume
    mounts rather than protocols:

    - **kio-fuse** exposes KIO URLs on the real filesystem under
      `/run/user/$UID/kio-fuse-*`, so anything Dolphin can open becomes an
      ordinary path we can already enumerate.
    - **gvfs** does the same under `/run/user/$UID/gvfs` for GTK-side mounts.
    - A "Connect to server" action can ask the existing desktop machinery to
      mount, then navigate to the resulting local path.

    That gets SMB and SFTP — the two actually named — for a fraction of the
    cost, at the price of depending on a mount helper being installed.

    **The same principle applies to the outbound direction.** Heimdall now drives
    [copyparty](https://github.com/9001/copyparty) as a subprocess to serve a
    folder over HTTP/WebDAV — see "sharing" below. Writing a server with
    resumable uploads, dedup and a browser UI is a project in its own right and
    a good one already exists under MIT.

    Remaining work on the inbound half:

    - "Connect to server" action driving `gio mount` / kio-fuse
    - Discover existing mounts under `/run/user/$UID/gvfs` and
      `/run/user/$UID/kio-fuse-*` and show them in Places
    - Treat a disconnected mount as an error state rather than an empty folder

18. **Terminal panel** (F4 docked Konsole). Dolphin embeds Konsole via KParts,
    which has no equivalent for us. Doing it properly means running a PTY and
    writing a terminal emulator — a project in itself. Options: skip, or embed
    an external terminal via X11 window reparenting (fragile, X11-only, and dead
    on Wayland).

19. **Archive browsing as folders.** Dolphin enters `.zip`/`.tar.gz` as if they
    were directories. Note archives were built once and dropped at the user's
    request; this would revive that decision in a different form.

---

## Sharing (done)

Right-click a folder → **Share over network**, read-only or with uploads
allowed. Heimdall launches copyparty scoped to that folder on a free port and shows
the address; active shares appear in the sidebar with copy and stop buttons, and
everything is torn down when the window closes.

Read-only and read-write are separate commands rather than a toggle, because the
difference is "people can look" versus "people can overwrite". The share list is
always visible while anything is running — a folder open to the network must not
be something you have to remember.

copyparty is located, not bundled: a `copyparty` binary on PATH, then
`python3 -m copyparty`, then a downloaded `copyparty-sfx.py`. The feature hides
itself when none is found.

## Deliberately not doing

- **Service menus** (`.desktop` files in `servicemenus/`) — our scripts menu
  covers the same need with less ceremony. Revisit only if interoperability
  with existing KDE service menus is wanted.
- **Baloo ratings and comments** — Dolphin exposes them; they are a Baloo
  concept with little value outside it. Tags we already share.
- **Konqueror-style embedded viewers.**

---

## Proposed order

1. The eight small gaps, plus batch rename. Each is self-contained and most of
   the plumbing exists.
2. Information panel and per-folder view properties — both change the session
   schema, so they belong together.
3. Decide on network access (item 17). If mounts, it is small; if protocols, it
   is the largest thing in the project.
4. Settings dialog, once there is enough to configure to justify one.
5. Version control decorations.
6. Terminal panel last, or never.
