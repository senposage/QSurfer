# QSurfer

QSurfer is an Explorer-style desktop search and browser for QNAP Qsirch.
It searches indexed NAS content quickly, opens files with the user's normal
Windows permissions, and can browse shares directly through mapped drives or
UNC paths.

## What It Does

- Search files and folders with exact-match, content, type, date, scope, and view controls
- Use browser-style search and folder tabs without losing an active search
- Browse NAS shares and local folders with back, forward, up, address, and folder-tree navigation
- Open, show, copy, rename, delete, favorite, group, and inspect files using familiar Windows behavior
- Display installed Windows preview handlers for supported files
- Keep favorites, groups, saved searches, and recent searches private to each Windows user
- Support light, dark, and Follow Windows themes, taskbar/tray behavior, and a configurable global hotkey

## Build

Requirements: .NET SDK 9 or later on Windows.

```powershell
dotnet build QSurfer.slnx -c Release
```

Run the development app with:

```powershell
dotnet run --project src/QSurfer.Avalonia/QSurfer.Avalonia.csproj
```

`build-qsurfer.bat` recreates a portable Windows package in `dist/QSurfer`.
It includes only a blank `config/config.json` template; configure the NAS
connection through Settings after first launch. Never commit a working config,
session, database, or log.

## Project Layout

- `src/QSurfer.Core`: Qsirch protocol client, settings, rules, history, path resolution, and browsing services
- `src/QSurfer.Avalonia`: Avalonia desktop application and Windows integrations
- `docs`: product contract and migration/parity checklist

## Attribution

QSurfer builds on the Qsirch REST API work from
[iios-co/qsirch](https://github.com/iios-co/qsirch). See [NOTICE](NOTICE)
and [LICENSE](LICENSE).
