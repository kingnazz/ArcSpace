# ArcSpace

ArcSpace is a lightweight Windows disk-space analyzer built for fast technician use, especially ConnectWise Control Backstage sessions.

## V1 goals

- Single self-contained Windows x64 EXE
- No installer, service, database, or persistent configuration
- Scan an entire drive or a selected folder
- Folder tree sorted largest-first
- File and folder totals with human-readable sizes
- Top 100 largest files
- Quick filters for files over 100 MB, 500 MB, and 1 GB
- Cancel a running scan
- Skip inaccessible paths and NTFS reparse points safely
- Open locations in Explorer and copy paths
- Confirmed permanent file/folder deletion
- Disk used/free/total summary

## Backstage workflow

1. Download `ArcSpace.exe` from the latest GitHub Actions build artifact.
2. Copy it into the remote machine's ConnectWise Control Backstage session.
3. Run `ArcSpace.exe`.
4. Scan the problem drive or folder.
5. Close the app and delete the EXE when finished.

ArcSpace writes no scan database or configuration files.

## Build locally

Requirements: Windows and the .NET 8 SDK.

```powershell
.\scripts\publish.ps1
```

The portable executable is written to:

```text
dist\win-x64\ArcSpace.exe
```

## Stack

- C#
- .NET 8
- WPF
- Self-contained single-file `win-x64` publish

## Scope

ArcSpace V1 intentionally avoids treemaps, duplicate-file analysis, cleanup automation, shell extensions, and persistent scan indexes. The goal is to answer one technician question quickly: **what is using the disk space?**
