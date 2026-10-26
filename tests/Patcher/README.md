# HiSuite 14 patcher tests

From the repository root on Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Patcher/run.ps1 -OriginalHiSuiteDirectory 'C:\fixtures\HiSuite-14.0.0.380-original'
```

Requires full Visual Studio or Build Tools with Roslyn and the .NET Framework 4.7.2 targeting pack. The runner discovers MSBuild using `vswhere` or the current developer shell. Optional `-MSBuildPath` and `-ReferenceAssemblyDirectory` select explicit installations.

Provide your own original `HiSuite.exe`, `httpcomponent.dll`, and `RunInfo.ini` in the input directory. The two binary SHA-256 values must match the supported official 14.0.0.380 profile. No vendor binaries are included or downloaded by these tests.

The 11 cases run on new copies under `artifacts/tests/Patcher/`: round trip and idempotency, corrupt/partial/mixed/version rejection, backup conflicts and loss, rollback when a second-file replacement fails, and prepared-copy isolation. The supplied originals are only read. Results, compiler inputs, provenance, and generated fixtures remain under that artifact directory for inspection. No application, installer, network request, or phone command is launched.
