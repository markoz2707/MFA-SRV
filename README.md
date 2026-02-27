# MFA-SRV

Multi-factor authentication system for Windows Active Directory. Intercepts authentication on domain controllers and enforces additional verification (TOTP, Push, FIDO2/YubiKey, FortiToken, SMS, Email) without modifying the AD schema.

## Architecture

```
+--------------------------------------------------------------+
|                  ADMIN DASHBOARD (React + TS)                |
+-------------------------------+------------------------------+
                                | REST/HTTPS
+-------------------------------+------------------------------+
|                  CENTRAL MFA SERVER                          |
|             (ASP.NET Core 8 + gRPC + REST)                   |
|  [Policy Engine] [Session Mgr] [MFA Providers] [Audit Log]  |
+---------------+----------------------------+-----------------+
    gRPC/mTLS   |                            | gRPC/mTLS
+---------------+--------+     +-------------+-----------------+
|     DC AGENT (per DC)  |     |    ENDPOINT AGENT (per WS)    |
|  C# Windows Service    |     |    C# Windows Service         |
|  + C++ LSA Auth DLL    |     |    + C++ Credential Provider  |
+------------------------+     +--------------------------------+
```

### Components

| Component | Technology | Description |
|-----------|-----------|-------------|
| Central Server | ASP.NET Core 8, EF Core, SQLite | Policy evaluation, session management, MFA orchestration |
| DC Agent | C# Windows Service + C++ DLL | Intercepts authentication on domain controllers via LSA |
| Endpoint Agent | C# Windows Service + C++ DLL | Credential Provider for workstation logon with local MFA |
| Admin Portal | React 18 + TypeScript + Vite | Web dashboard for policy management and monitoring |

### Authentication Flow

**Interactive Logon (with Endpoint Agent):**
1. User enters credentials -> Credential Provider intercepts
2. Endpoint Agent queries Central Server for policy evaluation
3. If MFA required -> prompt for OTP/push/YubiKey on login screen
4. User completes MFA -> Central Server issues session token
5. Token propagated to DC Agents via gossip protocol
6. Credentials released to Windows -> Kerberos AS-REQ -> DC
7. LSA DLL queries DC Agent -> finds valid session -> ALLOW

**Network Logon (RDP/SMB, no Endpoint Agent):**
1. Kerberos/NTLM request arrives at DC
2. LSA DLL intercepts -> queries DC Agent -> queries Central Server
3. If MFA required -> out-of-band challenge (push/SMS/email)
4. DC Agent holds request until user approves or timeout
5. After approval -> ALLOW -> delegate to original auth package

**Degraded Mode (Central Server unavailable):**
- `FAIL_OPEN`: Allow authentication, log event (default)
- `FAIL_CLOSE`: Block authentication
- `CACHED_ONLY`: Allow only with cached valid session

## Project Structure

```
MFA-SRV/
├── MfaSrv.sln                          # Solution (16 projects)
├── src/
│   ├── Core/
│   │   ├── MfaSrv.Core/                # Domain entities, interfaces, enums
│   │   ├── MfaSrv.Cryptography/        # TOTP, AES-GCM, token signing, certificates
│   │   └── MfaSrv.Protocol/            # gRPC .proto definitions + generated code
│   ├── Server/
│   │   └── MfaSrv.Server/              # Central MFA Server (REST + gRPC)
│   ├── Agents/
│   │   ├── MfaSrv.DcAgent/             # DC Agent Windows Service (C#)
│   │   ├── MfaSrv.DcAgent.Native/      # LSA Authentication Package (C++)
│   │   ├── MfaSrv.EndpointAgent/       # Endpoint Agent Windows Service (C#)
│   │   └── MfaSrv.EndpointAgent.Native/# Credential Provider DLL (C++)
│   ├── Providers/
│   │   ├── MfaSrv.Provider.Totp/       # TOTP (Google Authenticator, etc.)
│   │   ├── MfaSrv.Provider.Push/       # Push notifications (FCM/APNs)
│   │   ├── MfaSrv.Provider.Fido2/      # FIDO2/WebAuthn (YubiKey)
│   │   ├── MfaSrv.Provider.FortiToken/ # FortiToken via FortiAuthenticator API
│   │   ├── MfaSrv.Provider.Sms/        # SMS OTP
│   │   └── MfaSrv.Provider.Email/      # Email OTP
│   └── AdminPortal/
│       └── mfasrv-admin/               # React + TypeScript SPA
├── tests/
│   ├── MfaSrv.Tests.Unit/              # 169 unit tests
│   └── MfaSrv.Tests.Integration/       # Integration test project
├── deploy/
│   ├── docker/                         # Dockerfile + docker-compose.yml
│   ├── scripts/                        # PowerShell install scripts
│   ├── wix/                            # WiX v4 MSI installer definitions
│   └── gpo/                            # ADMX/ADML Group Policy templates
└── docs/                               # Documentation
```

## Quick Start

### Prerequisites

- .NET 8 SDK
- Node.js 18+ (for admin portal)
- Visual Studio 2022 or C++ Build Tools (for native DLLs)

### Build

```bash
# Build all C# projects
dotnet build MfaSrv.sln

# Build admin portal
cd src/AdminPortal/mfasrv-admin
npm install
npm run build

# Run tests
dotnet test tests/MfaSrv.Tests.Unit/MfaSrv.Tests.Unit.csproj
```

### Run the Central Server

```bash
cd src/Server/MfaSrv.Server
dotnet run
```

The server starts on:
- **HTTP API**: `http://localhost:5080`
- **gRPC**: `http://localhost:5081` (HTTP/2)

### Configuration

Edit `src/Server/MfaSrv.Server/appsettings.json`:

```jsonc
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=mfasrv.db"  // SQLite path
  },
  "Ldap": {
    "Server": "dc01.example.com",
    "Port": 389,
    "BaseDn": "DC=example,DC=com",
    "BindDn": "CN=svc-mfasrv,OU=ServiceAccounts,DC=example,DC=com",
    "BindPassword": "",
    "UseSsl": false,
    "SyncIntervalMinutes": 15
  },
  "MfaSrv": {
    "TokenSigningKey": "",    // Base64-encoded 256-bit key (auto-generated if empty)
    "EncryptionKey": ""       // Base64-encoded 256-bit key for secret encryption
  },
  "Cors": {
    "Origins": ["http://localhost:3000", "http://localhost:5173"]
  },
  "Backup": {
    "BackupDirectory": "./backups",
    "BackupIntervalHours": 6,
    "RetentionCount": 10,
    "Enabled": true
  },
  "HA": {
    "Enabled": false,
    "InstanceId": "",          // Auto-generated if empty
    "LeaseDurationSeconds": 30,
    "LeaseRenewIntervalSeconds": 10
  }
}
```

## API Reference

See [docs/api-reference.md](docs/api-reference.md) for the complete REST API documentation.

## Deployment

See [docs/deployment.md](docs/deployment.md) for installation guides covering:
- Docker deployment
- Windows Service installation (PowerShell scripts)
- MSI installer packages
- Group Policy configuration

## Monitoring

See [docs/monitoring.md](docs/monitoring.md) for details on:
- Prometheus metrics
- Health check endpoints
- Grafana dashboard setup

## Security

- TOTP secrets encrypted at rest with AES-256-GCM
- Agent-to-server communication secured with mTLS
- LSA DLL runs in LSASS with SEH exception handling (never crashes)
- Named Pipe communication secured with DACL (SYSTEM-only access)
- Session tokens use HMAC-SHA256 (compact binary format, ~120 bytes)
- Backup restore requires two-step confirmation token flow

## License

Proprietary. All rights reserved.
