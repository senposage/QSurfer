# QSurfer User Guide

QSurfer is a desktop workspace for searching indexed shared storage and browsing files with the same access you already have in the file system. Search finds likely results quickly; browsing, opening, copying, and recovery use your ordinary network permissions.

## First connection

1. Open Settings from the gear button.
2. Enable and configure at least one search provider: **Qsirch**, **QIndexer**, or both.
3. Save the connection.

Qsirch uses the NAS address, port, username, and password. QIndexer uses its own service address and search token. These credentials are used only for search requests. They do not override normal file permissions. Files still open through your mapped drive, network share, or mounted share using the currently signed-in user's access.

### Choosing a search provider

- **Qsirch** searches the NAS index supplied by the NAS.
- **QIndexer** searches an independently deployed QIndexer service. It can complement a NAS index and can also be configured to index other approved roots.
- When both are enabled, QSurfer asks both and combines matching results. If one is temporarily unavailable, the healthy provider can still return results. At least one provider must be configured.
- A new or rebuilding QIndexer root may have fewer results until its crawl finishes. Its administrator controls what locations it indexes.

## Search

Type a file or folder name in the search field and press Enter or choose Search.

- Leave **Search contents** off to find matching names and paths.
- Turn **Search contents** on to find indexed text inside documents. Results may include files whose names do not contain the words.
- Turn **Exact match** on for whole-word name matching. With content search also on, QSurfer sends the complete phrase to enabled search providers.
- Use type, date, arrangement, and view controls to reduce a broad result set.
- **Load more** continues the active query until the NAS has no further results or you stop it.

The active search provider must have indexed a file before QSurfer can find its contents. OCR availability for scanned documents depends on that provider's index.

### What the search controls mean

| Control | Plain-English meaning |
| --- | --- |
| Exact match | Find complete words in names. Turn it on when a short word is finding too many similar names. |
| Search contents | Also look inside indexed document text. Useful for a phrase in a letter or PDF, but it is broader and may take longer. |
| File types | Keep only selected kinds of results, such as folders, PDFs, or Word documents. |
| Any date / From / To | Limit by modified date. Leave a date blank when it should not limit the search. Future dates are blocked. |
| Clear filters | Reset the checkboxes, type choices, dates, and folder scope. It keeps the search words and current result list. |
| Scope | Choose all folders, or use Navigation selections to search inside selected NAS folders. |
| Arrange | Change the order of current results. It does not rerun the search. |
| View | Change whether results use details columns, a compact list, or icons. It does not rerun the search. |
| Load more | Ask the NAS for more matches from the same search. |
| Stop | Stop the search that is running now. Results already shown stay visible. |

## Search folders

Folder scope is attached to an individual search tab.

- **Shift+Click** a folder in Navigation to include it in the active search.
- Shift+Click another folder to include multiple branches.
- Shift+Click a child of an included folder to exclude that child branch.
- **Shift+Alt+Click** directly toggles an excluded folder. It works even when the rest of the search is not scoped.
- Use the **+** beside the scope list to add another path from the address picker. Select a suggestion, then add another path if needed.
- Use the switch beside a listed path to make that branch included or excluded. Use its **-** button to remove it.
- Choose **Clear** to remove both included and excluded folders from the current tab.

The active scope is shown below the filters: included folders use the accent color and excluded folders use neutral gray. A more-specific included child stays included even when one of its parents is excluded. The defaults can be changed in **Settings > Shortcut > Navigation scope gestures**.

## Browse

Use Navigation, the address field, or a folder result to open a folder. Back, Forward, Up, and Refresh work like Explorer. Double-click a file to open it in its associated application.

Search and browse share the same tab. Browse a folder without losing its search, then return by clicking the search field or the tab's search glyph. Click the folder glyph or address field to return to browsing.

QSurfer prefers mapped drives when available, then falls back to an accessible network or mounted path. Path mappings under Settings resolve the internal share path returned by search to the location used by the file system.

## Results and file operations

Use Details, List, Small icons, or Large icons from View. Details columns can be sorted by selecting their headers. Arrangement controls broader folder-first and recency presentations.

Right-click files and folders for Open, Show, Copy, Cut, Paste, Rename, Delete, New folder, Create shortcut, Copy full path, and Properties. These actions follow the operating system and file system rules. NAS deletes normally flow through the NAS recycle behavior when it is enabled.

## Favorites, recent searches, and saved searches

Favorites, recent searches, saved searches, and pinned tabs are personal to the signed-in user.

- Use the result star or context menu to favorite a file or folder.
- Save a recurring search from its tab.
- Saved searches remember the query, types, dates, exact/content settings, view, sort order, and included or excluded folder scope.
- The save glyph on a saved-search tab updates it immediately. A new search asks for a name; an existing name cannot silently overwrite another saved search.
- Opening a saved search opens it in a new QSurfer tab.
- Pinned tabs reopen after QSurfer starts and rerun after the window has settled.

## Preview and recovery

Select an item to load its Preview pane. Images can render directly; document preview availability depends on supported installed preview components. Opening and browsing remain available when preview is unsupported.

When the NAS exposes them, QSurfer can surface two recovery paths:

- **Recycle Bin**: restore a deleted NAS item to its original location.
- **Version history**: inspect accessible snapshot copies. **Recover copy** writes a separate copy. **Restore original** replaces a live item only after confirmation and retains the live item as a QSurfer safety copy.

Folder restore has stricter protection than file restore. It requires a temporary setting override and an explicit acknowledgement because one folder can replace many files.

## Settings

- **Connection** configures Qsirch and QIndexer independently. Enable either provider or both.
- **Behavior** controls startup, preview, recovery, visible system folders, result limits, and default view settings.
- **Appearance** controls system/light/dark mode, accent behavior, and custom colors.
- **Path mappings** connects search-result paths to mapped drives, network locations, or mounted shares.
- **Rules** hide matching results or browse entries; they do not change NAS permissions.
- **History** manages the current user's favorites, searches, and saved data.
- **Shortcut** configures keyboard actions and Navigation modifier-click gestures.

Most preferences are kept in the user's local QSurfer database. Shared deployment connection defaults and deliberate global rules are handled separately by an authorized administrator.

### Settings in plain language

**Connection**

- **Use Qsirch** enables the NAS search provider. **NAS host or IP**, **Port**, **Username**, and **Password** apply only to Qsirch.
- **Use QIndexer** enables the independent QIndexer provider. Its **Service host or IP**, **Port**, and **Search token** apply only to QIndexer.
- Each provider has its own **Use HTTPS** and **Verify the HTTPS certificate** settings. Keep certificate verification on unless an administrator confirms a known internal certificate problem.
- Qsirch and QIndexer can run together. They do not share credentials, and turning either one off keeps its saved connection details for later testing or recovery.

**Path mappings on Linux**

- **Mount at boot** installs a system-wide systemd mount for an SMB share. It remains available after reboot and while QSurfer is closed.
- The mount uses a root-owned credential file and remains present until the operating system unmounts it. QSurfer does not fall back to an eager fstab entry on systems without systemd.

**Behavior**

- **Show in taskbar**, **Minimize to notification area**, **Keep QSurfer in the notification area when closing**, and **Always on top** change only how the QSurfer window behaves.
- **Clear results when the search text is cleared** keeps an empty search tab visually clean. Turn it off if you prefer results to remain after erasing the words.
- **Search file contents by default** starts new searches in the broad document-text mode. Leave it off for the fastest name searches.
- **Highlight matching text** colors matching words in names and paths. It does not change the search itself.
- **Show preview pane by default** opens the right-side preview when a new window starts.
- **Use NAS thumbnails when available** allows picture and document thumbnails. It is cosmetic and safe to leave on.
- **Show raw NAS recovery system folders**, **Show QSurfer safety copies**, and **Show hidden and temporary files** expose clutter normally hidden from daily work. Leave them off unless investigating a recovery or temporary-file issue.
- **Flatten Recycle Bin** makes deleted items easier to scan by putting them in one dated list instead of making you open every original-folder branch.
- **Show local folders** and **Show local drives** control only what appears in Navigation. Mapped network shares remain available.
- **Search result limit** is the initial ceiling before you choose Load more. A higher number can take longer to appear.
- **Initial results** controls the first painted batch. **Additional results** controls later batches. Lower initial values feel faster; larger later values reduce repeated updates.
- **Search timeout** is how long QSurfer waits for a NAS response before treating it as a connection problem.
- **Restore original location** controls whether a snapshot may replace a live file. QSurfer still asks before it writes.
- **Folder restore override** is deliberately temporary and risky: it permits one whole-folder snapshot restore after the required acknowledgement.

**Appearance**

- **Follow system** uses the computer's light or dark preference. Choose Light or Dark to keep one mode.
- **Use the current system accent color** makes QSurfer's buttons, links, focus outline, and folder accents follow the operating system color.
- **Color preset** gives a starting color scheme. **Custom** preserves manual color choices.
- The color chips change only QSurfer: **Main surface** is the background, **Accent** is the action color, **Selected result** is the selected row, **Hover** is the row under the pointer, and **Match highlight** marks matching search text.

## Troubleshooting

- If search fails, check the enabled provider's host, port, HTTPS, certificate validation, and credentials or token.
- If a result cannot open, verify its mapped drive, network location, or mounted share is available and review Path mappings.
- If QIndexer results are incomplete, confirm its configured root has finished crawling. If the same file appears twice through a crawler mount and a shared path, its QIndexer administrator needs to reconcile that root's canonical paths.
- If a preview does not appear, try Open or Show; preview support depends on the installed platform handler for that file type.
- Check the lower-right status text for NAS connection state and the local logs for details when a request fails.
