[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$BackupZip,
    [Parameter(Mandatory)][string]$PublishPath,
    [string]$PackagePublishPath = (Join-Path $PSScriptRoot 'publish'),
    [string]$SiteName = 'SplitBill',
    [switch]$Migration
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from Windows PowerShell as Administrator.' }

Import-Module WebAdministration
Add-Type -AssemblyName System.IO.Compression.FileSystem

function FullPath([string]$Path) { return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path) }
function SafeArchivePath([string]$Path) { return -not [IO.Path]::IsPathRooted($Path) -and $Path -notmatch '(^|[\\/])\.\.([\\/]|$)' -and $Path -ne 'manifest.json' }
function CopyTree([string]$Source, [string]$Target) {
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    & robocopy.exe $Source $Target /MIR /COPY:DAT /R:2 /W:1 | Out-Host
    if ($LASTEXITCODE -gt 7) { throw "Copy failed with robocopy exit code $LASTEXITCODE." }
}

$zip = FullPath $BackupZip
$target = FullPath $PublishPath
$package = FullPath $PackagePublishPath
if (-not (Test-Path -LiteralPath (Join-Path $package 'Splitbill.dll') -PathType Leaf)) { throw "Package publish path is missing Splitbill.dll: $package" }
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Backup ZIP not found: $zip" }
if (-not (Test-Path -LiteralPath (Join-Path $target 'web.config') -PathType Leaf)) { throw "Publish path is missing web.config: $target" }

$archive = [IO.Compression.ZipFile]::OpenRead($zip)
$extract = Join-Path ([IO.DirectoryInfo]$target).Parent.FullName ('.restore-' + [Guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path ([IO.DirectoryInfo]$target).Parent.FullName 'restore-backups'
$offlinePath = Join-Path $target 'app_offline.htm'
$offlineWasPresent = Test-Path -LiteralPath $offlinePath -PathType Leaf
$appPoolStopped = $false
$backupPath = $null
try {
    $manifestEntry = $archive.GetEntry('manifest.json')
    if ($null -eq $manifestEntry) { throw 'Backup manifest is missing.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.FormatVersion -ne 1 -or $null -eq $manifest.Entries -or @($manifest.Entries).Count -eq 0) { throw 'Backup manifest is invalid.' }
    $total = [int64]0
    foreach ($item in @($manifest.Entries)) {
        if (-not (SafeArchivePath $item.Path) -or $item.Length -lt 0 -or $item.Length -gt 2147483648 -or $total -gt 2147483648 - $item.Length) { throw "Unsafe or oversized backup entry: $($item.Path)" }
        $entry = $archive.GetEntry($item.Path)
        if ($null -eq $entry -or $entry.Length -ne $item.Length) { throw "Backup entry mismatch: $($item.Path)" }
        $total += $item.Length
    }
    $dbEntry = $archive.GetEntry('database/splitbill.db')
    if ($null -eq $dbEntry) { throw 'Backup does not contain database/splitbill.db.' }

    if (-not $PSCmdlet.ShouldProcess($target, "Restore SplitBill backup (Migration=$Migration)")) { Write-Output "RESTORE_WHATIF target=$target"; return }
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    foreach ($item in @($manifest.Entries)) {
        $destination = Join-Path $extract $item.Path
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        $entry = $archive.GetEntry($item.Path)
        $input = $entry.Open(); $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $item.Sha256) { throw "Checksum mismatch after extraction: $($item.Path)" }
    }

    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $backupPath = Join-Path $backupRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    if (-not $offlineWasPresent) { [IO.File]::WriteAllText($offlinePath, '<!doctype html><html><body>SplitBill restore in progress.</body></html>') }
    Start-Sleep -Seconds 2
    Stop-WebAppPool -Name $SiteName -ErrorAction Stop
    $appPoolStopped = $true
    CopyTree (Join-Path $target 'App_Data') (Join-Path $backupPath 'App_Data')

    Push-Location $target
    try { $targetIdOutput = & dotnet (Join-Path $package 'Splitbill.dll') --print-installation-id } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the target installation ID before restore.' }
    $targetInstallationId = ($targetIdOutput | Select-String '^SPLITBILL_INSTALLATION_ID=' | Select-Object -First 1).Line -replace '^SPLITBILL_INSTALLATION_ID=', ''
    if (-not $Migration -and [string]::IsNullOrWhiteSpace($manifest.InstallationId)) { throw 'Backup manifest has no installation ID; choose -Migration only after reviewing the source.' }
    if (-not $Migration -and $targetInstallationId -ne $manifest.InstallationId) { throw "Installation ID mismatch. Use -Migration for a different server. Source=$($manifest.InstallationId) Target=$targetInstallationId" }

    $targetAppData = Join-Path $target 'App_Data'
    Copy-Item -LiteralPath (Join-Path $extract 'database/splitbill.db') -Destination (Join-Path $targetAppData 'splitbill.db') -Force
    if (Test-Path -LiteralPath (Join-Path $extract 'files/receipts') -PathType Container) { CopyTree (Join-Path $extract 'files/receipts') (Join-Path $targetAppData 'receipts') } else { New-Item -ItemType Directory -Path (Join-Path $targetAppData 'receipts') -Force | Out-Null }
    if (Test-Path -LiteralPath (Join-Path $extract 'files/payment-proofs') -PathType Container) { CopyTree (Join-Path $extract 'files/payment-proofs') (Join-Path $targetAppData 'payment-proofs') } else { New-Item -ItemType Directory -Path (Join-Path $targetAppData 'payment-proofs') -Force | Out-Null }
    if (-not $Migration -and (Test-Path -LiteralPath (Join-Path $extract 'keys/data-protection-keys') -PathType Container)) { CopyTree (Join-Path $extract 'keys/data-protection-keys') (Join-Path $targetAppData 'data-protection-keys') }
    else {
        $keyPath = Join-Path $targetAppData 'data-protection-keys'
        New-Item -ItemType Directory -Path $keyPath -Force | Out-Null
        Get-ChildItem -LiteralPath $keyPath -Force | Remove-Item -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $targetAppData 'splitbill.db') -PathType Leaf)) { throw 'Restore did not leave App_Data/splitbill.db.' }
    if ($Migration) {
        Push-Location $target
        try { & dotnet (Join-Path $package 'Splitbill.dll') --reset-machine-secrets } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw 'Machine-bound secret reset failed after migration restore.' }
    }
}
catch {
    $failure = $_
    if ($null -ne $backupPath -and (Test-Path -LiteralPath (Join-Path $backupPath 'App_Data') -PathType Container)) {
        try { CopyTree (Join-Path $backupPath 'App_Data') (Join-Path $target 'App_Data') } catch { throw "Restore failed and rollback failed. Original: $($failure.Exception.Message); rollback: $($_.Exception.Message)" }
    }
    throw $failure
}
finally {
    if ($archive) { $archive.Dispose() }
    if (Test-Path -LiteralPath $extract -PathType Container) { Remove-Item -LiteralPath $extract -Recurse -Force }
    if (-not $offlineWasPresent -and (Test-Path -LiteralPath $offlinePath -PathType Leaf)) { Remove-Item -LiteralPath $offlinePath -Force }
    if ($appPoolStopped) {
        Start-WebAppPool -Name $SiteName -ErrorAction Stop
        $deadline = (Get-Date).AddSeconds(20)
        do { Start-Sleep -Milliseconds 500; $poolState = (Get-WebAppPoolState -Name $SiteName -ErrorAction Stop).Value } while ($poolState -ne 'Started' -and (Get-Date) -lt $deadline)
        if ($poolState -ne 'Started') { throw "IIS app pool did not reach Started state: $SiteName ($poolState)." }
    }
}

Write-Output "RESTORE_COMPLETE target=$target backup=$backupPath migration=$Migration"
