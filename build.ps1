param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$MSBuildPath
)
$ErrorActionPreference = 'Stop'
if (-not $MSBuildPath) {
    $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with .NET desktop build tools and the .NET Framework 4.7.2 targeting pack.' }
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) { throw 'Full Visual Studio MSBuild is required, including ResolveComReference support.' }
$outputDirectory = Join-Path $PSScriptRoot ('artifacts/bin/' + $Configuration)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$solution = Join-Path $PSScriptRoot 'HiSuite Proxy.sln'
& $MSBuildPath $solution /nologo /restore /verbosity:minimal /p:RestorePackagesConfig=true (('/p:Configuration=' + $Configuration)) (('/p:OutputPath=' + $outputDirectory + '/'))
if ($LASTEXITCODE -ne 0) { throw ('Proxy build failed: ' + $LASTEXITCODE) }
Write-Output ('Built: ' + (Join-Path $outputDirectory 'HiSuite Proxy.exe'))
