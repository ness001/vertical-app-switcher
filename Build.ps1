param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'VerticalAppSwitcher.csproj'
$icon = Join-Path $PSScriptRoot 'AppIcon.ico'
$publishDirectory = Join-Path `
    $PSScriptRoot `
    (Join-Path 'bin\publish' $RuntimeIdentifier)
$publishedExecutable = Join-Path $publishDirectory 'VerticalAppSwitcher.exe'
$output = Join-Path $PSScriptRoot 'VerticalAppSwitcher.exe'

& (Join-Path $PSScriptRoot 'GenerateIcon.ps1') -OutputPath $icon

& dotnet publish `
    $project `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained false `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "The published executable was not found: $publishedExecutable"
}

$restartAfterBuild = $false
if (Test-Path -LiteralPath $output -PathType Leaf) {
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    $runningInstances = @(
        Get-CimInstance Win32_Process `
            -Filter "Name = 'VerticalAppSwitcher.exe'" |
            Where-Object {
                $_.ExecutablePath `
                    -and [string]::Equals(
                        $_.ExecutablePath,
                        $resolvedOutput,
                        [StringComparison]::OrdinalIgnoreCase)
            }
    )

    if ($runningInstances.Count -gt 1) {
        throw "Multiple running instances use $resolvedOutput."
    }

    if ($runningInstances.Count -eq 1) {
        $runningProcessId = [int]$runningInstances[0].ProcessId
        $shutdownRequest = Start-Process `
            -FilePath $output `
            -ArgumentList '--shutdown' `
            -PassThru `
            -Wait
        if ($shutdownRequest.ExitCode -ne 0) {
            throw "The running switcher did not accept the shutdown request."
        }

        $runningProcess = Get-Process `
            -Id $runningProcessId `
            -ErrorAction SilentlyContinue
        if ($null -ne $runningProcess `
            -and -not $runningProcess.WaitForExit(10000)) {
            $message = "The running switcher did not exit normally. "
            $message += "Use its tray-menu Exit command and rerun the build."
            throw $message
        }

        $restartAfterBuild = $true
    }
}

try {
    Copy-Item -LiteralPath $publishedExecutable -Destination $output -Force
}
catch {
    if ($restartAfterBuild -and (Test-Path -LiteralPath $output -PathType Leaf)) {
        Start-Process -FilePath $output -ArgumentList '--background'
    }

    $message = "Could not replace $output. Exit Vertical App Switcher "
    $message += "and rerun the build. " + $_.Exception.Message
    throw $message
}

if ($restartAfterBuild) {
    $restartedProcess = Start-Process `
        -FilePath $output `
        -ArgumentList '--background' `
        -PassThru
    Start-Sleep -Milliseconds 1000
    $restartedProcess.Refresh()
    if ($restartedProcess.HasExited) {
        $message = "The rebuilt switcher exited during restart with code "
        $message += $restartedProcess.ExitCode.ToString() + "."
        throw $message
    }
}

Write-Host "Built $output for $RuntimeIdentifier"
