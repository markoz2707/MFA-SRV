# MFA-SRV REST API Reference

Base URL: `http://localhost:5080`

All responses use JSON. Pagination follows the pattern `{ total, page, pageSize, data: [...] }`.

---

## Dashboard

### GET /api/dashboard/stats

Returns aggregated system statistics for the admin dashboard.

**Response:**
```json
{
  "users": { "total": 150, "mfaEnabled": 85 },
  "sessions": { "active": 42 },
  "agents": { "online": 3, "total": 5, "syncSubscribers": 3 },
  "policies": { "active": 4, "total": 6 },
  "last24Hours": {
    "authentications": 1240,
    "mfaChallenges": 380,
    "mfaSuccesses": 365,
    "mfaFailures": 15,
    "denied": 8
  },
  "enrollmentsByMethod": [
    { "method": "Totp", "count": 60 },
    { "method": "Push", "count": 25 }
  ],
  "recentEvents": [...]
}
```

### GET /api/dashboard/stats/hourly?hours=24

Returns audit events grouped by hour for charting. `hours` parameter: 1-168 (max 7 days).

---

## Users

### GET /api/users?page=1&pageSize=50&search=john

Lists users synchronized from Active Directory.

**Query Parameters:**
| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| page | int | 1 | Page number |
| pageSize | int | 50 | Items per page |
| search | string | null | Filter by SAM, UPN, or display name |

**Response:**
```json
{
  "total": 150,
  "page": 1,
  "pageSize": 50,
  "data": [
    {
      "id": "...",
      "samAccountName": "jdoe",
      "userPrincipalName": "jdoe@example.com",
      "displayName": "John Doe",
      "email": "jdoe@example.com",
      "isEnabled": true,
      "mfaEnabled": true,
      "lastAuthAt": "2024-01-15T10:30:00Z",
      "lastSyncAt": "2024-01-15T12:00:00Z",
      "enrollmentCount": 1
    }
  ]
}
```

### GET /api/users/{id}

Returns user details including enrollments and group memberships.

### POST /api/users/sync

Triggers an immediate LDAP synchronization. Returns `{ "message": "Sync completed" }`.

### GET /api/users/sync/test

Tests LDAP connectivity. Returns `{ "connected": true }`.

---

## Policies

### GET /api/policies

Lists all policies ordered by priority. Includes rule groups, rules, and actions.

### GET /api/policies/{id}

Returns a single policy with all nested rule groups, rules, and actions.

### POST /api/policies

Creates a new policy. Notifies connected agents if enabled.

**Request Body:**
```json
{
  "name": "Require MFA for Admins",
  "description": "Enforce TOTP for Domain Admins group",
  "isEnabled": true,
  "priority": 10,
  "failoverMode": "FailOpen",
  "ruleGroups": [
    {
      "order": 1,
      "rules": [
        {
          "ruleType": "SourceGroup",
          "operator": "Equals",
          "value": "S-1-5-21-...-512",
          "negate": false
        }
      ]
    }
  ],
  "actions": [
    {
      "actionType": "RequireMfa",
      "requiredMethod": "Totp"
    }
  ]
}
```

**Rule Types:** `SourceUser`, `SourceGroup`, `SourceIp`, `SourceOu`, `TargetResource`, `AuthProtocol`, `TimeWindow`, `RiskScore`

**Action Types:** `RequireMfa`, `Deny`, `Allow`, `AlertOnly`

**Failover Modes:** `FailOpen`, `FailClose`, `CachedOnly`

**MFA Methods:** `Totp`, `Push`, `Fido2`, `FortiToken`, `Sms`, `Email`

### PUT /api/policies/{id}

Updates an existing policy (full replacement of rules and actions).

### DELETE /api/policies/{id}

Deletes a policy. Notifies agents of the deletion.

### PATCH /api/policies/{id}/toggle

Toggles a policy's enabled state. Returns `{ "id": "...", "isEnabled": true }`.

---

## Enrollments (Admin)

### GET /api/enrollments/user/{userId}

Lists MFA enrollments for a specific user.

**Response:**
```json
[
  {
    "id": "...",
    "method": "Totp",
    "status": "Active",
    "friendlyName": "Google Authenticator",
    "deviceIdentifier": null,
    "createdAt": "2024-01-10T09:00:00Z",
    "activatedAt": "2024-01-10T09:02:00Z",
    "lastUsedAt": "2024-01-15T10:30:00Z"
  }
]
```

### POST /api/enrollments/begin

Begins a new MFA enrollment for a user.

**Request Body:**
```json
{
  "userId": "...",
  "method": "Totp",
  "friendlyName": "My Authenticator"
}
```

**Response:**
```json
{
  "enrollmentId": "...",
  "provisioningUri": "otpauth://totp/MfaSrv:user@example.com?secret=...",
  "qrCodeDataUri": "data:image/png;base64,...",
  "success": true
}
```

### POST /api/enrollments/complete

Completes enrollment by verifying the first OTP code.

**Request Body:**
```json
{
  "enrollmentId": "...",
  "verificationCode": "123456"
}
```

### DELETE /api/enrollments/{id}

Revokes an enrollment. If no active enrollments remain, disables MFA for the user.

---

## Self-Enrollment (End User)

### GET /api/self-enrollment/{userId}/status

Returns enrollment status and available MFA methods for the user.

### POST /api/self-enrollment/{userId}/begin

Begins self-service MFA enrollment.

**Request Body:**
```json
{
  "method": "Totp",
  "friendlyName": "My Phone"
}
```

### POST /api/self-enrollment/{userId}/complete

Completes self-service enrollment with verification code.

### DELETE /api/self-enrollment/{userId}/enrollments/{enrollmentId}

Revokes a self-enrolled MFA method.

### POST /api/self-enrollment/{userId}/test

Tests an active enrollment by issuing a challenge.

---

## Sessions

### GET /api/sessions?page=1&pageSize=50

Lists active MFA sessions.

**Response:**
```json
{
  "total": 42,
  "page": 1,
  "pageSize": 50,
  "data": [
    {
      "id": "...",
      "userId": "...",
      "sourceIp": "192.168.1.100",
      "targetResource": "dc01.example.com",
      "verifiedMethod": "Totp",
      "createdAt": "2024-01-15T10:30:00Z",
      "expiresAt": "2024-01-15T18:30:00Z"
    }
  ]
}
```

### DELETE /api/sessions/{id}

Revokes an active session immediately.

---

## Agents

### GET /api/agents

Lists all registered DC and Endpoint agents.

### GET /api/agents/{id}

Returns details for a specific agent.

### DELETE /api/agents/{id}

Removes an agent registration.

---

## Audit Log

### GET /api/audit?page=1&pageSize=50&userId=...&eventType=...&from=...&to=...

Queries the audit log with optional filters.

**Query Parameters:**
| Parameter | Type | Description |
|-----------|------|-------------|
| page | int | Page number |
| pageSize | int | Items per page |
| userId | string | Filter by user ID |
| eventType | enum | Filter by event type |
| from | DateTimeOffset | Start of time range |
| to | DateTimeOffset | End of time range |

**Event Types:** `AuthenticationAttempt`, `MfaChallengeIssued`, `MfaChallengeVerified`, `MfaChallengeFailed`, `UserEnrolled`, `UserDisenrolled`, `PolicyChanged`, `SessionCreated`, `SessionRevoked`, `AgentRegistered`, `AgentDeregistered`, `SystemEvent`

---

## Backups

### GET /api/backups

Lists available backup files.

**Response:**
```json
{
  "total": 5,
  "data": [
    {
      "fileName": "mfasrv_backup_20240115_120000.db",
      "sizeBytes": 524288,
      "createdUtc": "2024-01-15T12:00:00Z",
      "lastModifiedUtc": "2024-01-15T12:00:01Z"
    }
  ]
}
```

### POST /api/backups

Triggers a manual database backup. Returns `{ "message": "...", "fileName": "..." }`.

### POST /api/backups/restore

Restores from a backup file. Requires two-step confirmation:

**Step 1 - Request token:**
```json
{ "fileName": "mfasrv_backup_20240115_120000.db" }
```
Response includes a `confirmationToken` valid for 5 minutes.

**Step 2 - Confirm restore:**
```json
{
  "fileName": "mfasrv_backup_20240115_120000.db",
  "confirmationToken": "abc123..."
}
```

### GET /api/backups/{filename}/download

Downloads a backup file as `application/x-sqlite3`.

---

## Health & Monitoring

### GET /health

Liveness check. Returns health status of database, leader election, active sessions/agents/policies.

### GET /ready

Readiness check. Returns whether the instance can serve traffic.

### GET /status

Instance status including leader election state and version.

**Response:**
```json
{
  "status": "running",
  "timestamp": "2024-01-15T12:00:00Z",
  "instanceId": "SERVER01-1234",
  "isLeader": true,
  "version": "1.0.0"
}
```

### GET /metrics

Prometheus metrics endpoint. See [monitoring.md](monitoring.md) for metric details.
