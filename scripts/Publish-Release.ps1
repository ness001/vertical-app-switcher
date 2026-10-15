param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-((0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?$')]
    [string]$Version,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VerticalAppSwitcher\VerticalAppSwitcher.csproj'
$icon = Join-Path $root 'src\VerticalAppSwitcher\Assets\AppIcon.ico'

foreach ($part in ($Version.Split('-')[0].Split('.'))) {
    if ([long]$part -gt 65534) {
        throw 'Version components must be between 0 and 65534 for Windows file metadata.'
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'bin\release'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$packageName = "VerticalAppSwitcher-$Version-$RuntimeIdentifier"
$archive = Join-Path $OutputDirectory "$packageName.zip"
$checksum = "$archive.sha256"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $checksum)) {
    throw "Release files already exist for $packageName. Use an empty output directory."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stagingDirectory = Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $stagingDirectory $packageName
New-Item -ItemType Directory -Path $publishDirectory | Out-Null

try {
    & (Join-Path $PSScriptRoot 'GenerateIcon.ps1') -OutputPath $icon

    # Use the repository SDK without replacing or restarting the local app.
    Push-Location -LiteralPath $root
    try {
        & dotnet publish `
            $project `
            --configuration Release `
            --runtime $RuntimeIdentifier `
            --self-contained false `
            --output $publishDirectory `
            -p:PublishSingleFile=true `
            -p:DebugSymbols=false `
            -p:DebugType=None `
            -p:ContinuousIntegrationBuild=true `
            "-p:Version=$Version"

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    foreach ($name in @('VerticalAppSwitcher.exe', 'LICENSE', 'NOTICE')) {
        $file = Join-Path $publishDirectory $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Required release file was not found: $file"
        }
    }

    $instructions = @"
Vertical App Switcher $Version ($RuntimeIdentifier)

Requires Windows 10 or 11 and the matching .NET 10 Desktop Runtime:
https://dotnet.microsoft.com/download/dotnet/10.0

Extract the ZIP, then run VerticalAppSwitcher.exe. Administrator access is not required.
Keep LICENSE and NOTICE with the executable when distributing it.
This executable is not code-signed. Follow your organization's security policy.

Documentation: https://github.com/ness001/vertical-app-switcher#readme
"@
    Set-Content -LiteralPath (Join-Path $publishDirectory 'README.txt') `
        -Value $instructions -Encoding utf8
    $manifest = @{
        schemaVersion = 1
        version = $Version
        runtime = $RuntimeIdentifier
        desktopRuntime = '10.0.0'
    }
    $manifest | ConvertTo-Json | Set-Content `
        -LiteralPath (Join-Path $publishDirectory 'update.json') -Encoding utf8
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksum `
        -Value "$hash  $packageName.zip" -Encoding ascii
}
finally {
    # Remove only the unique staging folder created by this invocation.
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

Write-Host "Created $archive"
Write-Host "Created $checksum"
