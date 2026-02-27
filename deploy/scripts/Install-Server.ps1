#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or uninstalls the MfaSrv Central Server on a Windows host.

.DESCRIPTION
    This script performs a full deployment of the MfaSrv Central Server, including:
      - Copying server binaries to Program Files
      - Creating the data directory in ProgramData for the SQLite database
      - Writing appsettings.json with supplied parameters
      - Configuring Windows Firewall rules for HTTP and gRPC ports
      - Installing and starting the Windows service

    The -Uninstall switch reverses all of the above.

.PARAMETER DbConnectionString
    SQLite connection string. Defaults to using the ProgramData directory.

.PARAMETER LdapServer
    LDAP server hostname for Active Directory user sync. Leave empty to skip LDAP configuration.

.PARAMETER BindDn
    Distinguished name for the LDAP bind account.

.PARAMETER ListenPort
    HTTP API listen port. Default: 5080.

.PARAMETER GrpcPort
    gRPC listen port for agent communication. Default: 5081.

.PARAMETER SourcePath
    Path to the build output directory containing published binaries.
    Defaults to .\publish.

.PARAMETER Uninstall
    When specified, removes the MfaSrv Server from the system.

.EXAMPLE
    .\Install-Server.ps1 -LdapServer "dc01.contoso.com" -BindDn "CN=svc-mfasrv,OU=ServiceAccounts,DC=contoso,DC=com"

.EXAMPLE
    .\Install-Server.ps1 -Uninstall
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $false)]
    [string]$DbConnectionString = '',

    [Parameter(Mandatory = $false)]
    [string]$LdapServer = '',

    [Parameter(Mandatory = $false)]
    [string]$BindDn = '',

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int]$ListenPort = 5080,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int]$GrpcPort = 5081,

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
$ServiceName        = 'MfaSrvServer'
$ServiceDisplayName = 'MfaSrv Server'
$ServiceDescription = 'MfaSrv central MFA server - policy engine, user sync, audit logging, and gRPC endpoint for agent communication.'
$InstallDir         = Join-Path $env:ProgramFiles 'MfaSrv\Server'
$DataDir            = Join-Path $env:ProgramData  'MfaSrv\Server'
$ExeName            = 'MfaSrv.Server.exe'

$FwRuleNameHttp     = 'MfaSrv Server HTTP'
$FwRuleNameGrpc     = 'MfaSrv Server gRPC'

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
# Helper: Configure firewall rules
# ---------------------------------------------------------------------------
function Add-FirewallRules {
    param(
        [int]$HttpPort,
        [int]$GrpcPort
    )

    if ($PSCmdlet.ShouldProcess("TCP $HttpPort", 'Create firewall rule for HTTP')) {
        # Remove existing rule if present (idempotent)
        $existing = Get-NetFirewallRule -DisplayName $FwRuleNameHttp -ErrorAction SilentlyContinue
        if ($null -ne $existing) {
            Remove-NetFirewallRule -DisplayName $FwRuleNameHttp -ErrorAction SilentlyContinue
        }

        New-NetFirewallRule -DisplayName $FwRuleNameHttp `
                            -Description 'Allow inbound HTTP connections to MfaSrv Server REST API and admin portal.' `
                            -Direction Inbound `
                            -Protocol TCP `
                            -LocalPort $HttpPort `
                            -Action Allow `
                            -Profile Domain,Private `
                            -Enabled True | Out-Null
        Write-Host "Firewall rule created: $FwRuleNameHttp (TCP $HttpPort)"
    }

    if ($PSCmdlet.ShouldProcess("TCP $GrpcPort", 'Create firewall rule for gRPC')) {
        $existing = Get-NetFirewallRule -DisplayName $FwRuleNameGrpc -ErrorAction SilentlyContinue
        if ($null -ne $existing) {
            Remove-NetFirewallRule -DisplayName $FwRuleNameGrpc -ErrorAction SilentlyContinue
        }

        New-NetFirewallRule -DisplayName $FwRuleNameGrpc `
                            -Description 'Allow inbound gRPC connections from MfaSrv DC and endpoint agents.' `
                            -Direction Inbound `
                            -Protocol TCP `
                            -LocalPort $GrpcPort `
                            -Action Allow `
                            -Profile Domain,Private `
                            -Enabled True | Out-Null
        Write-Host "Firewall rule created: $FwRuleNameGrpc (TCP $GrpcPort)"
    }
}

function Remove-FirewallRules {
    foreach ($ruleName in @($FwRuleNameHttp, $FwRuleNameGrpc)) {
        $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
        if ($null -ne $existing) {
            if ($PSCmdlet.ShouldProcess($ruleName, 'Remove firewall rule')) {
                Remove-NetFirewallRule -DisplayName $ruleName
                Write-Host "Firewall rule removed: $ruleName"
            }
        }
    }
}

# ===========================================================================
# Main
# ===========================================================================

Assert-Administrator

if ($Uninstall) {
    # ----- Uninstall flow -----
    Write-Host '=== MfaSrv Server Uninstall ===' -ForegroundColor Cyan

    Remove-MfaSrvService
    Remove-FirewallRules

    # Remove install directory
    if (Test-Path $InstallDir) {
        if ($PSCmdlet.ShouldProcess($InstallDir, 'Delete install directory')) {
            Remove-Item -Path $InstallDir -Recurse -Force
            Write-Host "Deleted $InstallDir"
        }
    }

    # Preserve data directory by default (contains the database)
    if (Test-Path $DataDir) {
        Write-Warning "Data directory preserved at: $DataDir"
        Write-Warning 'This contains the SQLite database. Delete it manually if you want a clean removal.'
    }

    Write-Host ''
    Write-Host 'Uninstall complete.' -ForegroundColor Green
    return
}

# ----- Install flow -----
Write-Host '=== MfaSrv Server Install ===' -ForegroundColor Cyan

# Validate source path
if (-not (Test-Path (Join-Path $SourcePath $ExeName))) {
    throw "Cannot find '$ExeName' in '$SourcePath'. Please build with: dotnet publish -c Release"
}

# Set default DB connection string if not provided
if ([string]::IsNullOrWhiteSpace($DbConnectionString)) {
    $DbConnectionString = "Data Source=$(Join-Path $DataDir 'mfasrv.db')"
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
foreach ($dir in @($InstallDir, $DataDir)) {
    if (-not (Test-Path $dir)) {
        if ($PSCmdlet.ShouldProcess($dir, 'Create directory')) {
            New-Item -Path $dir -ItemType Directory -Force | Out-Null
            Write-Host "Created directory: $dir"
        }
    }
}

# Set ACL on data directory: SYSTEM and Administrators only
if ($PSCmdlet.ShouldProcess($DataDir, 'Set directory permissions')) {
    $acl = Get-Acl -Path $DataDir
    $acl.SetAccessRuleProtection($true, $false) # disable inheritance
    $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) } | Out-Null

    $systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        'SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $adminRule  = New-Object System.Security.AccessControl.FileSystemAccessRule(
        'BUILTIN\Administrators', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($systemRule)
    $acl.AddAccessRule($adminRule)
    Set-Acl -Path $DataDir -AclObject $acl
    Write-Host "Secured permissions on $DataDir"
}

# 3. Copy server binaries
if ($PSCmdlet.ShouldProcess($InstallDir, 'Copy server binaries')) {
    Write-Host "Copying binaries to $InstallDir..."
    $filesToCopy = Get-ChildItem -Path $SourcePath -File
    foreach ($file in $filesToCopy) {
        Copy-Item -Path $file.FullName -Destination $InstallDir -Force
    }
}

# 4. Write appsettings.json
$appSettingsSrc = Join-Path $SourcePath 'appsettings.json'
$appSettingsDst = Join-Path $DataDir 'appsettings.json'

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

    # Update connection string
    $config.ConnectionStrings.DefaultConnection = $DbConnectionString

    # Update LDAP settings if provided
    if (-not [string]::IsNullOrWhiteSpace($LdapServer)) {
        $config.Ldap.Server = $LdapServer
    }
    if (-not [string]::IsNullOrWhiteSpace($BindDn)) {
        $config.Ldap.BindDn = $BindDn
    }

    # Update Kestrel endpoints
    $config.Kestrel.Endpoints.Http.Url = "http://0.0.0.0:$ListenPort"
    $config.Kestrel.Endpoints.Grpc.Url = "http://0.0.0.0:$GrpcPort"

    $config | ConvertTo-Json -Depth 10 | Set-Content -Path $appSettingsDst -Encoding UTF8
    Write-Host "Configuration written to $appSettingsDst"
}

# 5. Configure firewall
Add-FirewallRules -HttpPort $ListenPort -GrpcPort $GrpcPort

# 6. Create / update Windows service
$exePath = Join-Path $InstallDir $ExeName
if ($null -eq (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Create Windows service')) {
        Write-Host "Creating service '$ServiceName'..."
        New-Service -Name $ServiceName `
                    -BinaryPathName "`"$exePath`" --contentRoot `"$DataDir`"" `
                    -DisplayName $ServiceDisplayName `
                    -Description $ServiceDescription `
                    -StartupType Automatic | Out-Null

        # Configure service recovery: restart on first two failures, then do nothing
        & sc.exe failure $ServiceName reset=86400 actions=restart/60000/restart/60000// | Out-Null
    }
}
else {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Update service binary path')) {
        & sc.exe config $ServiceName binPath= "`"$exePath`" --contentRoot `"$DataDir`"" | Out-Null
    }
}

# 7. Start the service
if ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    Write-Host "Starting service '$ServiceName'..."
    Start-Service -Name $ServiceName
    $svc = Get-Service -Name $ServiceName
    Write-Host "Service status: $($svc.Status)"
}

# 8. Summary
Write-Host ''
Write-Host '=== MfaSrv Server Install Summary ===' -ForegroundColor Green
Write-Host "  Install directory : $InstallDir"
Write-Host "  Data directory    : $DataDir"
Write-Host "  HTTP endpoint     : http://0.0.0.0:$ListenPort"
Write-Host "  gRPC endpoint     : http://0.0.0.0:$GrpcPort"
Write-Host "  Database          : $DbConnectionString"
if (-not [string]::IsNullOrWhiteSpace($LdapServer)) {
    Write-Host "  LDAP server       : $LdapServer"
}
Write-Host ''
Write-Host 'Install complete.' -ForegroundColor Green
