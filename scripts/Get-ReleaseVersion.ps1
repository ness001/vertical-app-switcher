param(
    [Parameter(Mandatory = $true)]
    [string]$Ref,

    [string]$OutputPath = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stablePattern = '^v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))$'
$tagPattern = '^v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?)$'

Push-Location -LiteralPath $root
try {
    & git merge-base --is-ancestor HEAD origin/main
    if ($LASTEXITCODE -ne 0) {
        throw 'The release commit must be part of main.'
    }

    if ($Ref.StartsWith('refs/tags/', [StringComparison]::Ordinal)) {
        $tag = $Ref.Substring('refs/tags/'.Length)
        if ($tag -cnotmatch $tagPattern) {
            throw 'Use a version tag such as v1.2.3 or v1.2.3-rc.1.'
        }
        $version = $Matches[1]
    }
    elseif ($Ref -eq 'refs/heads/main') {
        [xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/VerticalAppSwitcher/VerticalAppSwitcher.csproj') -Raw
        $baseVersion = [string]$project.Project.PropertyGroup.Version
        if ("v$baseVersion" -cnotmatch $stablePattern) {
            throw 'The project Version must be a stable three-part version.'
        }

        $tags = @(& git tag --list 'v*')
        if ($LASTEXITCODE -ne 0) { throw 'Could not list version tags.' }
        $stableTags = @(
            foreach ($candidate in $tags) {
                if ($candidate -cmatch $stablePattern) {
                    [pscustomobject]@{ Tag = $candidate; Version = [version]$Matches[1] }
                }
            }
        )
        $currentTags = @(& git tag --points-at HEAD)
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect tags on the release commit.' }
        $existing = $stableTags | Where-Object { $_.Tag -in $currentTags } |
            Sort-Object Version -Descending | Select-Object -First 1
        if ($existing) {
            # A rerun keeps its version rather than publishing the same commit again.
            $version = $existing.Version.ToString()
        }
        else {
            $latest = $stableTags | Sort-Object Version -Descending | Select-Object -First 1
            if (-not $latest -or [version]$baseVersion -gt $latest.Version) {
                $version = $baseVersion
            }
            else {
                $next = $latest.Version
                if ($next.Build -ge 65534) {
                    throw 'The patch version is exhausted. Raise the project minor or major Version.'
                }
                $version = '{0}.{1}.{2}' -f $next.Major, $next.Minor, ($next.Build + 1)
            }
        }
        $tag = "v$version"
    }
    else {
        throw 'Automatic releases run only from main or a version tag.'
    }

    foreach ($part in $version.Split('-')[0].Split('.')) {
        if ([long]$part -gt 65534) { throw 'Version components must not exceed 65534.' }
    }
    $result = [pscustomobject]@{
        Version = $version
        Tag = $tag
        Prerelease = $version.Contains('-')
    }
    if ($OutputPath) {
        "version=$version" >> $OutputPath
        "tag=$tag" >> $OutputPath
        "prerelease=$($result.Prerelease.ToString().ToLowerInvariant())" >> $OutputPath
    }
    $result
}
finally {
    Pop-Location
}
