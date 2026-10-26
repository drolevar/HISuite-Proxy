param(
    [string]$MSBuildPath,
    [string]$ReferenceAssemblyDirectory
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'These tests require Windows and the .NET Framework 4.7.2 targeting pack.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

# Use the Roslyn compiler belonging to a full Visual Studio/MSBuild installation.
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
$msbuildDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($MSBuildPath))
$compiler = @(
    (Join-Path $msbuildDirectory 'Roslyn/csc.exe'),
    (Join-Path (Split-Path -Parent $msbuildDirectory) 'Roslyn/csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $compiler) { throw 'Roslyn csc.exe was not found beside the selected full MSBuild installation.' }
if (-not $ReferenceAssemblyDirectory) {
    $ReferenceAssemblyDirectory = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.7.2'
}
foreach ($reference in @('mscorlib.dll', 'System.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ReferenceAssemblyDirectory $reference) -PathType Leaf)) {
        throw "Missing .NET Framework 4.7.2 reference assembly: $reference. Install the targeting pack or supply -ReferenceAssemblyDirectory."
    }
}

$outputBase = Join-Path $repository 'artifacts/tests/FirmwarePaths'
$run = Join-Path $outputBase ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$output = Join-Path $run 'FirmwarePath.Tests.exe'
$response = Join-Path $run 'compile.rsp'
$routing = Join-Path $repository 'HiSuite Proxy/FirmwarePaths.cs'
$testSource = Join-Path $PSScriptRoot 'FirmwarePathTests.cs'
@(
    '/nologo', '/nostdlib+', '/target:exe', '/optimize+', '/langversion:7.3',
    ('/out:"' + $output + '"'),
    ('/reference:"' + (Join-Path $ReferenceAssemblyDirectory 'mscorlib.dll') + '"'),
    ('/reference:"' + (Join-Path $ReferenceAssemblyDirectory 'System.dll') + '"'),
    ('"' + $routing + '"'),
    ('"' + $testSource + '"')
) | Set-Content -LiteralPath $response -Encoding UTF8
& $compiler /noconfig (('@' + $response))
if ($LASTEXITCODE -ne 0) { throw "Firmware path test compilation failed: $LASTEXITCODE" }
@{
    createdUtc = [DateTime]::UtcNow.ToString('o')
    msbuild = [IO.Path]::GetFullPath($MSBuildPath)
    compiler = $compiler
    targetFramework = '.NETFramework,Version=v4.7.2'
    sourceSha256 = (Get-FileHash -LiteralPath $routing -Algorithm SHA256).Hash
    testSourceSha256 = (Get-FileHash -LiteralPath $testSource -Algorithm SHA256).Hash
    networkRequests = 0
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'provenance.json') -Encoding UTF8
& $output 2>&1 | Tee-Object -FilePath (Join-Path $run 'results.txt')
$result = $LASTEXITCODE
Write-Output ("Firmware path test output: " + $run)
if ($result -ne 0) { throw "Firmware path tests failed: $result" }
