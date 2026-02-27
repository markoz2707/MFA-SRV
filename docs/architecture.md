# MFA-SRV Architecture

## System Overview

MFA-SRV is an agentless (from the AD schema perspective) multi-factor authentication system for Windows Active Directory. It intercepts authentication events at domain controllers and workstations, evaluates policies, and enforces additional verification factors.

```
                     +-----------------------+
                     |   Admin Dashboard     |
                     |  (React + TypeScript) |
                     +----------+------------+
                                | REST/HTTPS
                     +----------v------------+
                     |   Central MFA Server  |
                     |  (ASP.NET Core 8)     |
                     |                       |
                     |  +----------------+   |
                     |  | Policy Engine  |   |
                     |  +----------------+   |
                     |  | Session Mgr    |   |
                     |  +----------------+   |
                     |  | MFA Providers  |   |
                     |  +----------------+   |
                     |  | Audit Logger   |   |
                     |  +----------------+   |
                     +----+----------+-------+
                          |          |
                gRPC/mTLS |          | gRPC/mTLS
                          |          |
              +-----------+--+  +----+-------------+
              |  DC Agent    |  | Endpoint Agent   |
              |  (per DC)    |  | (per workstation)|
              |              |  |                  |
              | C# Service   |  | C# Service       |
              | + C++ LSA DLL|  | + C++ Cred.Prov. |
              +------+-------+  +------------------+
                     |
              gRPC gossip
                     |
              +------+-------+
              |  DC Agent    |
              |  (other DCs) |
              +--------------+
```

## Component Details

### Central Server (`MfaSrv.Server`)

The central server is the brain of the system. It runs as an ASP.NET Core 8 application exposing both REST and gRPC endpoints.

**Key Services:**

| Service | Responsibility |
|---------|---------------|
| `PolicyEngine` | Evaluates authentication context against policy rules |
| `SessionManager` | Creates, validates, and revokes MFA sessions |
| `MfaChallengeOrchestrator` | Coordinates MFA challenge issuance and verification |
| `UserSyncService` | Synchronizes users from Active Directory via LDAP |
| `LeaderElectionService` | Database-backed leader election for HA |
| `DatabaseBackupService` | Automated SQLite backups with rotation |
| `PolicySyncStreamService` | gRPC server-streaming for real-time policy push to agents |
| `SessionCleanupService` | Background cleanup of expired sessions |

**Communication:**
- REST API on port 5080 (admin portal, backup management)
- gRPC on port 5081 (agent communication)
- Prometheus metrics on `/metrics`

### DC Agent (`MfaSrv.DcAgent` + `MfaSrv.DcAgent.Native`)

Deployed on every domain controller. Two-layer architecture:

**Layer 1 - C++ LSA Authentication Package:**
- Registered in `HKLM\SYSTEM\CurrentControlSet\Control\Lsa\Authentication Packages`
- Loaded into LSASS process at boot
- Implements `SpLogonUserEx2` to intercept every logon event
- Communicates with the C# service via Named Pipe (`\\.\pipe\MfaSrvDcAgent`)
- Critical safety requirements: SEH wrappers, 3s timeout, fail-open on exception

**Layer 2 - C# Windows Service:**
- Manages the Named Pipe server for LSA DLL communication
- Evaluates authentication decisions using local policy cache
- Communicates with Central Server via gRPC for policy updates and session validation
- Participates in gossip protocol for DC-to-DC session synchronization
- Maintains SQLite cache for offline operation

**Decision Flow:**
```
LSASS                Named Pipe           DC Agent Service
  |                      |                      |
  |--- auth_request ---->|                      |
  |                      |--- query ----------->|
  |                      |                      |-- check local cache
  |                      |                      |-- check gossip sessions
  |                      |                      |-- query central server (if needed)
  |                      |<-- decision ---------|
  |<-- allow/deny -------|                      |
```

### Endpoint Agent (`MfaSrv.EndpointAgent` + `MfaSrv.EndpointAgent.Native`)

Deployed on workstations for interactive logon MFA.

**C++ Credential Provider:**
- Implements `ICredentialProvider` / `ICredentialProviderCredential`
- Integrates with the Windows logon UI
- Adds MFA input fields (OTP, FIDO2 button) to the logon screen
- Communicates with C# service via Named Pipe

**C# Windows Service:**
- Named Pipe server for Credential Provider communication
- gRPC client for Central Server communication
- Local session cache for offline validation
- YubiKey/FIDO2 local assertion flow support
- Heartbeat reporting to Central Server

### MFA Providers

Plugin architecture via `IMfaProvider` interface:

```csharp
public interface IMfaProvider
{
    string MethodId { get; }
    string DisplayName { get; }
    bool SupportsSynchronousVerification { get; }
    bool SupportsAsynchronousVerification { get; }
    bool RequiresEndpointAgent { get; }

    Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct);
    Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(EnrollmentContext ctx, string response, CancellationToken ct);
    Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct);
    Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct);
    Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct);
}
```

| Provider | Sync | Async | Endpoint Required | Description |
|----------|------|-------|-------------------|-------------|
| TOTP | Yes | No | No | Time-based OTP (Google Authenticator, etc.) |
| Push | No | Yes | No | Push notifications via FCM/APNs |
| FIDO2 | Yes | No | Yes | WebAuthn with YubiKey or platform authenticator |
| FortiToken | Yes | Yes | No | FortiAuthenticator REST API integration |
| SMS | Yes | No | No | SMS OTP via configurable gateway |
| Email | Yes | No | No | Email OTP via SMTP |

## Policy Engine

Policies are evaluated top-to-bottom by priority. First matching policy wins.

### Rule Structure

```
Policy (priority, failover_mode)
├── RuleGroup 1 (AND within group)
│   ├── Rule: SourceGroup = "Domain Admins"
│   └── Rule: AuthProtocol = "Kerberos"
├── RuleGroup 2 (OR between groups)
│   └── Rule: SourceIp IN "10.0.0.0/8"
└── Actions
    └── RequireMfa (method: TOTP)
```

- **Within a rule group:** all rules must match (AND logic)
- **Between rule groups:** any group matching is sufficient (OR logic)
- **Negation:** individual rules can be negated

### Rule Types

| Type | Description | Example |
|------|-------------|---------|
| `SourceUser` | Match by user SAM/UPN | `jdoe` |
| `SourceGroup` | Match by AD group SID | `S-1-5-21-...-512` |
| `SourceIp` | Match by source IP/CIDR | `10.0.0.0/8` |
| `SourceOu` | Match by user's OU path | `OU=Admins,DC=example,DC=com` |
| `TargetResource` | Match by target host/service | `fileserver.example.com` |
| `AuthProtocol` | Match by authentication protocol | `Kerberos`, `NTLM` |
| `TimeWindow` | Match by time-of-day | `08:00-18:00` |
| `RiskScore` | Match by computed risk score | `> 50` |

### Action Types

| Action | Behavior |
|--------|----------|
| `RequireMfa` | Enforce MFA (optionally require specific method) |
| `Deny` | Block authentication unconditionally |
| `Allow` | Allow without MFA |
| `AlertOnly` | Allow but generate an alert in audit log |

## Gossip Protocol

DC Agents synchronize MFA sessions between domain controllers using a gossip protocol over gRPC.

**Purpose:** When a user completes MFA on DC01, the session is gossiped to DC02/DC03 so subsequent authentications are recognized without requiring MFA again.

**Protocol:**
- Each DC Agent maintains a list of peer DC Agents
- Sessions are broadcast to all peers on creation/revocation
- Peers acknowledge receipt to prevent infinite rebroadcast
- Conflict resolution: latest timestamp wins

## Data Flow

### TOTP Enrollment

```
Admin Portal → POST /api/enrollments/begin
  → Server generates TOTP secret
  → Encrypts with AES-256-GCM
  → Stores encrypted secret in DB
  → Returns QR code data URI

User scans QR → enters 6-digit code
Admin Portal → POST /api/enrollments/complete
  → Server decrypts secret
  → Validates TOTP code
  → Activates enrollment
```

### Authentication with MFA

```
User logs in → Kerberos AS-REQ → DC
  → LSA DLL intercepts
  → Named Pipe → DC Agent
  → gRPC → Central Server
  → Policy Engine evaluates
  → Decision: REQUIRE_MFA
  → DC Agent holds the request
  → Central Server sends push/waits for OTP
  → User provides MFA
  → Central Server verifies
  → Session created, token issued
  → DC Agent releases authentication → ALLOW
  → Session gossiped to other DC Agents
```

## Database Schema

Key tables (EF Core with SQLite):

| Table | Description |
|-------|-------------|
| `Users` | Synced from AD (objectGUID, SAM, UPN, groups) |
| `UserGroupMemberships` | Cached AD group memberships |
| `MfaEnrollments` | User MFA method registrations (encrypted secrets) |
| `Policies` | Authentication policy definitions |
| `PolicyRuleGroups` | Logical groups of rules within policies |
| `PolicyRules` | Individual matching rules |
| `PolicyActions` | Actions to take when policy matches |
| `MfaSessions` | Active MFA sessions (token, expiry, source IP) |
| `MfaChallenges` | Pending MFA challenges awaiting verification |
| `AuditLog` | Append-only audit trail |
| `AgentRegistrations` | Registered DC and Endpoint agents |
| `LeaderLeases` | HA leader election lease tracking |

## Security Model

| Channel | Protocol | Security |
|---------|----------|----------|
| LSA DLL ↔ DC Agent | Named Pipe | DACL (SYSTEM only) |
| DC Agent ↔ Central Server | gRPC/HTTP2 | mTLS (per-agent certs) |
| Endpoint Agent ↔ Central Server | gRPC/HTTP2 | mTLS |
| DC Agent ↔ DC Agent (gossip) | gRPC/HTTP2 | mTLS |
| Admin Portal ↔ Central Server | REST/HTTPS | JWT/Cookie auth |

**Session Tokens:** Compact binary format with HMAC-SHA256 (~120 bytes), not JWT.

**Secret Storage:** TOTP secrets encrypted at rest with AES-256-GCM. Encryption key configured in `appsettings.json` (should be stored in a key vault in production).
