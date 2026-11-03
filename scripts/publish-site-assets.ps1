#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$WebsiteRoot = 'F:\Programmer\Mine Programmer\impatus-web',
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$release = Join-Path $root "artifacts\website\$version"
$baseUrl = 'https://impatus.no/uploads/cardgrinder'
$key = Join-Path $env:LOCALAPPDATA 'CardGrinderReleaseSigning\private.pem'
$manifest = Join-Path $release 'latest.json'
$channel = Join-Path $root 'src\CardGrinder.Core\UpdateChannel.json'
$tool = Join-Path $root 'tools\CardGrinder.UpdateManifest'
$installer = Join-Path $release "CardGrinder-$version-Setup-x64.exe"
$portable = Join-Path $release "CardGrinder-$version-Portable-x64.zip"
New-Item -ItemType Directory -Path $release -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'artifacts\release\CardGrinder-Setup.exe') -Destination $installer -Force
Copy-Item -LiteralPath (Join-Path $root 'artifacts\release\CardGrinder-win-x64.zip') -Destination $portable -Force
foreach ($file in @($key, $installer, $portable)) {
    if (!(Test-Path -LiteralPath $file)) { throw "Missing required file: $file. Build the release first." }
}
if ((Get-Item -LiteralPath $installer).VersionInfo.ProductVersion.Trim() -ne $version) {
    throw 'Installer version does not match the project.'
}
$checksumPath = Join-Path $root 'artifacts\release\SHA256SUMS.txt'
$checksumEntries = @(Get-Content -LiteralPath $checksumPath | Where-Object { $_.Trim() })
if ($checksumEntries.Count -ne 2) { throw 'Two release checksums are required.' }
foreach ($line in $checksumEntries) {
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(CardGrinder-Setup\.exe|CardGrinder-win-x64\.zip)$') { throw 'Unexpected checksum entry.' }
    $expectedHash = $Matches[1]
    $packagePath = Join-Path $root ('artifacts\release\' + $Matches[2])
    if ((Get-FileHash -LiteralPath $packagePath).Hash -ne $expectedHash) { throw 'Release checksum verification failed.' }
}
dotnet build $tool -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Manifest tool build failed.' }
dotnet run --no-build --project $tool -c Release -- sign $version $release $baseUrl (Join-Path $root 'docs\RELEASE-NOTES.md') $key $manifest
if ($LASTEXITCODE -ne 0) { throw 'Release signing failed.' }
$signed = dotnet run --no-build --project $tool -c Release -- verify $channel $manifest
if ($LASTEXITCODE -ne 0) { throw 'Release signing key does not match the key trusted by CardGrinder.' }
$localRelease = $signed | ConvertFrom-Json
if ($PrepareOnly) { Write-Output "Prepared signed release $version; nothing uploaded."; return }

$audit = Join-Path $release 'public-verification'
New-Item -ItemType Directory -Path $audit -Force | Out-Null
$currentPath = Join-Path $audit 'previous-latest.json'
$currentResponse = Invoke-WebRequest -Uri "$baseUrl/latest.json" -OutFile $currentPath -PassThru -SkipHttpErrorCheck -MaximumRedirection 0
if ($currentResponse.StatusCode -eq 200) {
    $currentJson = dotnet run --no-build --project $tool -c Release -- verify $channel $currentPath
    if ($LASTEXITCODE -ne 0) { throw 'The existing public update signature is invalid. Publication stopped.' }
    $current = $currentJson | ConvertFrom-Json
    if ([version]$current.version -gt [version]$version) { throw 'Refusing to replace a newer public version.' }
    if ($current.version -eq $version) {
        if ($current.installer.sha256 -ne $localRelease.installer.sha256 -or $current.portable.sha256 -ne $localRelease.portable.sha256) {
            throw 'This version is already published with different files. Increase the version and rebuild.'
        }
        # A retry preserves the original signed publication date and release notes.
        Copy-Item -LiteralPath $currentPath -Destination $manifest -Force
    }
} elseif ($currentResponse.StatusCode -ne 404) { throw "Channel preflight failed: HTTP $($currentResponse.StatusCode)" }

$wrangler = Join-Path $WebsiteRoot 'node_modules\wrangler\bin\wrangler.js'
$config = Join-Path $WebsiteRoot 'wrangler.jsonc'
$site = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
$bucket = ($site.r2_buckets | Where-Object { $_.binding -eq 'UPLOADS' }).bucket_name
if ($bucket -ne 'impatus-site-files') { throw 'Unexpected website storage bucket.' }
function Publish-Object([string]$file, [string]$name, [string]$mime) {
    node $wrangler r2 object put "$bucket/cardgrinder/$name" --file $file --content-type $mime --remote --config $config
    if ($LASTEXITCODE -ne 0) { throw "Upload failed: $name" }
}
function Publish-Immutable([string]$file, [string]$name, [string]$mime) {
    $download = Join-Path $audit $name
    $response = Invoke-WebRequest -Uri "$baseUrl/$name" -OutFile $download -PassThru -SkipHttpErrorCheck -MaximumRedirection 0
    if ($response.StatusCode -eq 404) {
        Publish-Object $file $name $mime
        Invoke-WebRequest -Uri "$baseUrl/$name" -OutFile $download -MaximumRedirection 0
    } elseif ($response.StatusCode -ne 200) { throw "Download preflight failed: $name (HTTP $($response.StatusCode))" }
    if ((Get-FileHash -LiteralPath $download).Hash -ne (Get-FileHash -LiteralPath $file).Hash) {
        throw "Published file differs: $name. Existing files will not be overwritten; use a new version."
    }
    Write-Host "Verified: $name"
}
$entry = Get-Content -LiteralPath (Join-Path $root 'docs\website-entry.json') -Raw | ConvertFrom-Json
$entry.version = $version
$entry.download_file = "/uploads/cardgrinder/" + [IO.Path]::GetFileName($installer)

$entry.images = @("/uploads/cardgrinder/CardGrinder-$version.png")
Publish-Immutable $installer ([IO.Path]::GetFileName($installer)) 'application/vnd.microsoft.portable-executable'
Publish-Immutable $portable ([IO.Path]::GetFileName($portable)) 'application/zip'
Publish-Immutable (Join-Path $root 'docs\images\CardGrinder.png') "CardGrinder-$version.png" 'image/png'
$websiteChecksums = Join-Path $release "SHA256SUMS-$version.txt"
@($installer, $portable) | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) } | Set-Content -LiteralPath $websiteChecksums -Encoding utf8NoBOM
Publish-Immutable $websiteChecksums "SHA256SUMS-$version.txt" 'text/plain; charset=utf-8'
# Expose updates only after all complete public downloads pass verification.
Publish-Object $manifest 'latest.json' 'application/json; charset=utf-8'
$published = Invoke-WebRequest -Uri ($baseUrl + '/latest.json?verify=' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -MaximumRedirection 0
if (($published.Headers['Cache-Control'] -join ',') -notmatch 'no-store') { throw 'Update channel has unsafe cache headers.' }
if ($published.Content -ne [IO.File]::ReadAllText($manifest)) { throw 'Published manifest differs from the signed local file.' }
$entry | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'website-entry.json') -Encoding utf8NoBOM
Write-Output "Published and verified CardGrinder $version."

