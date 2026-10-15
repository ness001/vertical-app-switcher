param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Commit,

    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [bool]$Prerelease = $false,
    [string]$AssetDirectory = 'release'
)

$ErrorActionPreference = 'Stop'
if ($Tag -cne "v$Version" -or $Commit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'The release tag, version, or commit is invalid.'
}

$assets = @()
foreach ($runtime in @('win-x64', 'win-arm64')) {
    $name = "VerticalAppSwitcher-$Version-$runtime.zip"
    $archive = Join-Path $AssetDirectory $name
    $checksum = "$archive.sha256"
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Content -LiteralPath $checksum -Raw).Trim() -cne "$hash  $name") {
        throw "Checksum mismatch for $name."
    }
    $assets += $archive, $checksum
}

# List published releases and drafts so a rerun cannot replace existing assets.
$releasesJson = & gh api "repos/$Repository/releases?per_page=100" --paginate --slurp
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect existing releases.' }
$pages = ConvertFrom-Json -InputObject ($releasesJson -join "`n") -NoEnumerate
$existing = @($pages | ForEach-Object { $_ } | Where-Object { $_.tag_name -ceq $Tag })
if ($existing.Count -gt 0) {
    $release = $existing[0]
    if ($release.draft -or $release.prerelease -ne $Prerelease) {
        throw 'The existing release state differs. Review it before retrying.'
    }
    $tagCommit = & gh api "repos/$Repository/commits/$Tag" --jq .sha
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $Commit) {
        throw 'The existing release points to another commit.'
    }
    foreach ($asset in $assets) {
        $name = Split-Path -Leaf $asset
        $match = @($release.assets | Where-Object { $_.name -ceq $name -and $_.state -eq 'uploaded' -and $_.size -gt 0 })
        if ($match.Count -ne 1) {
            throw "The existing release is incomplete: $name. Review it before retrying."
        }
    }
    Write-Host "Release $Tag is already published for $Commit; no assets were changed."
    return
}

# Resolve existing tags, including annotated tags, before creating a release.
$tagsJson = & gh api "repos/$Repository/git/matching-refs/tags/$Tag"
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect remote version tags.' }
$tags = ConvertFrom-Json -InputObject ($tagsJson -join "`n")
$matchingTag = @($tags | Where-Object { $_.ref -ceq "refs/tags/$Tag" })
if ($matchingTag.Count -gt 0) {
    $tagCommit = & gh api "repos/$Repository/commits/$Tag" --jq .sha
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $Commit) {
        throw 'The selected version tag already points to another commit.'
    }
}

$notes = @'
## Install

Download and extract the ZIP for your Windows architecture, then run VerticalAppSwitcher.exe.
Requires Windows 10 or 11 and the matching [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
Keep LICENSE and NOTICE with the executable. Executables are not code-signed; follow your organization's security policy.
Each ZIP has a SHA-256 checksum file for integrity checks.

Version 0.1.0 users must install an update-enabled version manually once. Later versions can check for updates from the tray menu.
'@
$arguments = @(
    'release', 'create', $Tag, '--repo', $Repository,
    '--target', $Commit, '--generate-notes', '--notes', $notes,
    '--title', "Vertical App Switcher $Tag"
)
if ($matchingTag.Count -gt 0) { $arguments += '--verify-tag' }
if ($Prerelease) { $arguments += '--prerelease' }
& gh @arguments @assets
if ($LASTEXITCODE -ne 0) {
    throw 'Could not publish the release. Check GitHub Releases before retrying; existing releases are not overwritten.'
}
