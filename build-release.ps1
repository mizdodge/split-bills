[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot = '',
    [switch]$UserEmailUpdate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { [IO.Path]::GetFullPath((Get-Location).Path) } else { [IO.Path]::GetFullPath($PSScriptRoot) }
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$requestedOutputValue = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $artifactsRoot } else { $OutputRoot }
$requestedOutput = [IO.Path]::GetFullPath($requestedOutputValue)
$separator = [IO.Path]::DirectorySeparatorChar

function Test-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    return $fullPath.StartsWith($fullParent + $separator, [StringComparison]::OrdinalIgnoreCase)
}

if ($requestedOutput -ne $artifactsRoot -and -not (Test-ChildPath $requestedOutput $artifactsRoot)) {
    throw "OutputRoot must be the repository artifacts directory or one of its children: $artifactsRoot"
}
New-Item -ItemType Directory -Path $requestedOutput -Force | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageName = if ($UserEmailUpdate) { "SplitBill-Server-$stamp (update user email)" } else { "SplitBill-Server-$stamp" }
$packageRoot = Join-Path $requestedOutput $packageName
$zipPath = Join-Path $requestedOutput "$packageName.zip"
if ((Test-Path -LiteralPath $packageRoot -PathType Container) -or (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw "Release output already exists: $packageName. Wait one second and retry."
}
if (-not (Test-ChildPath $packageRoot $artifactsRoot)) {
    throw 'Staging path safety check failed.'
}

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "== $Name =="
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

$publishRoot = Join-Path $packageRoot 'publish'
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$restore = { & dotnet restore (Join-Path $repoRoot 'Splitbill.sln') }
Invoke-Step 'Restore' $restore

$build = { & dotnet build (Join-Path $repoRoot 'Splitbill.sln') -c $Configuration --no-restore }
Invoke-Step 'Build' $build

$test = { & dotnet test (Join-Path $repoRoot 'Tests\Splitbill.Tests.csproj') -c $Configuration --no-build --no-restore }
Invoke-Step 'Test' $test

$publishArgs = @('publish', (Join-Path $repoRoot 'Splitbill.csproj'), '-c', $Configuration, '--no-restore', '-o', $publishRoot)
if (-not [string]::IsNullOrWhiteSpace($Runtime)) { $publishArgs += @('-r', $Runtime) }
$publish = { & dotnet @publishArgs }
Invoke-Step 'Publish' $publish

if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'web.config') -PathType Leaf)) {
    throw 'Fresh publish did not produce publish/web.config.'
}
New-Item -ItemType Directory -Path (Join-Path $publishRoot 'App_Data\receipts') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $publishRoot 'App_Data\data-protection-keys') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'setup-iis.ps1') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'update-iis.ps1') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'restore-splitbill.ps1') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'project_guide.md') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'documentation') -Destination (Join-Path $packageRoot 'documentation') -Recurse -Force

$settingsPath = Join-Path $publishRoot 'appsettings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$topLevelKeys = @($settings.PSObject.Properties.Name | Sort-Object)
$expectedTopLevelKeys = @('AllowedHosts', 'ConnectionStrings', 'Logging')
if (@(Compare-Object $topLevelKeys $expectedTopLevelKeys).Count -gt 0) {
    throw "Published appsettings.json has unexpected top-level keys: $($topLevelKeys -join ', ')"
}
if (-not $settings.ConnectionStrings.DefaultConnection -or $settings.ConnectionStrings.DefaultConnection -notmatch 'App_Data[/\\]splitbill\.db') {
    throw 'Published appsettings.json does not point to the App_Data SQLite database.'
}

$stagedFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse)
$forbidden = @(
    $stagedFiles | Where-Object { $_.Name -match '\.(db|db-wal|db-shm|log)$' }
    $stagedFiles | Where-Object { $_.Name -in @('.env', 'appsettings.Local.json') }
    $stagedFiles | Where-Object { $_.FullName -match '[\\/]App_Data[\\/](backups|receipts|data-protection-keys)[\\/]' }
)
if ($forbidden.Count -gt 0) {
    throw "Release contains forbidden runtime data: $($forbidden.FullName -join ', ')"
}
$settingsText = Get-Content -LiteralPath $settingsPath -Raw
if ($settingsText -match '(?i)OpenAI|ApiKey|ProtectedApiKey|AzureOpenAI') {
    throw 'Published appsettings.json contains an AI credential/configuration key.'
}

$manifestPath = Join-Path $packageRoot 'SHA256SUMS.txt'
$manifestLines = foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object FullName -ne $manifestPath | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
    "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)  *$relative"
}
Set-Content -LiteralPath $manifestPath -Value $manifestLines -Encoding UTF8

Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$zipSize = (Get-Item -LiteralPath $zipPath).Length
Write-Host "RELEASE_PACKAGE=$zipPath"
Write-Host "RELEASE_SIZE_BYTES=$zipSize"
Write-Host "RELEASE_SHA256=$zipHash"
