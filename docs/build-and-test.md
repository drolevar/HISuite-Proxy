# Build and test on Windows

Use Visual Studio 2022 / Build Tools with .NET desktop build tools, the .NET
Framework 4.7.2 targeting pack, and Windows Script Host COM tooling. Full MSBuild
is required because this project uses `ResolveComReference`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The script finds MSBuild with `vswhere`, restores packages.config dependencies,
and builds `artifacts/bin/Release/HiSuite Proxy.exe`. `-MSBuildPath` and
`-Configuration Debug` are supported. Generated dependencies and outputs are ignored.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\FirmwarePaths\run.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Diagnostics\run.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Patcher\run.ps1 -OriginalHiSuiteDirectory 'C:\path\to\original-14.0.0.380'
```

Suites discover MSBuild's Roslyn compiler and Framework reference assemblies.
Outputs stay under `artifacts/tests`. Patcher tests require the exact original
HiSuite files; no vendor binaries are included. Diagnostics tests generate
synthetic metadata and use ephemeral loopback HTTP, without a real Proxy GUI,
Huawei requests or certificate trust changes.

The upstream SharpZipLib 1.2.0 dependency produces NuGet audit warnings during
restore. Its update is separate from these Proxy changes.
