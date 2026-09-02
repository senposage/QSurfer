# QSurfer Product Specification

This is the migration contract for QSurfer. It combines the original
QsirchFloating/Python brief, the production WPF application, and the intended
QSurfer direction. The WPF application remains the behavioral reference until
QSurfer has passed the required acceptance checks below.

## Product Statement

QSurfer is a fast NAS search and browser for office users. It should feel like
Windows Explorer searching a shared drive: familiar, direct, and safe. Qsirch
is an indexing service behind the curtain, not something users need to see or
understand.

The application must work both with mapped drives and direct UNC paths. A
mapped drive is preferred for Open and Show when available. Direct UNC is the
fallback, especially for remote and VPN users. Internal Qsirch paths remain in
logs and metadata only.

## Deployment And Data Boundaries

| Data | Scope | Rule |
| --- | --- | --- |
| Connection defaults, exclusions, path mappings, global rules | Portable shared `config.json` | One deployment can provide defaults for every workstation. |
| Machine behavior, pinned tabs, window preferences | Per machine in `config.json` host section | Machines do not overwrite one another. |
| Favorites, groups, saved searches, recent searches | Local SQLite, per Windows user and machine | Never leak one user’s saved items to another. |
| Qsirch session material | Local encrypted store | Never ship it in a release package. |
| Logs | Separate client/session and search logs | Keep user/session visibility out of noisy search logging. |

The release package must contain a sanitized configuration template only. It
must never contain a working credential, session, local database, or log.

## Required Workspace Shape

The main window is one Explorer-like workspace, not a stack of independent
dialogs.

1. **Command strip**: open, show location, favorite/group actions, preview,
   settings, help, and application controls. Commands act on the active
   result/tab and are disabled when no selection makes them meaningful.
2. **Tab strip**: browser-style search and browse tabs. Each tab owns its
   state, can be stopped independently, and has an in-tab close control. A
   pinned search tab cannot be cleared or closed accidentally and survives per
   machine.
3. **Search/filter strip**: query, exact-match, content-search, type filters,
   folders, date range, scope, arrange/view, sort, clear filters, stop, load
   more, and search. Controls reflow at narrower widths; the query field never
   disappears.
4. **Navigation pane**: a resizable, collapsible Explorer navigation region.
   Favorites and nested favorite groups are its first source. It is designed
   to grow into shares, locations, and the active browser folder tree rather
   than becoming a one-purpose favorites list.
5. **Content pane**: the active tab's results or folder contents. It claims
   full width whenever either side pane is hidden. All views use recognizable
   shell icons and retain normal Explorer selection behavior.
6. **Preview pane**: a resizable, collapsible right pane. It shows a native
   preview only when a real capable handler exists. Unsupported types,
   especially video, state that a preview is unavailable rather than showing a
   misleading substitute.
7. **Status strip**: concise, live status and counts: files, folders, hidden
   by rules, result-limit state, and operation progress.

## Search Contract

- QSurfer authenticates once per usable Qsirch session, reuses it safely, and
  does not interfere with a user's NAS web session.
- A search clears stale results when a new query begins, then paints the first
  server batch immediately. Background tabs can defer painting until focused.
- Search continues through Qsirch pages until there are no more usable results
  or the user-configured limit is reached. Results hidden by visibility rules
  do not consume the visible-result limit.
- The initial request is small and recent-first so a user sees useful results
  quickly. Further pages continue without blocking the interface.
- Folder and file searches are both intentional. Folders are shown before
  files when enabled, but no unrelated matching result is hidden simply
  because it has a parent folder.
- Exact match, content search, types, folders, date ranges, scope, arrange,
  sort, and view are tab-local. Changing a filter refilters/reruns the active
  search immediately and never leaks into another tab.
- The status text must say `Ready 500 results`, never concatenate words and
  counts. The limit warning is visually strong only while more results might
  remain; it disappears after Load more proves there are none.

## Results Contract

- Details default order is **Name, Location, Date modified, Type, Size**.
  Headers sort; Ctrl-click supports multi-column sort. Users can hide columns.
- Supported views mirror Explorer conventions: details, list, small icons,
  large icons, and content. Icon views use a grid, not a vertical text list.
- Folder groups may be collapsed and expanded. A matching folder appears in
  the result order naturally with its matching children; groups never crush
  independent matches.
- Search-match highlighting is optional and must remain readable in light and
  dark themes without making hover or selection slow.
- **Open** uses the Windows default handler for the resolved user-facing path.
  **Show** navigates to its containing folder. Both are available in the
  current-result list, saved results, favorites, and context menus.
- Multiple selected results support favorite and group operations without
  freezing a search or repainting the whole window synchronously.
- Shell file/folder icons are the default. Qsirch thumbnails are an optional
  enhancement, not a dependency for normal result painting.

## Navigation, Favorites, And Browser Contract

- Favorites are a real folder tree: nested groups, saved searches, and
  unfiled favorites. Tree items can be opened with a double click and have
  appropriate context actions such as Open, Show, Unfavorite, Delete saved
  search, and Delete folder.
- Add to group supports multiple selected results. Group creation is compact,
  preserves tree expansion, and runs off the UI thread.
- The folder browser uses direct UNC paths without requiring a mapped drive.
  It has back, forward, up, breadcrumbs, folder tree, refresh, and tab-local
  navigation state.
- File operations such as create folder, rename, copy, move, and delete use a
  background operation queue with progress, conflict handling, and explicit
  confirmation for destructive work.

## Preview Contract

- Preview is optional and off until shown by the user, but the Preview command
  is always available in the workspace.
- Use registered Windows preview handlers where supported. Map Qsirch results
  to the user-visible mapped or UNC path before invoking a handler.
- Do not use a browser component, Qsirch low-resolution placeholder, or fake
  text preview as a replacement for a native preview.
- A future cross-platform preview layer may render supported documents, but
  must never replace a working native handler with a lower-quality version.

## Application Behavior Contract

- One QSurfer instance per machine. A second launch activates the existing
  window.
- Tray/minimize behavior, taskbar presence, close-to-tray/exit behavior, and
  a configurable global show/hide shortcut are all behavior settings.
- Clicking the tray icon restores the window. The app cannot become invisible
  from both tray and taskbar.
- Follow Windows, Light, and Dark themes are readable, including all hover,
  selection, input, dropdown, and scrollbar states.
- Help is visible and current. It documents settings in plain language and
  links to the QSurfer GitHub branch. Donation remains present but visually
  distant from the primary Close action.

## Migration Sequence

### Gate 1: Coherent shell

Build the tab strip, resizable navigation pane, resizable preview pane, content
pane, status strip, and responsive command/filter strips as a single layout.
The navigation pane begins with the existing SQLite favorites tree and is
architected to accept browser locations/folders. Do not ship a flat favorites
sidecar.

### Gate 2: Search and results parity

Move the proven Qsirch pipeline intact: session reuse, paging, cancellation,
visibility rules, path resolution, exact/content search, all filters, dates,
folder handling, result counts, sort, views, icons, selection, and result
actions. Prove first-paint latency and isolated tab behavior with logs.

### Gate 3: Saved-workflow parity

Move recent searches, favorites, nested groups, saved searches, context menus,
batch actions, and history maintenance. All database work must be asynchronous
and per-user.

### Gate 4: Browser and preview

Complete UNC browser tabs, navigation history, folder tree, operation queue,
shell icons, and native preview-host integration. Only then add file-management
commands to the regular user workflow.

### Gate 5: Application behavior and release

Migrate tray, single-instance activation, taskbar controls, hotkey, theme,
settings, help, licensing, sanitized packaging, update-ready layout, and
release validation. QSurfer does not replace the WPF release until each
required workflow above is tested from the packaged binary.

## Current Branch Rule

No feature is considered migrated because its control exists. It is migrated
only when its complete workflow, persistence scope, empty/error state,
performance behavior, and corresponding menu/keyboard actions are present.

## 1.1 Hardening Focus

- **Local search delegation:** searches rooted on `C:\` and standard local user
  folders should use Windows search and Explorer facilities rather than the
  NAS/Qsirch pipeline. NAS searches remain Qsirch-backed.
- **Preview responsiveness:** reduce native preview-handler startup hesitation,
  preserve a warm host when safe, and make slow or unsupported handlers fail
  clearly without stalling the results workspace.
- **Live diagnostics review:** collect and review the separate client/session
  and search logs after a normal office work session. Resolve exceptions,
  repeated authentication, slow operations, UI stalls, and noisy logging before
  adding new feature scope.
- **Linux build and verification:** produce a Linux package and verify the
  search, browsing, path resolution, favorites, themes, and unsupported-preview
  behavior on Linux. Keep Windows shell integrations as platform-specific
  enhancements with clear Linux fallbacks.
- **Help and project links:** refresh the in-app help documentation and ensure
  every GitHub link points to the QSurfer repository.
- **Sorting correctness:** make each sort selection take effect immediately and
  apply it consistently to folders as well as files whenever that sort mode has
  meaningful folder data.
