# QSurfer Handoff

Updated: 2026-09-02

## Current Release State

- `main` is pushed at commit `1603411` (`Release QSurfer 1.0.3`).
- Annotated tag `v1.0.3` is pushed.
- Portable Windows package is built at:
  `release\QSurfer-1.0.3-win-x64.zip`
- The GitHub release draft is open in the signed-in in-app browser at the
  `v1.0.3` release form. Its title, notes, and ZIP are complete. It has not
  been published yet; click **Publish release** after confirmation.
- The release ZIP is 42.16 MB and contains the single-file self-contained
  Windows application plus `config\config.json` from the template, not a
  real workstation configuration.

## Important Working Tree Note

`src/QSurfer.Core/Services/ResultRules.cs` is modified but deliberately was
not included in the 1.0.3 commit. Treat it as separate in-progress work: do
not discard or stage it unless its purpose has been reviewed with the user.

## Product Shape

QSurfer is an Avalonia/.NET 9 portable NAS search and browsing application.
It combines NAS search REST integration with Windows/UNC file browsing, native
Windows previews, per-Windows-user favorites, shell-style file operations,
and NAS snapshot/recycle-bin recovery workflows.

Key source areas:

- `src/QSurfer.Avalonia/ViewModels/MainWindowViewModel.cs`: UI state,
  tabs, search, browse navigation, panes, and commands.
- `src/QSurfer.Core/Services/QsirchClient.cs`: REST search/session behavior.
- `src/QSurfer.Core/Services/PathMapper.cs`: NAS/internal/UNC/drive path
  conversion. Preserve the "wizard behind the curtain" rule: users should
  see usable Windows drive paths whenever one exists.
- `src/QSurfer.Core/Services/NasFileBrowser.cs`: browse/file operations.
- `src/QSurfer.Avalonia/Services/ShellPreviewHost.cs`: Windows native preview
  handlers.
- `src/QSurfer.Core/Services/SnapshotTimelineService.cs`: snapshots and
  recycle-bin recovery.

## Current Navigation Rules

- Local user folders are shown first, alphabetically.
- Mapped drives are shown after them, alphabetically by drive letter.
- Accessible but unmapped NAS shares are retained as UNC roots.
- A NAS share with a mapped drive displays the drive letter, not a redundant
  UNC prefix.
- Path mapping preserves spaces in share names and matches both NAS short
  names and FQDNs. If Windows already maps the NAS using an FQDN, UNC fallback
  uses that FQDN so domain-authenticated Windows access continues working.

## Build and Package

- Target framework: .NET 9.
- Run `build-qsurfer.bat` from the repository root.
- The script intentionally closes a running `QSurfer.exe`, waits briefly,
  rebuilds `dist\QSurfer`, and installs `config.template.json` as
  `dist\QSurfer\config\config.json`.
- The build emits known Windows-platform/Avalonia warnings but currently
  succeeds.

## 1.1 Backlog

- Design multi-window tab tear-off. It is feasible, but should begin with a
  window/workspace host that owns a collection of already-isolated tab states,
  then add a clear Move to new window command before drag-and-drop tear-off.
  Shared services (connection/session, favorites database, path mapper,
  preview cache) should be application-scoped; tab and browse state should
  remain window-scoped.
- Update Help with the correct GitHub link.
- Verify sorting updates immediately and applies to folders for every
  applicable arrange mode.
- Produce and verify a Linux build; keep Windows shell integrations behind
  appropriate platform fallbacks. Replace Windows DPAPI credential storage
  with the native Linux desktop secret service/keyring; do not store Qsirch
  passwords in portable JSON or plaintext SQLite on either platform.
- Continue gathering ordinary runtime logs after extended use and investigate
  anything beyond expected failed-path/auth retries.

## Recent Context

- Qsirch search should not use content search unless the user explicitly
  enables **Search contents**. A prior `apple` test returned unrelated PDFs
  because content matching leaked through; this was investigated using NAS
  logs and should remain covered by future testing.
- Recycle-bin flattening now lists deleted files directly and uses modified
  date so recent deletes paint first. Hidden/temp files are hidden by default.
- Snapshot restores deliberately require confirmation for overwrites; folder
  restore policy is guarded separately from normal file recovery.
