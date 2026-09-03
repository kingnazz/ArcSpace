# NTFS MFT fast-scanner investigation

## Decision for ArcSpace v1.5

ArcSpace v1.5 does **not** ship an MFT scanner. The current recursive filesystem scanner remains the production engine and now sits behind `IDiskScanner`, which is the feature boundary for a future optional engine.

An MFT mode is technically feasible and could make volume discovery dramatically faster, but the low-risk Windows API path does not provide file sizes in the enumeration records. Getting accurate sizes would require either a file-handle query for each record or direct parsing of NTFS file-record attributes. The first approach can give back much of the performance advantage; the second introduces a large correctness and maintenance surface that is not justified for the v1.5 refinement release.

## Windows API shape

A read-only implementation would need the following pieces:

1. `GetVolumeInformationW` to verify the target is a local NTFS volume and supports the required volume features.
2. `CreateFileW` on a volume path such as `\\.\C:` to obtain a volume handle.
3. `DeviceIoControl` with `FSCTL_ENUM_USN_DATA` and `MFT_ENUM_DATA` to enumerate MFT-backed USN records.
4. Parsing of `USN_RECORD_V2` and, where supported, `USN_RECORD_V3` to collect file reference numbers, parent reference numbers, names, attributes, and directory flags.
5. A parent-ID graph to reconstruct paths and aggregate folder totals without walking directories.
6. A separate size source because USN records do not contain logical or allocated file size:
   - `OpenFileById` plus `GetFileInformationByHandleEx(FileStandardInfo)` for each file; or
   - read-only retrieval and parsing of raw NTFS file records and their `$DATA` attributes.
7. Explicit handling for hard links, sparse files, compressed files, alternate data streams, deleted or changing records, and records whose parent is unavailable.

Relevant Microsoft documentation:

- [FSCTL_ENUM_USN_DATA](https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)
- [USN_RECORD_V2](https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)
- [CreateFileW: physical disks and volumes](https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew)
- [OpenFileById](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-openfilebyid)
- [FILE_STANDARD_INFO](https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_standard_info)
- [GetVolumeInformationW](https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw)
- [Reparse points and file operations](https://learn.microsoft.com/windows/win32/fileio/reparse-points-and-file-operations)

## Elevation and Backstage behavior

Direct volume access through `CreateFileW` requires administrative privileges on supported Windows versions. ArcSpace should not assume that an interactive or Backstage process has the necessary token merely because a technician can access the machine.

ConnectWise ScreenConnect documents Backstage as a limited Windows logon session in which some programs may not run. It separately exposes permissions for running toolbox items as the system user. A future MFT engine therefore needs a runtime capability probe rather than a hardcoded assumption about Backstage identity or elevation.

Recommended behavior:

- Do not add a `requireAdministrator` application manifest to the normal ArcSpace executable.
- Do not show a UAC prompt automatically at startup.
- Offer fast mode only when the selected target resolves to a local NTFS volume and the read-only volume handle opens successfully.
- If access is denied, the volume is not NTFS, the target is a network path, or any validation fails, fall back immediately to `DiskScanner` without blocking the scan.
- Keep normal recursive scanning available as an explicit option even when fast mode is supported.

ConnectWise references:

- [Backstage mode](https://docs.connectwise.com/ScreenConnect_Documentation/Get_started/Host_client/View_menu/Backstage_mode)
- [Role-based permissions](https://docs.connectwise.com/ScreenConnect_Documentation/Get_started/Administration_page/Security_page/Define_user_roles_and_permissions/List_of_role-based_security_permissions)

## Constraints and correctness risks

### NTFS and target scope

`FSCTL_ENUM_USN_DATA` works against an NTFS volume handle, not an arbitrary folder handle. A selected-folder scan would still enumerate volume records and then filter the reconstructed parent graph to the requested subtree. It is not a replacement for network, FAT/exFAT, removable, or otherwise unsupported targets.

### Reparse points

MFT enumeration sees records rather than traversing namespace targets. That is useful for loop avoidance, but ArcSpace must preserve its existing policy: identify `FILE_ATTRIBUTE_REPARSE_POINT`, do not follow the target, and clearly count or report the skipped record. Opening records by ID must use reparse-aware flags so a junction, symbolic link, cloud placeholder, or other filter-managed object is not silently followed or hydrated.

### Hard links and size accounting

One NTFS file can have multiple directory entries. The engine must define whether ArcSpace reports logical namespace usage or unique physical allocation and must avoid accidentally multiplying physical usage when aggregating hard-linked records. The existing scanner naturally reports each enumerated directory entry; an MFT engine must make that behavior deliberate.

### Live mutation

The MFT can change while it is being enumerated. Records may be renamed, deleted, moved, or have missing parents between enumeration and size lookup. The implementation needs tolerant graph assembly, bounded orphan handling, cancellation checks, and a final consistency label rather than pretending the result is a filesystem snapshot.

### Raw record parsing

Parsing raw NTFS records is the fastest route to resident and non-resident `$DATA` sizes, but it couples ArcSpace to NTFS on-disk structures and edge cases including sparse, compressed, encrypted, multi-stream, and fragmented attributes. It should only be considered after a test corpus and differential validation against Windows APIs exist. No write-capable volume operation is needed or acceptable.

## Expected benefit

MFT enumeration can remove most directory-open and path-walk overhead on large local NTFS volumes, so the discovery phase should be substantially faster than recursive enumeration, especially on volumes with very high file counts. End-to-end benefit depends on the size strategy:

- Per-record `OpenFileById` size queries are simpler and safer, but may become the dominant cost and can interact with permissions, antivirus, cloud placeholders, and filesystem filters.
- Raw `$DATA` parsing offers the largest potential speedup, but also carries the highest implementation and correctness risk.

No performance number should be promised until both approaches are benchmarked on HDD, SSD, very large MFTs, sparse/compressed files, hard links, reparse points, protected folders, and active filesystems.

## Recommended next step

For the release after v1.5, build a separate, opt-in experimental engine behind `IDiskScanner` with this sequence:

1. Capability probe and safe fallback only.
2. MFT/USN enumeration plus parent-graph reconstruction, without exposing it in normal UI.
3. Benchmark `OpenFileById` size lookup against the recursive scanner.
4. Differential tests comparing file counts, logical sizes, folder totals, top files, reparse handling, and cancellation results.
5. Consider raw file-record parsing only if per-file handle queries fail to deliver a meaningful real-world improvement.

The production recommendation is therefore: **yes, investigate an MFT scanner next, but ship it only as an optional elevated NTFS engine after measured correctness and fallback testing—not as the default scanner.**
