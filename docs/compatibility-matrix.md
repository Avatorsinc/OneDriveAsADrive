# Compatibility matrix

What's known to work, what's partial, and current limitations. This reflects testing as of July 2026 on the 1.3.x line; contributions/reports welcome via [issues](https://github.com/Avatorsinc/OneDriveAsADrive/issues).

## Platforms

| Platform | Status |
|----------|--------|
| Windows 11 | ✅ Tested |
| Windows 10 | ✅ Expected (same WebDAV redirector + WAM) |
| Windows Server 2019/2022 | ⚠️ Should work; WebClient service isn't installed by default |
| ARM64 Windows | ⚠️ Untested; the x64 build runs under emulation |

## Accounts

| Account type | Status |
|--------------|--------|
| Personal Microsoft account | ✅ Works out of the box (self-consent) |
| Work/school OneDrive | ✅ Works; may need [admin consent](admin-consent.md) |
| SharePoint document library | ✅ Works; needs broader scopes + likely admin consent |
| Multiple accounts on one PC | ✅ Pin the identity with `account` in config |
| Mixing personal + work in one instance | ❌ Not possible — one identity per running instance |

## File operations

| Operation | Status | Notes |
|-----------|--------|-------|
| Browse / list folders | ✅ | Paginated — large folders load fully |
| Read / download | ✅ | Range requests supported (seek in media) |
| Upload (small, ≤4 MB) | ✅ | Simple PUT |
| Upload (large, >4 MB) | ✅ | Chunked upload session; truncated streams fail loudly |
| Create folder | ✅ | |
| Delete | ✅ | Permanent (goes to OneDrive recycle bin) |
| Rename / move within a drive | ✅ | Instant (server-side) |
| Move **between** drives | ⚠️ | Windows falls back to copy-then-delete (full re-upload) |
| Office (Word/Excel) open & save | ✅ | Fake WebDAV locks satisfy Office's lock requirement |
| Multi-user file locking | ❌ | No cross-user locking — last write wins (not a file server) |

## Known limitations

- **Not a file server.** No SMB/DFS-style locking semantics; treat it as "my cloud files as a drive letter."
- **Cross-drive moves are copy+delete**, not instant.
- **Throughput** is WebDAV-over-loopback → Graph, slower than SMB for bulk operations.
- **First-run auth needs a visible window** (handled by the installer's sign-in step); fully headless first-consent isn't possible with delegated auth.

## Runtime

- Self-contained single-file exe — **no .NET install required** on target machines.
- Built on .NET 9 (`win-x64`).
