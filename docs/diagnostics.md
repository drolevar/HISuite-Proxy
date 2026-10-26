# Proxy diagnostics

The source build writes redacted JSONL files to `logs` beside the executable.
Set `HISUITE_PROXY_LOG_DIR` to choose another directory. Logging is independent
of the original Debug Logging checkbox, which still writes unredacted `logs.txt`.
Use the JSONL files when sharing diagnostics.

Records include UTC time, process/thread IDs, a session flow ID, selected UI
settings, request action, API request/response metadata, local overrides and
exceptions. Firmware ZIP bodies stay streamed. Identifiers, tokens, certificates,
attestations, signatures, cookies and authorization values are redacted. Unknown
JSON scalars retain shape without values. Records are limited to 64 KiB; files
rotate at 16 MiB, with eight files per session.

Wrappers return original values and preserve exceptions. An unavailable log
directory disables diagnostics without changing proxy results. Non-seekable
WebException bodies remain unread by default; `HISUITE_PROXY_CAPTURE_ERROR_BODY=1`
opts into consuming their bounded, redacted error text.

Follow one `flowId` within the same session. `request.handler.begin` records
selection; `incoming.body` records queries; `webclient.upload/response` record
explicit upstream POSTs; `response.sent.to.hisuite` records local overrides;
`response.final.to.hisuite` records forwarded API replies; and
`request.handler.caught` exposes swallowed handler exceptions. Normal `full`,
alternate `hfull_switch` and rollback `full_back` are separate queries.

## Guarded rollback metadata preview

This optional display test is limited to BLA-L29/C432 rollback IDs
**401403 / 401404 / 386376**. It is off by default. Prepare the three manifests
and common changelog with the metadata-only, hash-checking helper:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\New-MetadataPreview.ps1
$env:HISUITE_PROXY_FIXED_OFFER_PREVIEW = '1'
$env:HISUITE_PROXY_FIXED_OFFER_CONFIG = (Resolve-Path .\artifacts\MetadataPreview\fixed-offer-preview.json).Path
& '.\artifacts\bin\Release\HiSuite Proxy.exe'
```

For existing copies, supply `-MetadataSourceDirectory <directory>` containing
`manifest-0.xml`, `manifest-1.xml`, `manifest-2.xml` and `changelog-base.xml`.
The helper refuses an existing destination and never fetches firmware archives.

The adapter parses `commonRules.PackageType` as JSON, accepts compact/pretty
requests, returns the fixed `full_back` offer before the original handler, and
serves cached XML. The window title identifies preview mode. ZIP/APP/IMG/BIN
downloads and `authorize.action` are blocked. Missing configuration fails closed;
unavailable selected metadata returns 404.

The example's empty `authorizeResult` string is a format probe. Live checks with
the instrumented official release produced Earlier Versions in HiSuite 11 and
14.0.0.380; native observation confirmed parser/selection success in 14. The
source build carries the same adapter, with separate loopback tests. This proves
display only: empty authorization is rejected later, and Huawei approval,
phone acceptance from EMUI 10.0.0.202 and the precise T8 suffix remain unverified.

To restore ordinary behavior, close the Proxy, remove both preview environment
variables and restart. Default file diagnostics do not change firmware offers.
