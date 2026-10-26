# HiSuite 14 patch preparation

The patcher supports only the exact official OVE **14.0.0.380** files. It checks
the complete SHA-256 of `HiSuite.exe` and `httpcomponent.dll`, RunInfo.ini version,
and every expected byte. It rejects unknown builds, partial patches and mixed
original/patched file sets.

```powershell
& '.\artifacts\bin\Release\HiSuite Proxy.exe' --hisuite14-inspect 'C:\path\to\HiSuite'
& '.\artifacts\bin\Release\HiSuite Proxy.exe' --hisuite14-prepare 'C:\path\to\HiSuite' 'C:\path\to\new-copy'
& '.\artifacts\bin\Release\HiSuite Proxy.exe' --hisuite14-restore 'C:\path\to\new-copy'
```

`--hisuite14-prepare` creates and patches a new full copy.
`--hisuite14-apply <directory>` applies the profile in place. Both validate
`<filename>.hisuite-proxy.original` backups before writing. Matching backups
are preserved; conflicting ones are rejected. Multi-file writes roll back earlier
changes if a later write fails. Close HiSuite first; locked files cause an error.

CLI dispatch runs before the WinForms constructor, so inspection/preparation
do not start the listener or install certificates. Unknown/incomplete commands
print usage. SETUP recognizes the profile and reports concrete patch errors.

The HTTP edits relax certificate checks for local TLS interception; the EXE
edit changes the observed checksum-failure result. These file edits alone do
**not** route HiSuite 14 update traffic through the Proxy. A compatible transport
setup is also required. This change covers the Proxy application and its tests;
native client runtime tools are separate.

See [`tests/Patcher`](../tests/Patcher/README.md) for validation against temporary
copies of hash-checked user-supplied originals. Passing patch tests establishes
file transformations and backups, not firmware authorization or flashing.
