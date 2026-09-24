<#
.SYNOPSIS
    Builds LinTv.Api for linux-x64 and uploads it to /tmp/lintv on the target host.

.DESCRIPTION
    Connection settings are resolved in this order (first non-empty wins):
      1. Script parameters (-HostName, -User, -Password, -Port)
      2. Environment variables LINTV_HOST, LINTV_USER, LINTV_PASSWORD, LINTV_PORT
      3. publish.settings.json next to this script (gitignored; see publish.settings.example.json)

    With a password, the upload uses the Posh-SSH module (Windows OpenSSH cannot take a
    password non-interactively). Without one, it falls back to ssh/scp with key auth.

.EXAMPLE
    .\publish.ps1
.EXAMPLE
    $env:LINTV_PASSWORD = '...'; .\publish.ps1 -HostName 192.168.1.50 -User scott
#>
[CmdletBinding()]
param(
    [string]$HostName,
    [string]$User,
    [string]$Password,
    [int]$Port,
    [string]$RemoteDir = '/tmp/lintv',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'linux-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Resolve-Setting([string]$value, [string]$envName, [string]$fileKey) {
    if ($value) { return $value }
    $envValue = [Environment]::GetEnvironmentVariable($envName)
    if ($envValue) { return $envValue }
    if ($script:settings -and $script:settings.$fileKey) { return [string]$script:settings.$fileKey }
    return $null
}

$settingsPath = Join-Path $root 'publish.settings.json'
$script:settings = $null
if (Test-Path $settingsPath) {
    $script:settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
}

$HostName = Resolve-Setting $HostName 'LINTV_HOST' 'Host'
$User = Resolve-Setting $User 'LINTV_USER' 'User'
$Password = Resolve-Setting $Password 'LINTV_PASSWORD' 'Password'
$portSetting = Resolve-Setting $(if ($Port) { "$Port" } else { $null }) 'LINTV_PORT' 'Port'
$Port = if ($portSetting) { [int]$portSetting } else { 22 }

if (-not $HostName -or -not $User) {
    throw "Host and user are required: pass -HostName/-User, set LINTV_HOST/LINTV_USER, or create $settingsPath"
}

# --- Build ---------------------------------------------------------------------------
$publishDir = Join-Path $root 'publish'
$archive = Join-Path $root 'lintv.tar.gz'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $archive) { Remove-Item $archive -Force }

Write-Host "Publishing LinTv.Api ($Configuration, $Runtime)..." -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'LinTv.Api') -c $Configuration -r $Runtime --self-contained false -o $publishDir -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# One archive = one transfer, and extraction on the host replaces RemoteDir wholesale.
& tar -czf $archive -C $publishDir .
if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }

$remoteArchive = '/tmp/lintv.tar.gz'
# Empty RemoteDir rather than recreate it: under /opt the parent is root-owned, but the
# directory itself (and its setgid group) belongs to the deploy user.
$extract = "mkdir -p '$RemoteDir' && find '$RemoteDir' -mindepth 1 -delete && tar -xzf '$remoteArchive' -C '$RemoteDir' --no-same-owner && rm -f '$remoteArchive'"

# --- Upload --------------------------------------------------------------------------
Write-Host "Uploading to $User@${HostName}:$RemoteDir..." -ForegroundColor Cyan
if ($Password) {
    if (-not (Get-Module -ListAvailable Posh-SSH)) {
        throw "Password auth needs the Posh-SSH module: Install-Module Posh-SSH -Scope CurrentUser"
    }
    Import-Module Posh-SSH

    $secure = ConvertTo-SecureString $Password -AsPlainText -Force
    $credential = New-Object System.Management.Automation.PSCredential($User, $secure)

    # -AcceptKey trusts the host key on first connect and pins it afterwards.
    Set-SCPItem -ComputerName $HostName -Port $Port -Credential $credential -AcceptKey `
        -Path $archive -Destination '/tmp' -NewName 'lintv.tar.gz'

    $session = New-SSHSession -ComputerName $HostName -Port $Port -Credential $credential -AcceptKey
    try {
        $result = Invoke-SSHCommand -SSHSession $session -Command $extract
        if ($result.ExitStatus -ne 0) { throw "Remote extract failed: $($result.Error)" }
    }
    finally {
        Remove-SSHSession -SSHSession $session | Out-Null
    }
}
else {
    & scp -P $Port $archive "$User@${HostName}:$remoteArchive"
    if ($LASTEXITCODE -ne 0) { throw "scp failed ($LASTEXITCODE)" }

    & ssh -p $Port "$User@$HostName" $extract
    if ($LASTEXITCODE -ne 0) { throw "Remote extract failed ($LASTEXITCODE)" }
}

Remove-Item $archive -Force
Write-Host "Done. Deployed to ${HostName}:$RemoteDir" -ForegroundColor Green
