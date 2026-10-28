param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path $PSScriptRoot ('artifacts/bin/' + $Configuration)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$project = Join-Path $PSScriptRoot 'HiSuite Proxy\HiSuite Proxy.csproj'
& dotnet build $project -c $Configuration -o $outputDirectory --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw ('Proxy build failed: ' + $LASTEXITCODE) }
Write-Output ('Built: ' + (Join-Path $outputDirectory 'HiSuite Proxy.exe'))
