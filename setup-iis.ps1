param(
    [string]$SiteName = 'SplitBill',
    [int]$Port = 8080,
    [string]$BindAddress = '127.0.0.1',
    [string]$PublishPath = (Join-Path $PSScriptRoot 'publish'),
    [string]$HostingBundleInstaller
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from Windows PowerShell as Administrator.'
}

$publishRoot = (Resolve-Path -LiteralPath $PublishPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'web.config'))) {
    throw 'Publish output is missing. Run: dotnet publish Splitbill.csproj -c Release -o publish'
}

$logDirectory = Join-Path $publishRoot 'App_Data\setup-logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Start-Transcript -Path (Join-Path $logDirectory 'iis-setup.log') -Force
try {
    $modulePath = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    if (-not (Test-Path -LiteralPath $modulePath)) {
        if (-not $HostingBundleInstaller -or -not (Test-Path -LiteralPath $HostingBundleInstaller)) {
            throw 'ASP.NET Core Hosting Bundle is missing. Pass -HostingBundleInstaller with the official downloaded installer path.'
        }
        $installerLog = Join-Path $logDirectory 'hosting-install.log'
        Write-Output 'Installing the ASP.NET Core Hosting Bundle...'
        $installerProcess = Start-Process -FilePath $HostingBundleInstaller -WindowStyle Hidden -Wait -PassThru -ArgumentList @('/install', '/quiet', '/norestart', '/log', ('"{0}"' -f $installerLog))
        if ($installerProcess.ExitCode -notin 0, 3010) {
            throw "Hosting Bundle installer exited with $($installerProcess.ExitCode). See $installerLog."
        }
        if (-not (Test-Path -LiteralPath $modulePath)) { throw 'Hosting Bundle completed without installing AspNetCoreModuleV2.' }
        if ((Get-Service WAS).Status -eq 'Running') { Stop-Service WAS -Force }
    }

    Start-Service W3SVC
    Import-Module WebAdministration
    if (-not (Test-Path "IIS:\AppPools\$SiteName")) { New-WebAppPool -Name $SiteName | Out-Null }
    Set-ItemProperty "IIS:\AppPools\$SiteName" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$SiteName" -Name enable32BitAppOnWin64 -Value $false
    Set-ItemProperty "IIS:\AppPools\$SiteName" -Name processModel.identityType -Value ApplicationPoolIdentity
    Set-ItemProperty "IIS:\AppPools\$SiteName" -Name processModel.loadUserProfile -Value $true

    $existingSite = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if ($existingSite) {
        if ($existingSite.PhysicalPath -ne $publishRoot) { throw "Existing site $SiteName points to a different directory; it was not changed." }

        $expectedBinding = "${BindAddress}:${Port}:"
        $matchingBinding = Get-WebBinding -Name $SiteName -Protocol 'http' | Where-Object bindingInformation -eq $expectedBinding
        if (-not $matchingBinding) {
            New-WebBinding -Name $SiteName -Protocol 'http' -IPAddress $BindAddress -Port $Port | Out-Null
        }

        Get-WebBinding -Name $SiteName -Protocol 'http' |
            Where-Object { $_.bindingInformation -ne $expectedBinding -and $_.bindingInformation -match ":${Port}:$" } |
            Remove-WebBinding
    }
    else {
        New-Website -Name $SiteName -Port $Port -IPAddress $BindAddress -PhysicalPath $publishRoot -ApplicationPool $SiteName | Out-Null
    }

    if ($BindAddress -ne '127.0.0.1') {
        $firewallRuleName = "$SiteName HTTP $Port"
        $firewallLocalAddress = if ($BindAddress -eq '*') { 'Any' } else { $BindAddress }
        $existingRules = Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue
        if ($existingRules) {
            $existingRules | Set-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -Protocol TCP -LocalAddress $firewallLocalAddress -LocalPort $Port -RemoteAddress Any -Profile Any | Out-Null
        }
        else {
            New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalAddress $firewallLocalAddress -LocalPort $Port -Profile Any | Out-Null
        }
    }

    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name userName -Value ''
    $dataDirectory = Join-Path $publishRoot 'App_Data'
    New-Item -ItemType Directory -Path (Join-Path $dataDirectory 'receipts') -Force | Out-Null
    & icacls.exe $publishRoot /grant "IIS AppPool\${SiteName}:(OI)(CI)RX"
    if ($LASTEXITCODE -ne 0) { throw 'Failed to grant read/execute permission to the application pool.' }
    & icacls.exe $dataDirectory /grant "IIS AppPool\${SiteName}:(OI)(CI)M" /T
    if ($LASTEXITCODE -ne 0) { throw 'Failed to grant runtime-data write permission to the application pool.' }
    if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Started') { Start-WebAppPool -Name $SiteName }
    Start-Website -Name $SiteName
    Write-Output "IIS_SETUP_COMPLETE binding=${BindAddress}:${Port}"
    Get-Website -Name $SiteName | Format-List Name,State,PhysicalPath,Bindings
}
finally {
    Stop-Transcript
}
