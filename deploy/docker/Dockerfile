# =============================================================================
# MfaSrv Central Server - Multi-Stage Docker Build
#
# Build:   docker build -t mfasrv-server -f deploy/docker/Dockerfile .
#          (run from the repository root)
#
# Run:     docker run -d -p 5080:5080 -p 5081:5081 -v mfasrv-data:/data mfasrv-server
# =============================================================================

# ---------------------------------------------------------------------------
# Stage 1: Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY MfaSrv.sln ./
COPY src/Core/MfaSrv.Core/MfaSrv.Core.csproj                             src/Core/MfaSrv.Core/
COPY src/Core/MfaSrv.Cryptography/MfaSrv.Cryptography.csproj             src/Core/MfaSrv.Cryptography/
COPY src/Core/MfaSrv.Protocol/MfaSrv.Protocol.csproj                     src/Core/MfaSrv.Protocol/
COPY src/Server/MfaSrv.Server/MfaSrv.Server.csproj                       src/Server/MfaSrv.Server/
COPY src/Providers/MfaSrv.Provider.Totp/MfaSrv.Provider.Totp.csproj       src/Providers/MfaSrv.Provider.Totp/
COPY src/Providers/MfaSrv.Provider.Push/MfaSrv.Provider.Push.csproj       src/Providers/MfaSrv.Provider.Push/
COPY src/Providers/MfaSrv.Provider.Fido2/MfaSrv.Provider.Fido2.csproj     src/Providers/MfaSrv.Provider.Fido2/
COPY src/Providers/MfaSrv.Provider.FortiToken/MfaSrv.Provider.FortiToken.csproj src/Providers/MfaSrv.Provider.FortiToken/
COPY src/Providers/MfaSrv.Provider.Sms/MfaSrv.Provider.Sms.csproj         src/Providers/MfaSrv.Provider.Sms/
COPY src/Providers/MfaSrv.Provider.Email/MfaSrv.Provider.Email.csproj     src/Providers/MfaSrv.Provider.Email/

# Restore dependencies (cached unless csproj files change)
RUN dotnet restore src/Server/MfaSrv.Server/MfaSrv.Server.csproj

# Copy full source
COPY src/ src/

# Build and publish
RUN dotnet publish src/Server/MfaSrv.Server/MfaSrv.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---------------------------------------------------------------------------
# Stage 2: Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Security: run as non-root user
RUN groupadd --gid 1000 mfasrv && \
    useradd --uid 1000 --gid mfasrv --shell /bin/false --create-home mfasrv

WORKDIR /app
COPY --from=build /app/publish .

# Create data directory for SQLite database
RUN mkdir -p /data && chown mfasrv:mfasrv /data

# Volume for persistent data (SQLite DB, logs)
VOLUME ["/data"]

# Expose HTTP (REST API + admin portal) and gRPC (agent communication)
EXPOSE 5080 5081

# Environment variables for runtime configuration
# These can be overridden via docker run -e or docker-compose environment
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS="" \
    ConnectionStrings__DefaultConnection="Data Source=/data/mfasrv.db" \
    Kestrel__Endpoints__Http__Url="http://0.0.0.0:5080" \
    Kestrel__Endpoints__Grpc__Url="http://0.0.0.0:5081" \
    Kestrel__Endpoints__Grpc__Protocols="Http2" \
    Logging__LogLevel__Default="Information"

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:5080/health || exit 1

# Run as non-root
USER mfasrv

ENTRYPOINT ["dotnet", "MfaSrv.Server.dll"]
