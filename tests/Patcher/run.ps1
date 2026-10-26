param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OriginalHiSuiteDirectory,
    [string]$MSBuildPath,
    [string]$ReferenceAssemblyDirectory
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'These tests require Windows and the .NET Framework 4.7.2 targeting pack.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$original = (Get-Item -LiteralPath $OriginalHiSuiteDirectory -ErrorAction Stop).FullName
if (-not (Test-Path -LiteralPath $original -PathType Container)) { throw 'OriginalHiSuiteDirectory must be a directory.' }

# Discover full Visual Studio/MSBuild rather than the dotnet SDK compiler or a specific VS edition/year.
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

# Fail before creating output or copying fixtures when the supplied vendor files are unknown or modified.
$pinned = @{
    'HiSuite.exe' = '4549da99080922facf3349b78179dee975c1d5d259fe4ea41a533da46697d304'
    'httpcomponent.dll' = '302e710b21fcdabb7835b5cb12693f4eb1bd40a68f29df98e474ccb0e87f2ab3'
}
foreach ($name in $pinned.Keys) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $original $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $pinned[$name]) { throw "$name does not match the supported original HiSuite 14.0.0.380 SHA-256." }
}
if (-not (Test-Path -LiteralPath (Join-Path $original 'RunInfo.ini') -PathType Leaf)) { throw 'OriginalHiSuiteDirectory must also contain RunInfo.ini.' }
$outputBase = Join-Path $repository 'artifacts/tests/Patcher'
$sourcePrefix = $original.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($outputBase.Equals($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or $outputBase.StartsWith($sourcePrefix + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test output must not be inside the supplied original HiSuite directory.'
}
$run = Join-Path $outputBase ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$output = Join-Path $run 'PatchEngine.Tests.exe'
$response = Join-Path $run 'compile.rsp'
$engine = Join-Path $repository 'HiSuite Proxy/HiSuite14Patcher.cs'
$testSource = Join-Path $PSScriptRoot 'PatchEngineTests.cs'
@(
    '/nologo', '/nostdlib+', '/target:exe', '/optimize+', '/langversion:7.3',
    ('/out:"' + $output + '"'),
    ('/reference:"' + (Join-Path $ReferenceAssemblyDirectory 'mscorlib.dll') + '"'),
    ('/reference:"' + (Join-Path $ReferenceAssemblyDirectory 'System.dll') + '"'),
    ('"' + $engine + '"'),
    ('"' + $testSource + '"')
) | Set-Content -LiteralPath $response -Encoding UTF8
& $compiler /noconfig (('@' + $response))
if ($LASTEXITCODE -ne 0) { throw "Patch engine test compilation failed: $LASTEXITCODE" }
@{
    createdUtc = [DateTime]::UtcNow.ToString('o')
    msbuild = [IO.Path]::GetFullPath($MSBuildPath)
    compiler = $compiler
    targetFramework = '.NETFramework,Version=v4.7.2'
    sourceSha256 = (Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash
    testSourceSha256 = (Get-FileHash -LiteralPath $testSource -Algorithm SHA256).Hash
    originalFileSha256 = $pinned
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'provenance.json') -Encoding UTF8
& $output $original (Join-Path $run 'fixtures') 2>&1 | Tee-Object -FilePath (Join-Path $run 'results.txt')
$result = $LASTEXITCODE
Write-Output ("Patcher test output: " + $run)
if ($result -ne 0) { throw "Patch engine tests failed: $result" }
