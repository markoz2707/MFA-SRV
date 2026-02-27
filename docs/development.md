# MFA-SRV Development Guide

## Prerequisites

- .NET 8 SDK
- Node.js 18+ and npm
- Visual Studio 2022 with:
  - ASP.NET and web development workload
  - Desktop development with C++ workload (for native DLLs)
- Git

## Building

### Full Solution Build

```bash
# Build all C# projects (14 projects)
dotnet build MfaSrv.sln

# Native C++ projects require Visual Studio / MSBuild with C++ tools
# Open MfaSrv.sln in Visual Studio and build DcAgent.Native / EndpointAgent.Native
```

### Admin Portal

```bash
cd src/AdminPortal/mfasrv-admin
npm install
npm run dev      # Development server on http://localhost:5173
npm run build    # Production build to dist/
```

### Running the Server

```bash
cd src/Server/MfaSrv.Server
dotnet run
```

The server auto-creates the SQLite database on first run. No migrations needed.

**Endpoints:**
- REST API: http://localhost:5080
- gRPC: http://localhost:5081
- Prometheus: http://localhost:5080/metrics
- Health: http://localhost:5080/health
- Readiness: http://localhost:5080/ready

## Testing

```bash
# Run all unit tests (169 tests)
dotnet test tests/MfaSrv.Tests.Unit/MfaSrv.Tests.Unit.csproj

# Run with verbose output
dotnet test tests/MfaSrv.Tests.Unit/MfaSrv.Tests.Unit.csproj --verbosity normal

# Run specific test class
dotnet test --filter "FullyQualifiedName~PolicyEngineTests"
```

### Test Organization

```
tests/MfaSrv.Tests.Unit/
├── Cryptography/
│   ├── AesGcmEncryptionTests.cs
│   ├── Base32Tests.cs
│   ├── CertificateHelperTests.cs
│   ├── SessionTokenServiceTests.cs
│   └── TotpGeneratorTests.cs
├── DcAgent/
│   ├── AuthDecisionServiceTests.cs
│   └── SqliteCacheStoreTests.cs
├── Providers/
│   ├── EmailMfaProviderTests.cs
│   ├── Fido2MfaProviderTests.cs
│   ├── FortiTokenMfaProviderTests.cs
│   ├── PushMfaProviderTests.cs
│   └── SmsMfaProviderTests.cs
└── Server/
    ├── BackupSettingsTests.cs
    ├── DatabaseExportServiceTests.cs
    ├── HealthCheckTests.cs
    ├── LeaderElectionServiceTests.cs
    ├── MetricsServiceTests.cs
    ├── PolicyEngineTests.cs
    └── SessionManagerTests.cs
```

### Test Stack

| Package | Version | Purpose |
|---------|---------|---------|
| xUnit | 2.9.0 | Test framework |
| FluentAssertions | 6.12.1 | Assertion library |
| Moq | 4.20.72 | Mocking |
| EF Core InMemory | 8.0.8 | In-memory database for tests |
| Microsoft.Data.Sqlite | 8.0.8 | SQLite in-memory for cache tests |

## Project Dependencies

```
MfaSrv.Core (entities, interfaces, enums)
├── No external dependencies

MfaSrv.Cryptography (TOTP, AES-GCM, certs, tokens)
├── MfaSrv.Core

MfaSrv.Protocol (gRPC .proto + generated code)
├── Grpc.Tools

MfaSrv.Server (central server)
├── MfaSrv.Core
├── MfaSrv.Cryptography
├── MfaSrv.Protocol
├── MfaSrv.Provider.Totp
├── EF Core + SQLite
├── Grpc.AspNetCore
├── prometheus-net.AspNetCore
└── System.DirectoryServices.Protocols

MfaSrv.DcAgent (DC agent service)
├── MfaSrv.Core
├── MfaSrv.Cryptography
├── MfaSrv.Protocol
└── Microsoft.Data.Sqlite

MfaSrv.EndpointAgent (endpoint agent service)
├── MfaSrv.Core
├── MfaSrv.Cryptography
└── MfaSrv.Protocol

MfaSrv.Provider.* (MFA providers)
├── MfaSrv.Core
└── (provider-specific dependencies)
```

## Adding a New MFA Provider

1. Create a new project `src/Providers/MfaSrv.Provider.{Name}/`
2. Implement `IMfaProvider`:

```csharp
public class MyMfaProvider : IMfaProvider
{
    public string MethodId => "MY_METHOD";
    public string DisplayName => "My Method";
    public bool SupportsSynchronousVerification => true;
    public bool SupportsAsynchronousVerification => false;
    public bool RequiresEndpointAgent => false;

    public Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct) { ... }
    public Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(EnrollmentContext ctx, string response, CancellationToken ct) { ... }
    public Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct) { ... }
    public Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct) { ... }
    public Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct) { ... }
}
```

3. Add the `MfaMethod` enum value in `MfaSrv.Core/Enums/MfaMethod.cs`
4. Register in `Program.cs`:
```csharp
builder.Services.AddSingleton<IMfaProvider, MyMfaProvider>();
```
5. Add unit tests in `tests/MfaSrv.Tests.Unit/Providers/`

## Code Conventions

- **C# style:** Standard .NET conventions, file-scoped namespaces
- **Entity IDs:** String GUIDs (`Guid.NewGuid().ToString()`)
- **Timestamps:** `DateTimeOffset` (UTC)
- **Async:** All I/O operations are async with `CancellationToken` support
- **Logging:** `ILogger<T>` via DI, structured logging
- **Configuration:** `IOptions<T>` / `IOptionsMonitor<T>` pattern
- **Database:** EF Core with SQLite, `AsNoTracking()` for read queries

## gRPC Protocol

Proto files are in `src/Core/MfaSrv.Protocol/Protos/`:

| File | Purpose |
|------|---------|
| `mfa_service.proto` | Server ↔ Agent communication (auth evaluation, enrollment, sessions) |
| `dc_agent.proto` | DC Agent specific services (named pipe proxy) |
| `endpoint_agent.proto` | Endpoint Agent specific services (credential provider proxy) |
| `gossip.proto` | DC-to-DC session synchronization |

Generated C# code is placed in `obj/` during build (standard Grpc.Tools behavior).
