# Diagnostics and metadata-preview tests

Build the application first, then run from the repository root on Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Diagnostics/run.ps1
```

The runner discovers full Visual Studio/MSBuild and Roslyn, compiles .NET Framework 4.7.2 test executables, and references the built `artifacts/bin/Release/HiSuite Proxy.exe` with its own restored dependencies. Optional `-ProxyPath`, `-MSBuildPath`, and `-ReferenceAssemblyDirectory` select explicit inputs. Generated executables, copied dependencies, synthetic fixtures, logs, results, and hashes stay under ignored `artifacts/tests/Diagnostics/`.

The logger suite covers recursive privacy rules, public firmware-field retention, body identity, request correlation, selected UI-state snapshots, error-stream preservation, exception identity, concurrency, and bounded valid JSONL records. A separate process checks an unwritable log destination. WebClient calls use an in-memory fake transport and prohibit real requests.

The preview suite runs 16 routing and failure cases plus the actual source `DiagnosticsBeforeRequest` wrapper. It generates minimal synthetic XML, URLs, offer and configuration files; only the three IDs required by the preview guard are retained. These fixtures convey no firmware identity, installation authorization, handset compatibility or rollback approval.

Preview traffic connects only to an ephemeral IPv4 loopback HTTP proxy. All responses are supplied by the preview dispatcher or a local fake fallback; destination host strings are never resolved. There is no TLS interception, certificate trust, system proxy change, phone action or port 7777 listener. The built application entry point and Form1 constructor are never invoked. The actual source wrapper case uses an uninitialized Form1 only with preview enabled; the real original handler's initialized-GUI behavior is outside this suite.
