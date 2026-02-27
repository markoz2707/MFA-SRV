#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or uninstalls the MfaSrv DC Agent on a domain controller.

.DESCRIPTION
    This script performs a full deployment of the MfaSrv DC Agent, including:
      - Copying service binaries to Program Files
      - Copying MfaSrvLsaAuth.dll to System32
      - Registering the LSA authentication package in the registry
      - Creating the configuration directory in ProgramData
      - Writing appsettings.json with supplied parameters
      - Installing and starting the Windows service

    The -Uninstall switch reverses all of the above.

    WARNING: Registering or unregistering the LSA authentication package requires
    a system restart to take full effect. The script will warn but will NOT
    automatically restart the machine.

.PARAMETER CentralServerUrl
    The gRPC endpoint URL of the MfaSrv central server.

.PARAMETER AgentId
    A unique identifier for this DC agent instance. If omitted, the machine
    name is used.

.PARAMETER FailoverMode
    Behavior when the central server is unreachable. Valid values: FailOpen, FailClosed.
    Default: FailOpen.

.PARAMETER SourcePath
    Path to the build output directory containing published binaries.
    Defaults to .\publish.

.PARAMETER Uninstall
    When specified, removes the MfaSrv DC Agent from the system.

.EXAMPLE
    .\Install-DcAgent.ps1 -CentralServerUrl "https://mfasrv:5081" -AgentId "DC01"

.EXAMPLE
    .\Install-DcAgent.ps1 -Uninstall
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $false)]
    [string]$CentralServerUrl = 'https://mfasrv-server:5081',

    [Parameter(Mandatory = $false)]
    [string]$AgentId = $env:COMPUTERNAME,

    [Parameter(Mandatory = $false)]
    [ValidateSet('FailOpen', 'FailClosed')]
    [string]$FailoverMode = 'FailOpen',

    [Parameter(Mandatory = $false)]
    [string]$SourcePath = (Join-Path $PSScriptRoot 'publish'),

    [Parameter(Mandatory = $false)]
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
$ServiceName        = 'MfaSrvDcAgent'
$ServiceDisplayName = 'MfaSrv DC Agent'
$ServiceDescription = 'MfaSrv domain controller agent - relays authentication requests to central MFA server and enforces MFA policy via the LSA authentication package.'
$InstallDir         = Join-Path $env:ProgramFiles 'MfaSrv\DcAgent'
$ConfigDir          = Join-Path $env:ProgramData  'MfaSrv\DcAgent'
$System32Dir        = Join-Path $env:SystemRoot   'System32'
$LsaDllName         = 'MfaSrvLsaAuth.dll'
$LsaRegPath         = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'
$LsaPackageName     = 'MfaSrvLsaAuth'
$ExeName            = 'MfaSrv.DcAgent.exe'

# ---------------------------------------------------------------------------
# Helper: Verify administrator
# ---------------------------------------------------------------------------
function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]$identity
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run as Administrator.'
    }
}

# ---------------------------------------------------------------------------
# Helper: Stop and remove the Windows service
# ---------------------------------------------------------------------------
function Remove-MfaSrvService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Verbose "Service '$ServiceName' not found; nothing to remove."
        return
    }

    if ($svc.Status -ne 'Stopped') {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop service')) {
            Write-Host "Stopping service '$ServiceName'..."
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    }

    if ($PSCmdlet.ShouldProcess($ServiceName, 'Delete service')) {
        Write-Host "Removing service '$ServiceName'..."
        & sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to delete service '$ServiceName' (exit code $LASTEXITCODE)."
        }
    }
}

# ---------------------------------------------------------------------------
# Helper: Unregister LSA authentication package
# ---------------------------------------------------------------------------
function Unregister-LsaPackage {
    if ($PSCmdlet.ShouldProcess($LsaPackageName, 'Unregister LSA authentication package')) {
        $current = (Get-ItemProperty -Path $LsaRegPath -Name 'Authentication Packages' -ErrorAction SilentlyContinue).'Authentication Packages'
        if ($null -ne $current) {
            $updated = @($current | Where-Object { $_ -ne $LsaPackageName -and $_ -ne '' })
            if ($updated.Count -eq 0) { $updated = @('') }
            Set-ItemProperty -Path $LsaRegPath -Name 'Authentication Packages' -Value $updated
            Write-Host "Removed '$LsaPackageName' from LSA Authentication Packages."
        }
    }
}

# ---------------------------------------------------------------------------
# Helper: Register LSA authentication package
# ---------------------------------------------------------------------------
function Register-LsaPackage {
    if ($PSCmdlet.ShouldProcess($LsaPackageName, 'Register LSA authentication package')) {
        $current = (Get-ItemProperty -Path $LsaRegPath -Name 'Authentication Packages' -ErrorAction SilentlyContinue).'Authentication Packages'
        if ($null -eq $current) {
            $current = @()
        }
        if ($current -notcontains $LsaPackageName) {
            $updated = @($current) + @($LsaPackageName)
            Set-ItemProperty -Path $LsaRegPath -Name 'Authentication Packages' -Value $updated
            Write-Host "Registered '$LsaPackageName' in LSA Authentication Packages."
        }
        else {
            Write-Verbose "'$LsaPackageName' is already registered."
        }
    }
}

# ===========================================================================
# Main
# ===========================================================================

Assert-Administrator

if ($Uninstall) {
    # ----- Uninstall flow -----
    Write-Host '=== MfaSrv DC Agent Uninstall ===' -ForegroundColor Cyan

    Remove-MfaSrvService
    Unregister-LsaPackage

    # Remove LSA DLL from System32
    $lsaDst = Join-Path $System32Dir $LsaDllName
    if (Test-Path $lsaDst) {
        if ($PSCmdlet.ShouldProcess($lsaDst, 'Delete LSA DLL')) {
            Remove-Item -Path $lsaDst -Force
            Write-Host "Deleted $lsaDst"
        }
    }

    # Remove install directory
    if (Test-Path $InstallDir) {
        if ($PSCmdlet.ShouldProcess($InstallDir, 'Delete install directory')) {
            Remove-Item -Path $InstallDir -Recurse -Force
            Write-Host "Deleted $InstallDir"
        }
    }

    # Preserve config directory by default (contains local state)
    if (Test-Path $ConfigDir) {
        Write-Warning "Configuration directory preserved at: $ConfigDir"
        Write-Warning 'Delete it manually if you want a clean removal.'
    }

    Write-Host ''
    Write-Warning '*** A SYSTEM RESTART is required to fully unload the LSA authentication package. ***'
    Write-Host ''
    Write-Host 'Uninstall complete.' -ForegroundColor Green
    return
}

# ----- Install flow -----
Write-Host '=== MfaSrv DC Agent Install ===' -ForegroundColor Cyan

# Validate source path
if (-not (Test-Path (Join-Path $SourcePath $ExeName))) {
    throw "Cannot find '$ExeName' in '$SourcePath'. Please build with: dotnet publish -c Release -r win-x64 --self-contained false"
}
if (-not (Test-Path (Join-Path $SourcePath $LsaDllName))) {
    throw "Cannot find '$LsaDllName' in '$SourcePath'. Please build the native project MfaSrv.DcAgent.Native in Release|x64 configuration."
}

# 1. Stop existing service if running
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existingSvc -and $existingSvc.Status -ne 'Stopped') {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop existing service')) {
        Write-Host "Stopping existing service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force
    }
}

# 2. Create directories
foreach ($dir in @($InstallDir, $ConfigDir)) {
    if (-not (Test-Path $dir)) {
        if ($PSCmdlet.ShouldProcess($dir, 'Create directory')) {
            New-Item -Path $dir -ItemType Directory -Force | Out-Null
            Write-Host "Created directory: $dir"
        }
    }
}

# 3. Copy service binaries
if ($PSCmdlet.ShouldProcess($InstallDir, 'Copy service binaries')) {
    Write-Host "Copying binaries to $InstallDir..."
    $filesToCopy = Get-ChildItem -Path $SourcePath -File | Where-Object { $_.Name -ne $LsaDllName }
    foreach ($file in $filesToCopy) {
        Copy-Item -Path $file.FullName -Destination $InstallDir -Force
    }
}

# 4. Copy LSA DLL to System32
$lsaSrc = Join-Path $SourcePath $LsaDllName
$lsaDst = Join-Path $System32Dir $LsaDllName
if ($PSCmdlet.ShouldProcess($lsaDst, 'Copy LSA DLL to System32')) {
    Write-Host "Copying $LsaDllName to $System32Dir..."
    Copy-Item -Path $lsaSrc -Destination $lsaDst -Force
}

# 5. Write appsettings.json
$appSettingsSrc = Join-Path $SourcePath 'appsettings.json'
$appSettingsDst = Join-Path $ConfigDir 'appsettings.json'

if ($PSCmdlet.ShouldProcess($appSettingsDst, 'Write configuration')) {
    if (Test-Path $appSettingsDst) {
        Write-Host 'Existing appsettings.json found; updating in place...'
        $config = Get-Content -Path $appSettingsDst -Raw | ConvertFrom-Json
    }
    else {
        if (Test-Path $appSettingsSrc) {
            $config = Get-Content -Path $appSettingsSrc -Raw | ConvertFrom-Json
        }
        else {
            throw "No appsettings.json template found in '$SourcePath'."
        }
    }

    $config.DcAgent.CentralServerUrl = $CentralServerUrl
    $config.DcAgent.AgentId          = $AgentId
    $config.DcAgent.FailoverMode     = $FailoverMode
    $config.DcAgent.CacheDbPath      = Join-Path $ConfigDir 'dcagent_cache.db'

    $config | ConvertTo-Json -Depth 10 | Set-Content -Path $appSettingsDst -Encoding UTF8
    Write-Host "Configuration written to $appSettingsDst"
}

# 6. Register LSA authentication package
Register-LsaPackage

# 7. Create / update Windows service
$exePath = Join-Path $InstallDir $ExeName
if ($null -eq (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Create Windows service')) {
        Write-Host "Creating service '$ServiceName'..."
        New-Service -Name $ServiceName `
                    -BinaryPathName "`"$exePath`" --contentRoot `"$ConfigDir`"" `
                    -DisplayName $ServiceDisplayName `
                    -Description $ServiceDescription `
                    -StartupType Automatic | Out-Null

        # Configure service recovery: restart on first two failures
        & sc.exe failure $ServiceName reset=86400 actions=restart/30000/restart/30000// | Out-Null
    }
}
else {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Update service binary path')) {
        & sc.exe config $ServiceName binPath= "`"$exePath`" --contentRoot `"$ConfigDir`"" | Out-Null
    }
}

# 8. Start the service
if ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    Write-Host "Starting service '$ServiceName'..."
    Start-Service -Name $ServiceName
    $svc = Get-Service -Name $ServiceName
    Write-Host "Service status: $($svc.Status)"
}

Write-Host ''
Write-Warning '*** A SYSTEM RESTART is recommended to activate the LSA authentication package. ***'
Write-Warning 'Logons will not be intercepted by MfaSrv until the restart completes.'
Write-Host ''
Write-Host 'Install complete.' -ForegroundColor Green
