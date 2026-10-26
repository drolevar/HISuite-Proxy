param(
    [string]$ProxyPath,
    [string]$MSBuildPath,
    [string]$ReferenceAssemblyDirectory
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'These tests require Windows and the .NET Framework 4.7.2 targeting pack.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ProxyPath) { $ProxyPath = Join-Path $repository 'artifacts/bin/Release/HiSuite Proxy.exe' }
if (-not (Test-Path -LiteralPath $ProxyPath -PathType Leaf)) { throw 'Build Proxy first with build.ps1, or supply -ProxyPath pointing to that built EXE.' }
$ProxyPath = (Get-Item -LiteralPath $ProxyPath).FullName
$applicationDirectory = Split-Path -Parent $ProxyPath

if (-not $MSBuildPath) {
    $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
    if (-not $MSBuildPath) {
        $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $MSBuildPath = $command.Source }
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'Install Visual Studio or Build Tools with MSBuild and the .NET Framework 4.7.2 targeting pack, or supply -MSBuildPath.'
}
$MSBuildPath = (Get-Item -LiteralPath $MSBuildPath).FullName
$msbuildDirectory = Split-Path -Parent $MSBuildPath
$compiler = @(
    (Join-Path $msbuildDirectory 'Roslyn/csc.exe'),
    (Join-Path (Split-Path -Parent $msbuildDirectory) 'Roslyn/csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $compiler) { throw 'Roslyn csc.exe was not found beside the selected full MSBuild installation.' }
if (-not $ReferenceAssemblyDirectory) {
    $ReferenceAssemblyDirectory = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.7.2'
}
foreach ($name in @('mscorlib.dll', 'System.dll', 'System.Core.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ReferenceAssemblyDirectory $name) -PathType Leaf)) {
        throw "Missing .NET Framework 4.7.2 reference assembly: $name. Install the targeting pack or supply -ReferenceAssemblyDirectory."
    }
}
$ReferenceAssemblyDirectory = (Get-Item -LiteralPath $ReferenceAssemblyDirectory).FullName
foreach ($name in @('Titanium.Web.Proxy.dll', 'Newtonsoft.Json.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $applicationDirectory $name) -PathType Leaf)) { throw "Built application dependency is missing: $name" }
}

$run = Join-Path $repository ('artifacts/tests/Diagnostics/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$binary = Join-Path $run 'bin'
New-Item -ItemType Directory -Path $binary -Force | Out-Null
$copiedProxy = Join-Path $binary 'HiSuite Proxy.exe'
Copy-Item -LiteralPath $ProxyPath -Destination $copiedProxy
Get-ChildItem -LiteralPath $applicationDirectory -Filter '*.dll' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $binary $_.Name)
}
if (Test-Path -LiteralPath ($ProxyPath + '.config') -PathType Leaf) {
    Copy-Item -LiteralPath ($ProxyPath + '.config') -Destination ($copiedProxy + '.config')
}
$inputs = [ordered]@{}
foreach ($name in @('HiSuite Proxy.exe', 'Titanium.Web.Proxy.dll', 'Newtonsoft.Json.dll')) {
    $file = Join-Path $binary $name
    $inputs[$name] = @{
        assembly = [Reflection.AssemblyName]::GetAssemblyName($file).FullName
        sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
}
foreach ($suite in @('LoggerTests', 'PreviewTests')) {
    $testSource = Join-Path $PSScriptRoot ($suite + '.cs')
    $output = Join-Path $binary ($suite + '.exe')
    $response = Join-Path $run ($suite + '.rsp')
    @(
        '/nologo', '/nostdlib+', '/target:exe', '/platform:x86', '/optimize+', '/langversion:7.3', '/codepage:65001',
        ('/out:"' + $output + '"'),
        ('/reference:"' + (Join-Path $ReferenceAssemblyDirectory 'mscorlib.dll') + '"'),
        ('/reference:"' + (Join-Path $ReferenceAssemblyDirectory 'System.dll') + '"'),
        ('/reference:"' + (Join-Path $ReferenceAssemblyDirectory 'System.Core.dll') + '"'),
        ('/reference:"' + $copiedProxy + '"'),
        ('/reference:"' + (Join-Path $binary 'Titanium.Web.Proxy.dll') + '"'),
        ('/reference:"' + (Join-Path $binary 'Newtonsoft.Json.dll') + '"'),
        ('"' + $testSource + '"')
    ) | Set-Content -LiteralPath $response -Encoding UTF8
    & $compiler /noconfig (('@' + $response))
    if ($LASTEXITCODE -ne 0) { throw "$suite compilation failed: $LASTEXITCODE" }
    if (Test-Path -LiteralPath ($copiedProxy + '.config') -PathType Leaf) {
        Copy-Item -LiteralPath ($copiedProxy + '.config') -Destination ($output + '.config')
    }
}
@{
    createdUtc = [DateTime]::UtcNow.ToString('o')
    msbuild = $MSBuildPath
    compiler = $compiler
    targetFramework = '.NETFramework,Version=v4.7.2'
    applicationInputs = $inputs
    loggerTestSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'LoggerTests.cs') -Algorithm SHA256).Hash
    previewTestSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PreviewTests.cs') -Algorithm SHA256).Hash
    fixtures = 'Generated synthetic XML, package URLs and empty-authorization offer; no vendor metadata or phone captures.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'provenance.json') -Encoding UTF8

$logger = Join-Path $binary 'LoggerTests.exe'
& $logger (Join-Path $run 'logger') 2>&1 | Tee-Object -FilePath (Join-Path $run 'logger-results.txt')
$loggerResult = $LASTEXITCODE
& $logger (Join-Path $run 'logger-unwritable') unwritable 2>&1 | Tee-Object -FilePath (Join-Path $run 'unwritable-results.txt')
$unwritableResult = $LASTEXITCODE
& (Join-Path $binary 'PreviewTests.exe') (Join-Path $run 'preview') 2>&1 | Tee-Object -FilePath (Join-Path $run 'preview-results.txt')
$previewResult = $LASTEXITCODE
@{
    loggerExitCode = $loggerResult
    unwritableSinkExitCode = $unwritableResult
    previewExitCode = $previewResult
    allPassed = ($loggerResult -eq 0 -and $unwritableResult -eq 0 -and $previewResult -eq 0)
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding UTF8
Write-Output ('Diagnostics test output: ' + $run)
if ($loggerResult -ne 0 -or $unwritableResult -ne 0 -or $previewResult -ne 0) { throw 'Diagnostics tests failed; inspect the result files in the output directory.' }
