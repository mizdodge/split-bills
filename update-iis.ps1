[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SiteName = 'SplitBill',
    [string]$PackagePublishPath = (Join-Path $PSScriptRoot 'publish'),
    [string]$TargetPublishPath,
    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from Windows PowerShell as Administrator.'
}

Import-Module WebAdministration

function Resolve-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Invoke-Robocopy([string[]]$Arguments, [string]$Name) {
    & robocopy.exe @Arguments
    if ($LASTEXITCODE -gt 7) { throw "$Name failed with robocopy exit code $LASTEXITCODE." }
}

$source = Resolve-FullPath $PackagePublishPath
if (-not (Test-Path -LiteralPath (Join-Path $source 'web.config') -PathType Leaf)) {
    throw "Package publish path is missing web.config: $source"
}

if ([string]::IsNullOrWhiteSpace($TargetPublishPath)) {
    $site = Get-Website -Name $SiteName -ErrorAction Stop
    $TargetPublishPath = $site.PhysicalPath
}
$target = Resolve-FullPath $TargetPublishPath
if (-not (Test-Path -LiteralPath $target -PathType Container)) {
    throw "Target publish path does not exist: $target"
}
if ([StringComparer]::OrdinalIgnoreCase.Equals($source.TrimEnd('\'), $target.TrimEnd('\'))) {
    throw 'Package and target publish paths must be different. Extract the new package beside the existing installation.'
}
if (-not (Test-Path -LiteralPath (Join-Path $target 'web.config') -PathType Leaf)) {
    throw "Target publish path is missing web.config: $target"
}

$targetDirectory = [IO.DirectoryInfo]$target
$targetParent = $targetDirectory.Parent.FullName
$backupBase = if ([string]::IsNullOrWhiteSpace($BackupRoot)) { Join-Path $targetParent 'publish-backups' } else { [IO.Path]::GetFullPath($BackupRoot) }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $backupBase "$SiteName-$stamp"

if (-not $PSCmdlet.ShouldProcess($target, "Back up and update IIS site $SiteName")) {
    Write-Output "IIS_UPDATE_WHATIF source=$source target=$target backup=$backup"
    return
}
New-Item -ItemType Directory -Path $backupBase -Force | Out-Null
if (Test-Path -LiteralPath $backup) { throw "Backup path already exists: $backup" }
New-Item -ItemType Directory -Path $backup -Force | Out-Null

$offlinePath = Join-Path $target 'app_offline.htm'
$offlineWasPresent = Test-Path -LiteralPath $offlinePath -PathType Leaf
$deploymentStarted = $false
$appPoolStopped = $false

try {
    if (-not $offlineWasPresent) {
        [IO.File]::WriteAllText($offlinePath, '<!doctype html><html><head><meta charset="utf-8"><title>SplitBill maintenance</title></head><body>SplitBill maintenance in progress.</body></html>')
    }
    Start-Sleep -Seconds 2

    Stop-WebAppPool -Name $SiteName -ErrorAction Stop
    $appPoolStopped = $true
    Invoke-Robocopy @($target, $backup, '/E', '/COPYALL', '/R:2', '/W:1') 'Backup'

    $deploymentStarted = $true
    $targetAppData = Join-Path $target 'App_Data'
    $targetSettings = Join-Path $target 'appsettings.json'
    # Exclude by directory name so robocopy cannot mirror the package's empty
    # App_Data over live SQLite, receipts, proofs, keys, or push subscriptions.
    Invoke-Robocopy @($source, $target, '/MIR', '/COPY:DAT', '/R:2', '/W:1', '/XD', 'App_Data', '/XF', 'appsettings.json', 'app_offline.htm') 'Application update'

    if (-not (Test-Path -LiteralPath $targetAppData -PathType Container)) {
        throw 'Application update removed or failed to preserve target App_Data.'
    }
    if (-not (Test-Path -LiteralPath $targetSettings -PathType Leaf)) {
        throw 'Application update removed target appsettings.json.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $target 'web.config') -PathType Leaf)) {
        throw 'Application update did not leave web.config in the target.'
    }

    & icacls.exe $targetAppData /grant "IIS AppPool\${SiteName}:(OI)(CI)M" /T
    if ($LASTEXITCODE -ne 0) { throw 'Failed to preserve application-pool Modify permission on App_Data.' }
}
catch {
    $failure = $_
    if ($deploymentStarted -and (Test-Path -LiteralPath $backup -PathType Container)) {
        try {
            Invoke-Robocopy @($backup, $target, '/MIR', '/COPYALL', '/R:2', '/W:1') 'Rollback'
        }
        catch {
            throw "Update failed and automatic rollback also failed. Original: $($failure.Exception.Message); rollback: $($_.Exception.Message). Backup: $backup"
        }
    }
    throw $failure
}
finally {
    # Remove only the maintenance marker created by this run, and do it before
    # starting the pool so IIS can immediately activate the updated application.
    if (-not $offlineWasPresent -and (Test-Path -LiteralPath $offlinePath -PathType Leaf)) {
        [IO.File]::Delete($offlinePath)
    }
    if ($appPoolStopped) {
        Start-WebAppPool -Name $SiteName -ErrorAction Stop
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 500
            $poolState = (Get-WebAppPoolState -Name $SiteName -ErrorAction Stop).Value
        } while ($poolState -ne 'Started' -and (Get-Date) -lt $deadline)
        if ($poolState -ne 'Started') { throw "IIS app pool did not reach Started state: $SiteName ($poolState)." }
    }
}

Write-Output "IIS_UPDATE_COMPLETE site=$SiteName target=$target backup=$backup"
