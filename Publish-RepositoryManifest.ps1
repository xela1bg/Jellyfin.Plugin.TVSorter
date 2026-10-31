param(
    [Parameter(Mandatory = $true)]
    [uri] $ReleaseAssetUrl,

    [Parameter(Mandatory = $true)]
    [uri] $RepositoryBaseUrl
)

$ErrorActionPreference = 'Stop'

$buildMetadataPath = Join-Path $PSScriptRoot 'build.yaml'
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'

$pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pythonCommand) {
    $pythonPath = $pythonCommand.Source
}
else {
    $pythonCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe')
    )
    $pythonPath = $pythonCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

if (-not $pythonPath) {
    throw 'Python 3 with JPRM installed is required to generate the repository manifest.'
}

$versionScript = "import sys, yaml; data = yaml.safe_load(open(sys.argv[1], encoding='utf-8')); print(data['version'])"
$versionOutput = & $pythonPath -c $versionScript $buildMetadataPath
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read the authoritative release version from build.yaml.'
}

$version = ($versionOutput | Out-String).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "build.yaml contains an invalid plugin version: '$version'."
}

$expectedAssetName = "tv-sorter_$version.zip"
$expectedAssetPath = "/xela1bg/Jellyfin.Plugin.TVSorter/releases/download/v$version/$expectedAssetName"
if (($ReleaseAssetUrl.Scheme -ne 'https') -or ($ReleaseAssetUrl.Host -ne 'github.com') -or (-not [string]::Equals($ReleaseAssetUrl.AbsolutePath, $expectedAssetPath, [StringComparison]::OrdinalIgnoreCase))) {
    throw "ReleaseAssetUrl must point to the published v$version asset '$expectedAssetName'."
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("tv-sorter-release-" + [guid]::NewGuid().ToString('N'))
$downloadedZipPath = Join-Path $temporaryDirectory $expectedAssetName

try {
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
    Invoke-WebRequest -Uri $ReleaseAssetUrl -OutFile $downloadedZipPath -MaximumRedirection 10

    if (-not (Test-Path -LiteralPath $downloadedZipPath -PathType Leaf) -or (Get-Item -LiteralPath $downloadedZipPath).Length -eq 0) {
        throw 'The published GitHub Release ZIP could not be downloaded or is empty.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($downloadedZipPath)
    try {
        $metadataEntry = $archive.GetEntry('meta.json')
        if ($null -eq $metadataEntry) {
            throw 'The downloaded release ZIP does not contain JPRM meta.json.'
        }

        $reader = [System.IO.StreamReader]::new($metadataEntry.Open())
        try {
            $packageMetadata = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $buildMetadataOutput = & $pythonPath -c "import sys, yaml, json; print(json.dumps(yaml.safe_load(open(sys.argv[1], encoding='utf-8'))))" $buildMetadataPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not parse build.yaml release metadata.'
    }
    $buildMetadata = $buildMetadataOutput | ConvertFrom-Json

    $packageMetadataMatches = ($packageMetadata.version -eq $version) `
        -and ($packageMetadata.guid -eq $buildMetadata.guid) `
        -and ($packageMetadata.owner -eq $buildMetadata.owner) `
        -and ($packageMetadata.targetAbi -eq $buildMetadata.targetAbi)
    if (-not $packageMetadataMatches) {
        throw 'The downloaded release ZIP metadata does not match build.yaml version, GUID, owner, and targetAbi.'
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($downloadedZipPath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($artifact in $buildMetadata.artifacts) {
            if ($entries -notcontains [string]$artifact) {
                throw "The downloaded release ZIP is missing declared artifact '$artifact'."
            }
        }

        if ($entries -notcontains 'image.png') {
            throw 'The downloaded release ZIP is missing the configured image.png.'
        }
    }
    finally {
        $archive.Dispose()
    }

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        & $pythonPath -m jprm repo init $manifestPath
        if ($LASTEXITCODE -ne 0) {
            throw 'JPRM could not initialize manifest.json.'
        }
    }

    # JPRM calculates the catalog checksum from this downloaded release ZIP;
    # the manifest sourceUrl is the exact same GitHub asset URL.
    & $pythonPath -m jprm repo add `
        --url $RepositoryBaseUrl.AbsoluteUri.TrimEnd('/') `
        --plugin-url $ReleaseAssetUrl.AbsoluteUri `
        $manifestPath `
        $downloadedZipPath
    if ($LASTEXITCODE -ne 0) {
        throw 'JPRM could not add the downloaded GitHub Release ZIP to manifest.json.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Write-Output "Updated $manifestPath from the exact published asset $ReleaseAssetUrl. Commit the manifest and generated tv-sorter/image.png before configuring Jellyfin to use the feed."