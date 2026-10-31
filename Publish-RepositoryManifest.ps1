param(
    [Parameter(Mandatory = $true)]
    [uri] $ReleaseAssetUrl,

    [Parameter(Mandatory = $true)]
    [uri] $RepositoryBaseUrl
)

$ErrorActionPreference = 'Stop'

$version = '1.0.0.0'
$packagePath = Join-Path $PSScriptRoot "artifacts\tv-sorter_$version.zip"
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "JPRM release package not found: $packagePath"
}

if ($ReleaseAssetUrl.AbsoluteUri -notmatch '^https://github\.com/xela1bg/Jellyfin\.Plugin\.TVSorter/releases/download/') {
    throw 'ReleaseAssetUrl must be the actual TV Sorter asset URL from a published GitHub Release.'
}

$assetResponse = Invoke-WebRequest -Uri $ReleaseAssetUrl -Method Head -MaximumRedirection 10
if ($assetResponse.StatusCode -lt 200 -or $assetResponse.StatusCode -ge 300) {
    throw "The release asset URL returned HTTP $($assetResponse.StatusCode)."
}

$python = Get-Command python -ErrorAction Stop
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    & $python.Source -m jprm repo init $manifestPath
    if ($LASTEXITCODE -ne 0) {
        throw 'JPRM could not initialize manifest.json.'
    }
}

& $python.Source -m jprm repo add `
    --url $RepositoryBaseUrl.AbsoluteUri.TrimEnd('/') `
    --plugin-url $ReleaseAssetUrl.AbsoluteUri `
    $manifestPath `
    $packagePath
if ($LASTEXITCODE -ne 0) {
    throw 'JPRM could not add the published TV Sorter release to manifest.json.'
}

Write-Output "Updated $manifestPath. Commit it and the generated tv-sorter/image.png before configuring Jellyfin to use the feed."