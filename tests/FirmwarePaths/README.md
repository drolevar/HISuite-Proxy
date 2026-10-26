# Firmware path tests

From the repository root on Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/FirmwarePaths/run.ps1
```

Requires full Visual Studio or Build Tools with Roslyn and the .NET Framework 4.7.2 targeting pack. Discovery uses `vswhere` or the current developer shell; optional `-MSBuildPath` and `-ReferenceAssemblyDirectory` select explicit installations.

The 53 checks cover separate base/CUST/preload directory selection, current and legacy CDN URL shapes, changed CDN authority/scheme, directory-prefix collisions, case-sensitive paths, queries/fragments, and malformed or unconfigured URLs. URLs are string fixtures; no network requests or vendor binaries are needed. Generated files and results go under `artifacts/tests/FirmwarePaths/`.
