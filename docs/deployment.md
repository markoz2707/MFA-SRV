# MFA-SRV Deployment Guide

## Overview

MFA-SRV consists of three deployable components:

| Component | Target | Install Method |
|-----------|--------|---------------|
| Central Server | Windows/Linux server | Docker, PowerShell, or MSI |
| DC Agent + LSA DLL | Domain Controllers | PowerShell or MSI |
| Endpoint Agent + Credential Provider | Workstations | PowerShell, MSI, or GPO |

---

## 1. Central Server

### Docker (Recommended for Linux/Container environments)

```bash
cd deploy/docker

# Edit docker-compose.yml to set environment variables
docker-compose up -d
```

The Docker image uses a multi-stage build:
- Build stage: .NET 8 SDK
- Runtime stage: `mcr.microsoft.com/dotnet/aspnet:8.0`
- Exposes ports 5080 (HTTP) and 5081 (gRPC)
- SQLite database stored in a Docker volume

**Environment variables:**
```yaml
ConnectionStrings__DefaultConnection: "Data Source=/data/mfasrv.db"
Ldap__Server: "dc01.example.com"
Ldap__BaseDn: "DC=example,DC=com"
Ldap__BindDn: "CN=svc-mfasrv,OU=ServiceAccounts,DC=example,DC=com"
Ldap__BindPassword: "your-password"
MfaSrv__TokenSigningKey: "base64-encoded-256-bit-key"
MfaSrv__EncryptionKey: "base64-encoded-256-bit-key"
```

### PowerShell (Windows Server)

```powershell
# Publish the server
dotnet publish src/Server/MfaSrv.Server/MfaSrv.Server.csproj -c Release -o C:\MfaSrv\Server

# Run the install script
.\deploy\scripts\Install-Server.ps1 -InstallDir "C:\MfaSrv\Server"
```

The script:
- Creates a Windows Service (`MfaSrv.Server`)
- Configures firewall rules for ports 5080/5081
- Sets up the service to run as `NetworkService` (or specify a service account)
- Creates the backup directory

### MSI Installer

Build the WiX installer:
```bash
dotnet tool install --global wix
cd deploy/wix
wix build Server.wxs -o MfaSrv.Server.msi
```

The MSI installs to `C:\Program Files\MfaSrv\Server\` and registers the Windows Service.

---

## 2. DC Agent

The DC Agent consists of two parts:
- **C# Windows Service** (`MfaSrv.DcAgent.exe`) - manages authentication decisions, policy cache, gossip protocol
- **C++ LSA Authentication Package** (`MfaSrv.DcAgent.Native.dll`) - loaded into LSASS, intercepts logon events

### PowerShell Installation

```powershell
# Publish the DC Agent
dotnet publish src/Agents/MfaSrv.DcAgent/MfaSrv.DcAgent.csproj -c Release -r win-x64 -o C:\MfaSrv\DcAgent

# Copy the native DLL (requires separate C++ build)
copy src\Agents\MfaSrv.DcAgent.Native\x64\Release\MfaSrv.DcAgent.Native.dll C:\MfaSrv\DcAgent\

# Run the install script
.\deploy\scripts\Install-DcAgent.ps1 -InstallDir "C:\MfaSrv\DcAgent" -ServerUrl "https://mfasrv.example.com:5081"
```

The script:
- Creates the `MfaSrv.DcAgent` Windows Service
- Registers the LSA Authentication Package in the registry
- Configures the Named Pipe for LSASS-to-service IPC
- Requires a **reboot** to load the LSA DLL

### Configuration

Edit `C:\MfaSrv\DcAgent\appsettings.json`:

```json
{
  "DcAgent": {
    "ServerUrl": "https://mfasrv.example.com:5081",
    "InstanceId": "",
    "GlobalFailoverMode": "FailOpen",
    "CachePath": "./cache/mfasrv_cache.db",
    "SessionCacheMinutes": 480,
    "PolicyRefreshSeconds": 60,
    "CertificatePath": "./certs/agent.pfx",
    "CaCertificatePath": "./certs/ca.pem"
  },
  "Gossip": {
    "Enabled": true,
    "ListenPort": 5090,
    "Peers": ["dc02.example.com:5090", "dc03.example.com:5090"]
  }
}
```

### LSA DLL Safety

The native DLL loaded into LSASS is designed with these safety guarantees:
- All code wrapped in `__try/__except` SEH handlers
- Named Pipe timeout: 3 seconds (never blocks indefinitely)
- Fail-open on any exception (logs and allows authentication)
- No dynamic allocation outside communication buffers
- Links only: ntdll, kernel32, advapi32
- Must be digitally signed for production deployment

---

## 3. Endpoint Agent

### PowerShell Installation

```powershell
dotnet publish src/Agents/MfaSrv.EndpointAgent/MfaSrv.EndpointAgent.csproj -c Release -r win-x64 -o C:\MfaSrv\EndpointAgent

# Copy Credential Provider DLL
copy src\Agents\MfaSrv.EndpointAgent.Native\x64\Release\MfaSrv.EndpointAgent.Native.dll C:\MfaSrv\EndpointAgent\

.\deploy\scripts\Install-EndpointAgent.ps1 -InstallDir "C:\MfaSrv\EndpointAgent" -ServerUrl "https://mfasrv.example.com:5081"
```

### Group Policy Deployment

For large-scale deployment, use the ADMX/ADML templates:

1. Copy `deploy/gpo/MfaSrv.admx` to `\\domain\SYSVOL\domain\Policies\PolicyDefinitions\`
2. Copy `deploy/gpo/en-US/MfaSrv.adml` to `\\domain\SYSVOL\domain\Policies\PolicyDefinitions\en-US\`
3. Open Group Policy Management Console
4. Navigate to: **Computer Configuration > Administrative Templates > MFA-SRV**
5. Configure settings for DC Agent and/or Endpoint Agent

Available GPO settings:
- Server URL
- Failover mode (Fail-Open, Fail-Close, Cached-Only)
- Cache path and duration
- Certificate paths
- Gossip peer list
- Heartbeat interval

---

## High Availability

MFA-SRV supports active-passive HA with database-backed leader election.

### Setup

1. Deploy two (or more) Central Server instances pointing to the same database
2. Use a shared SQLite database (NFS/SMB mount) or migrate to PostgreSQL
3. Enable HA in `appsettings.json`:

```json
{
  "HA": {
    "Enabled": true,
    "InstanceId": "server01",
    "LeaseDurationSeconds": 30,
    "LeaseRenewIntervalSeconds": 10
  }
}
```

### Behavior

- Only the leader runs background tasks (session cleanup, LDAP sync, backups)
- All instances can serve health/metrics/read-only endpoints
- If the leader fails to renew the lease within `LeaseDurationSeconds`, a standby instance takes over
- Leader election state is visible at `/status` and `/health`

---

## Database

### Development: SQLite (default)

Zero configuration required. Database file created automatically at startup.

### Production: PostgreSQL

Change the connection string and install the Npgsql EF Core provider:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=db.example.com;Database=mfasrv;Username=mfasrv;Password=..."
  }
}
```

The EF Core abstraction layer allows switching providers without code changes.

### Automated Backups

- Enabled by default with 6-hour interval
- Uses SQLite `VACUUM INTO` for hot backups (no write locks)
- Old backups rotated based on retention count (default: 10)
- Manual backups via `POST /api/backups`
- Download/restore via the backup REST API

---

## Certificate Infrastructure

MFA-SRV includes built-in certificate management for mTLS:

1. The server generates a self-signed CA certificate on first run
2. Agents submit a CSR during registration
3. The server signs agent certificates with the CA
4. All gRPC communication uses mTLS with these certificates

For production, replace with certificates from your enterprise PKI.

---

## Firewall Rules

| Source | Destination | Port | Protocol | Purpose |
|--------|-------------|------|----------|---------|
| Admin browsers | Central Server | 5080/tcp | HTTPS | REST API + Admin Portal |
| DC Agents | Central Server | 5081/tcp | gRPC (HTTP/2) | Auth evaluation, policy sync |
| Endpoint Agents | Central Server | 5081/tcp | gRPC (HTTP/2) | Auth evaluation, heartbeat |
| DC Agent | DC Agent | 5090/tcp | gRPC (HTTP/2) | Gossip protocol (session sync) |
| Monitoring | Central Server | 5080/tcp | HTTP | /metrics, /health, /ready |
