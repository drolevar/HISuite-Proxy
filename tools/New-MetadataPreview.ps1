param(
    [string]$Destination = (Join-Path $PSScriptRoot '../artifacts/MetadataPreview'),
    [string]$MetadataSourceDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputDirectory = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $outputDirectory) { throw 'Choose a new destination; existing preview files are left untouched.' }
$offer = Get-Content -LiteralPath (Join-Path $root 'examples/metadata-preview/offer.json') -Raw | ConvertFrom-Json
$expectedIds = @('401403','401404','386376')
$hashes = @(
    'AB0D068411D17DAF93C69393B104E2E5EBCCAFBD246F5D5617606ED2EAD95ACD',
    '327460115E80AC2EAC791B9AB0239E7CBCBFD4EF77CA425B34181920D4077415',
    'C4F191CE0E31A547C519D242D83F3EE1CA5CD39FC3DBACF183B1DF5D91F44D13',
    'B9EF3793FC4AA8D10202C251B7D34283BD77243E8096467CCF00DBE373FB2BF6'
)
if ($offer.authorizeResult -ne '' -or $offer.versionPackageCheckResults.Count -ne 3) { throw 'Expected the diagnostic empty-authorization offer.' }
$metadataDirectory = Join-Path $outputDirectory 'metadata'
New-Item -ItemType Directory -Path $metadataDirectory | Out-Null
$packages = @()
for ($index = 0; $index -lt 3; $index++) {
    $component = $offer.versionPackageCheckResults[$index].components[0]
    if ($component.versionID -ne $expectedIds[$index]) { throw 'Unexpected example package IDs.' }
    $name = 'manifest-' + $index + '.xml'
    $target = Join-Path $metadataDirectory $name
    if ($MetadataSourceDirectory) {
        Copy-Item -LiteralPath (Join-Path $MetadataSourceDirectory $name) -Destination $target
    } else {
        Invoke-WebRequest -Uri ($component.url + 'full/filelist.xml') -UseBasicParsing -OutFile $target
    }
    if ((Get-Item -LiteralPath $target).Length -gt 1048576 -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hashes[$index]) { throw ('Unexpected metadata: ' + $name) }
    $packages += @{ url=$component.url; manifest=$name; changelog='changelog.xml' }
}
$changelog = Join-Path $metadataDirectory 'changelog.xml'
if ($MetadataSourceDirectory) {
    Copy-Item -LiteralPath (Join-Path $MetadataSourceDirectory 'changelog-base.xml') -Destination $changelog
} else {
    Invoke-WebRequest -Uri ($offer.versionPackageCheckResults[0].components[0].url + 'full/changelog_base.xml') -UseBasicParsing -OutFile $changelog
}
if ((Get-Item -LiteralPath $changelog).Length -gt 1048576 -or (Get-FileHash -LiteralPath $changelog -Algorithm SHA256).Hash -ne $hashes[3]) { throw 'Unexpected changelog.' }
$offerFile = Join-Path $outputDirectory 'offer.json'
Copy-Item -LiteralPath (Join-Path $root 'examples/metadata-preview/offer.json') -Destination $offerFile
$configuration = Join-Path $outputDirectory 'fixed-offer-preview.json'
@{ metadataDirectory=$metadataDirectory; offerFile=$offerFile; packages=$packages } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configuration -Encoding UTF8
Write-Output ('Prepared metadata-only preview: ' + $configuration)
Write-Output 'Set HISUITE_PROXY_FIXED_OFFER_PREVIEW=1 and HISUITE_PROXY_FIXED_OFFER_CONFIG to that path before starting the Proxy.'
