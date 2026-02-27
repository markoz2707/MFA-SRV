#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or uninstalls the MfaSrv Endpoint Agent on a Windows workstation or server.

.DESCRIPTION
    This script performs a full deployment of the MfaSrv Endpoint Agent, including:
      - Copying service binaries to Program Files
      - Copying MfaSrvCredentialProvider.dll to System32
      - Registering the Credential Provider COM object (CLSID + InprocServer32)
      - Registering the Credential Provider in the Windows authentication chain
      - Creating the configuration directory in ProgramData
      - Writing appsettings.json with supplied parameters
      - Installing and starting the Windows service

    The -Uninstall switch reverses all of the above.

.PARAMETER CentralServerUrl
    The gRPC endpoint URL of the MfaSrv central server.

.PARAMETER AgentId
    A unique identifier for this endpoint agent instance. If omitted, the machine
    name is used.

.PARAMETER FailoverMode
    Behavior when the central server is unreachable. Valid values: FailOpen, FailClosed.
    Default: FailOpen.

.PARAMETER SourcePath
    Path to the build output directory containing published binaries.
    Defaults to .\publish.

.PARAMETER Uninstall
    When specified, removes the MfaSrv Endpoint Agent from the system.

.EXAMPLE
    .\Install-EndpointAgent.ps1 -CentralServerUrl "https://mfasrv:5081" -AgentId "WKS001"

.EXAMPLE
    .\Install-EndpointAgent.ps1 -Uninstall
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
$ServiceName        = 'MfaSrvEndpointAgent'
$ServiceDisplayName = 'MfaSrv Endpoint Agent'
$ServiceDescription = 'MfaSrv endpoint agent - communicates with the credential provider DLL via named pipe and relays MFA challenges to the central server.'
$InstallDir         = Join-Path $env:ProgramFiles 'MfaSrv\EndpointAgent'
$ConfigDir          = Join-Path $env:ProgramData  'MfaSrv\EndpointAgent'
$System32Dir        = Join-Path $env:SystemRoot   'System32'
$CredProvDllName    = 'MfaSrvCredentialProvider.dll'
$ExeName            = 'MfaSrv.EndpointAgent.exe'

# Credential Provider COM CLSID (matches CredentialProvider.h)
$CredProvCLSID      = '{A0E9E5B0-1234-4567-89AB-CDEF01234567}'
$CredProvRegPath    = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$CredProvCLSID"
$ComClsidRegPath    = "HKCR:\CLSID\$CredProvCLSID"
# HKCR is a merged view; write directly to HKLM for 64-bit
$ComClsidHKLMPath   = "HKLM:\SOFTWARE\Classes\CLSID\$CredProvCLSID"

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
# Helper: Unregister Credential Provider
# ---------------------------------------------------------------------------
function Unregister-CredentialProvider {
    # Remove Credential Provider registration
    if (Test-Path $CredProvRegPath) {
        if ($PSCmdlet.ShouldProcess($CredProvCLSID, 'Unregister Credential Provider')) {
            Remove-Item -Path $CredProvRegPath -Recurse -Force
            Write-Host "Removed Credential Provider registration: $CredProvCLSID"
        }
    }

    # Remove COM CLSID registration
    if (Test-Path $ComClsidHKLMPath) {
        if ($PSCmdlet.ShouldProcess($CredProvCLSID, 'Unregister COM CLSID')) {
            Remove-Item -Path $ComClsidHKLMPath -Recurse -Force
            Write-Host "Removed COM CLSID registration: $CredProvCLSID"
        }
    }
}

# ---------------------------------------------------------------------------
# Helper: Register Credential Provider
# ---------------------------------------------------------------------------
function Register-CredentialProvider {
    $dllPath = Join-Path $System32Dir $CredProvDllName

    # COM InprocServer32 registration
    if ($PSCmdlet.ShouldProcess($CredProvCLSID, 'Register COM CLSID')) {
        $inprocPath = Join-Path $ComClsidHKLMPath 'InprocServer32'

        if (-not (Test-Path $ComClsidHKLMPath)) {
            New-Item -Path $ComClsidHKLMPath -Force | Out-Null
        }
        Set-ItemProperty -Path $ComClsidHKLMPath -Name '(Default)' -Value 'MfaSrv Credential Provider'

        if (-not (Test-Path $inprocPath)) {
            New-Item -Path $inprocPath -Force | Out-Null
        }
        Set-ItemProperty -Path $inprocPath -Name '(Default)' -Value $dllPath
        Set-ItemProperty -Path $inprocPath -Name 'ThreadingModel' -Value 'Apartment'

        Write-Host "Registered COM InprocServer32 for CLSID $CredProvCLSID"
    }

    # Credential Provider registration
    if ($PSCmdlet.ShouldProcess($CredProvCLSID, 'Register as Windows Credential Provider')) {
        if (-not (Test-Path $CredProvRegPath)) {
            New-Item -Path $CredProvRegPath -Force | Out-Null
        }
        Set-ItemProperty -Path $CredProvRegPath -Name '(Default)' -Value 'MfaSrv Credential Provider'

        Write-Host "Registered Windows Credential Provider: $CredProvCLSID"
    }
}

# ===========================================================================
# Main
# ===========================================================================

Assert-Administrator

if ($Uninstall) {
    # ----- Uninstall flow -----
    Write-Host '=== MfaSrv Endpoint Agent Uninstall ===' -ForegroundColor Cyan

    Remove-MfaSrvService
    Unregister-CredentialProvider

    # Remove Credential Provider DLL from System32
    $cpDst = Join-Path $System32Dir $CredProvDllName
    if (Test-Path $cpDst) {
        if ($PSCmdlet.ShouldProcess($cpDst, 'Delete Credential Provider DLL')) {
            Remove-Item -Path $cpDst -Force
            Write-Host "Deleted $cpDst"
        }
    }

    # Remove install directory
    if (Test-Path $InstallDir) {
        if ($PSCmdlet.ShouldProcess($InstallDir, 'Delete install directory')) {
            Remove-Item -Path $InstallDir -Recurse -Force
            Write-Host "Deleted $InstallDir"
        }
    }

    # Preserve config directory by default
    if (Test-Path $ConfigDir) {
        Write-Warning "Configuration directory preserved at: $ConfigDir"
        Write-Warning 'Delete it manually if you want a clean removal.'
    }

    Write-Host ''
    Write-Host 'Uninstall complete.' -ForegroundColor Green
    Write-Host 'The credential provider tile will disappear from the logon screen on next reboot or logoff.' -ForegroundColor Yellow
    return
}

# ----- Install flow -----
Write-Host '=== MfaSrv Endpoint Agent Install ===' -ForegroundColor Cyan

# Validate source path
if (-not (Test-Path (Join-Path $SourcePath $ExeName))) {
    throw "Cannot find '$ExeName' in '$SourcePath'. Please build with: dotnet publish -c Release -r win-x64 --self-contained false"
}
if (-not (Test-Path (Join-Path $SourcePath $CredProvDllName))) {
    throw "Cannot find '$CredProvDllName' in '$SourcePath'. Please build the native project MfaSrv.EndpointAgent.Native in Release|x64 configuration."
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
    $filesToCopy = Get-ChildItem -Path $SourcePath -File | Where-Object { $_.Name -ne $CredProvDllName }
    foreach ($file in $filesToCopy) {
        Copy-Item -Path $file.FullName -Destination $InstallDir -Force
    }
}

# 4. Copy Credential Provider DLL to System32
$cpSrc = Join-Path $SourcePath $CredProvDllName
$cpDst = Join-Path $System32Dir $CredProvDllName
if ($PSCmdlet.ShouldProcess($cpDst, 'Copy Credential Provider DLL to System32')) {
    Write-Host "Copying $CredProvDllName to $System32Dir..."
    Copy-Item -Path $cpSrc -Destination $cpDst -Force
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

    $config.EndpointAgent.CentralServerUrl = $CentralServerUrl
    $config.EndpointAgent.AgentId          = $AgentId
    $config.EndpointAgent.FailoverMode     = $FailoverMode
    $config.EndpointAgent.Hostname         = $env:COMPUTERNAME

    $config | ConvertTo-Json -Depth 10 | Set-Content -Path $appSettingsDst -Encoding UTF8
    Write-Host "Configuration written to $appSettingsDst"
}

# 6. Register Credential Provider
Register-CredentialProvider

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
Write-Host 'Install complete.' -ForegroundColor Green
Write-Host 'The MfaSrv credential provider tile will appear on the next logon screen.' -ForegroundColor Yellow
