# QSurfer Parity Audit

The retired WPF application remains the behavioral reference. This is the current source-level test checklist for the Avalonia QSurfer build, not a wish list.

## Ready For Test

### Application and settings

- NAS host, port, credentials, HTTPS, and certificate verification are configurable in the UI.
- Portable connection defaults and per-machine preferences load through the shared configuration store.
- Path mappings prefer an available mapped drive and fall back to UNC paths.
- Light, dark, and Follow Windows themes persist.
- Taskbar visibility, minimize/close to the notification area, click-to-restore tray icon, global show/hide hotkey, and single-instance activation are implemented.
- Global visibility rules are editable only by a local Administrator or a domain Domain Admin. Local or domain accounts that are not privileged are treated as non-admins.
- Help, GitHub, and donation links are available from the application.

### Search and results

- Searches stream results from Qsirch, begin with a small first page, and continue through additional pages.
- Independent tabs, tab pinning, per-tab query/filter/view/sort state, tab persistence, stop, clear, and Load more are implemented.
- Exact match, content search, multi-select file types including Folders, date presets and custom dates, scope, recent searches, and Clear filters are available.
- Details, list, small-icon, and large-icon views are available with shell icons, folder-first ordering, folder groups, match highlighting, column visibility, and Ctrl multi-column sorting.
- Result actions include Open, Show in File Explorer, Copy full path, Properties, Favorite, and Add to group.

### Favorites, preview, and browser

- Saved searches, favorites, nested favorite groups, group deletion, batch favorite operations, and private per-user SQLite data are available.
- The preview pane is resizable, collapses cleanly, and uses installed Windows preview handlers when possible. Video remains intentionally unsupported.
- The integrated browser opens direct UNC paths and mapped drives, preserves search results while browsing, and offers Back, Forward, Up, Refresh, a folder tree, typed locations, and clickable breadcrumbs.
- Browser rows support multi-select and folder-first ordering. Right-click actions include Open, Cut, Copy, Paste, Rename, Delete, New folder, Create shortcut, Copy full path, Properties, and Favorite.
- Copy places real file and folder objects on the Windows clipboard; QSurfer can also paste file and folder selections from Windows.
- Delete uses the standard shell path so NAS-side recycle-folder behavior is preserved when configured by the share.

## Still To Improve

- Browser location and history are currently shared by the active workspace rather than being independently retained for every search tab.
- Large copy, move, and delete operations run off the UI thread, but they do not yet have an Explorer-style progress queue, conflict dialog, or cancellation surface.
- Cut state is complete inside QSurfer. Advertising a cut operation to other Windows applications needs the native shell preferred-drop-effect format before it can claim full cross-app Cut parity.
- Drag-and-drop between the browser, local folders, and Explorer is not yet implemented.
- Qsirch thumbnail retrieval remains optional; Windows shell icons are the normal default.
- Search and paint performance still need real-world NAS testing under several active tabs before this replaces the WPF release.

## Test Focus

1. Browse a mapped drive, a direct UNC share, and a local folder; verify Back, Forward, Up, breadcrumbs, and the folder tree.
2. Test multi-select Copy, Cut, Paste, Rename, Delete, New folder, Create shortcut, and Properties against a non-production folder.
3. Run simultaneous searches in multiple tabs, change filters while searching, pin a tab, restart, and refocus it.
4. Verify favorites, nested groups, saved searches, and native previews against the account and mapped paths users will actually have.
