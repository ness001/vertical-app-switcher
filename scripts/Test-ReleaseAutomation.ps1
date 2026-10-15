$ErrorActionPreference = 'Stop'
$versionScript = Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1'
$publishScript = Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1'
$global:ReleaseTestTags = @('v0.1.0')
$global:ReleaseTestCurrentTags = @()
$global:ReleaseTestCommit = 'a' * 40
$global:ReleaseTestPages = '[]'
$global:ReleaseTestPublished = $null

# Mock only Git and GitHub IO. The real version and publication scripts run.
function git {
    $global:LASTEXITCODE = 0
    switch ($args[0]) {
        'merge-base' { return }
        'tag' {
            if ($args[1] -eq '--points-at') { return $global:ReleaseTestCurrentTags }
            return $global:ReleaseTestTags
        }
        default { throw "Unexpected Git operation: $args" }
    }
}
function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'release') {
        $global:ReleaseTestPublished = @($args)
        return
    }
    if ($args[1] -like '*/releases?*') { return $global:ReleaseTestPages }
    if ($args[1] -like '*/git/matching-refs/*') { return '[]' }
    if ($args[1] -like '*/commits/*') { return $global:ReleaseTestCommit }
    throw "Unexpected GitHub operation: $args"
}
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

[xml]$project = Get-Content (Join-Path $PSScriptRoot '../src/VerticalAppSwitcher/VerticalAppSwitcher.csproj') -Raw
$baseVersion = [version][string]$project.Project.PropertyGroup.Version
$first = & $versionScript -Ref refs/heads/main -OutputPath ''
Assert ($first.Version -eq $baseVersion.ToString()) 'Project version was not used for the first release.'
$global:ReleaseTestTags = @('v0.1.0', "v$baseVersion", 'v9.0.0-rc.1')
$next = & $versionScript -Ref refs/heads/main -OutputPath ''
$expectedNext = '{0}.{1}.{2}' -f $baseVersion.Major, $baseVersion.Minor, ($baseVersion.Build + 1)
Assert ($next.Version -eq $expectedNext) 'Patch increment failed.'
$global:ReleaseTestCurrentTags = @("v$baseVersion")
$rerun = & $versionScript -Ref refs/heads/main -OutputPath ''
Assert ($rerun.Version -eq $baseVersion.ToString()) 'Rerun changed the version.'
$preview = & $versionScript -Ref refs/tags/v0.3.0-rc.1 -OutputPath ''
Assert ($preview.Version -eq '0.3.0-rc.1' -and $preview.Prerelease) 'Prerelease selection failed.'
Write-Host 'PASS first version, patch increment, rerun, and prerelease selection'

$directory = Join-Path ([IO.Path]::GetTempPath()) ('release-automation-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $names = @()
    foreach ($runtime in @('win-x64', 'win-arm64')) {
        $name = "VerticalAppSwitcher-0.2.0-$runtime.zip"
        $file = Join-Path $directory $name
        Set-Content -LiteralPath $file -Value 'checksum fixture'
        $hash = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
        Set-Content -LiteralPath "$file.sha256" -Value "$hash  $name"
        $names += $name, "$name.sha256"
    }
    $parameters = @{
        Tag = 'v0.2.0'
        Version = '0.2.0'
        Commit = $global:ReleaseTestCommit
        Repository = 'example/repository'
        AssetDirectory = $directory
    }
    & $publishScript @parameters
    Assert ($global:ReleaseTestPublished -contains '--target') 'Release was not tied to the build commit.'
    Assert ($global:ReleaseTestPublished -contains $global:ReleaseTestCommit) 'Build commit missing.'
    Assert ($global:ReleaseTestPublished -notcontains '--draft') 'Release remained a draft.'
    Assert ($global:ReleaseTestPublished -notcontains '--verify-tag') 'New automatic tag could not be created.'
    Write-Host 'PASS automatic release targets the checked commit'

    $release = @{
        tag_name = 'v0.2.0'; draft = $false; prerelease = $false
        assets = @($names | ForEach-Object { @{ name = $_; state = 'uploaded'; size = 123 } })
    }
    $global:ReleaseTestPages = '[[' + ($release | ConvertTo-Json -Depth 5 -Compress) + ']]'
    $global:ReleaseTestPublished = $null
    & $publishScript @parameters
    Assert ($null -eq $global:ReleaseTestPublished) 'Rerun attempted to replace an existing release.'
    Write-Host 'PASS existing complete release is left unchanged'

    $release.assets = @($release.assets | Select-Object -First 3)
    $global:ReleaseTestPages = '[[' + ($release | ConvertTo-Json -Depth 5 -Compress) + ']]'
    $rejected = $false
    try { & $publishScript @parameters }
    catch { $rejected = $_.Exception.Message -like 'The existing release is incomplete*' }
    Assert $rejected 'An incomplete release was accepted.'
    Assert ($null -eq $global:ReleaseTestPublished) 'An incomplete release was overwritten.'
    Write-Host 'PASS incomplete release requires review'

    $global:ReleaseTestPages = '[]'
    Set-Content -LiteralPath (Join-Path $directory ($names[0] + '.sha256')) -Value 'bad checksum'
    $rejected = $false
    try { & $publishScript @parameters }
    catch { $rejected = $_.Exception.Message -like 'Checksum mismatch*' }
    Assert $rejected 'Bad checksum did not block publication.'
    Assert ($null -eq $global:ReleaseTestPublished) 'Publication ran after a bad checksum.'
    Write-Host 'PASS checksum failure blocks publication'
}
finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
