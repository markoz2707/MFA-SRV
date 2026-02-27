# MFA-SRV Monitoring Guide

## Health Endpoints

| Endpoint | Purpose | Tags |
|----------|---------|------|
| `GET /health` | Liveness check - database, leader status, system counts | `live` |
| `GET /ready` | Readiness check - can this instance serve traffic | `ready` |
| `GET /status` | Detailed instance status (JSON) | - |
| `GET /metrics` | Prometheus metrics (text/plain) | - |

### Health Response Format

```json
{
  "status": "Healthy",
  "timestamp": "2024-01-15T12:00:00Z",
  "duration": 12.5,
  "checks": [
    {
      "name": "mfasrv",
      "status": "Healthy",
      "duration": 8.2,
      "data": {
        "database": "connected",
        "is_leader": true,
        "instance_id": "SERVER01-1234",
        "active_sessions": 42,
        "active_agents": 3,
        "active_policies": 4
      }
    },
    {
      "name": "database",
      "status": "Healthy",
      "duration": 2.1,
      "data": {}
    }
  ]
}
```

---

## Prometheus Metrics

All metrics are prefixed with `mfasrv_`.

### Authentication Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_auth_evaluations_total` | Counter | `decision` | Total authentication evaluations |
| `mfasrv_auth_evaluation_duration_seconds` | Histogram | - | Evaluation latency |

**Decision labels:** `allow`, `deny`, `require_mfa`, `pending`

### MFA Challenge Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_mfa_challenges_issued_total` | Counter | `method` | Total challenges issued |
| `mfasrv_mfa_verifications_total` | Counter | `method`, `result` | Verification attempts |
| `mfasrv_mfa_verification_duration_seconds` | Histogram | `method` | Verification latency |

**Method labels:** `TOTP`, `PUSH`, `FIDO2`, `FORTITOKEN`, `SMS`, `EMAIL`
**Result labels:** `success`, `failure`

### Session Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_active_sessions` | Gauge | - | Currently active sessions |
| `mfasrv_sessions_created_total` | Counter | - | Total sessions created |
| `mfasrv_sessions_revoked_total` | Counter | - | Total sessions revoked |
| `mfasrv_sessions_expired_total` | Counter | - | Sessions expired during cleanup |

### Agent Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_registered_agents` | Gauge | `type` | Registered agents count |
| `mfasrv_agent_heartbeats_total` | Counter | `agent_id` | Heartbeats received |

**Type labels:** `dc`, `endpoint`

### Policy Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_active_policies` | Gauge | - | Active policies count |
| `mfasrv_policy_evaluations_total` | Counter | `action` | Policy evaluation results |

**Action labels:** `require_mfa`, `deny`, `allow`, `alert_only`

### gRPC Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_grpc_calls_total` | Counter | `method`, `status` | gRPC calls received |
| `mfasrv_grpc_call_duration_seconds` | Histogram | `method` | gRPC call latency |

### Database Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_db_backups_total` | Counter | `result` | Backup operations |
| `mfasrv_db_size_bytes` | Gauge | - | Database file size |

### HA Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_is_leader` | Gauge | - | 1 = leader, 0 = standby |
| `mfasrv_leader_elections_total` | Counter | - | Leadership transitions |

### Enrollment Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mfasrv_enrollments_total` | Counter | `method`, `result` | MFA enrollment operations |
| `mfasrv_enrolled_users` | Gauge | - | Users with active enrollments |

---

## Prometheus Configuration

Add to `prometheus.yml`:

```yaml
scrape_configs:
  - job_name: 'mfasrv'
    scrape_interval: 15s
    static_configs:
      - targets: ['mfasrv-server:5080']
    metrics_path: /metrics
```

For HA deployments, scrape all instances:

```yaml
scrape_configs:
  - job_name: 'mfasrv'
    scrape_interval: 15s
    static_configs:
      - targets:
        - 'mfasrv-01:5080'
        - 'mfasrv-02:5080'
```

---

## Alerting Rules

Suggested Prometheus alerting rules:

```yaml
groups:
  - name: mfasrv
    rules:
      # No leader elected
      - alert: MfaSrvNoLeader
        expr: sum(mfasrv_is_leader) == 0
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: "No MFA-SRV leader instance"

      # High MFA failure rate
      - alert: MfaSrvHighFailureRate
        expr: >
          rate(mfasrv_mfa_verifications_total{result="failure"}[5m])
          / rate(mfasrv_mfa_verifications_total[5m]) > 0.2
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "MFA failure rate above 20%"

      # No active agents
      - alert: MfaSrvNoAgents
        expr: mfasrv_registered_agents{type="dc"} == 0
        for: 5m
        labels:
          severity: critical
        annotations:
          summary: "No DC agents connected"

      # Database backup failing
      - alert: MfaSrvBackupFailing
        expr: increase(mfasrv_db_backups_total{result="failure"}[24h]) > 0
        labels:
          severity: warning
        annotations:
          summary: "Database backup failed in the last 24 hours"

      # Auth evaluation latency
      - alert: MfaSrvHighLatency
        expr: histogram_quantile(0.99, rate(mfasrv_auth_evaluation_duration_seconds_bucket[5m])) > 0.1
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "Auth evaluation p99 latency above 100ms"
```

---

## Grafana Dashboard

Import the following panels for a comprehensive dashboard:

### Overview Row
- **Active Sessions** (Stat): `mfasrv_active_sessions`
- **Online Agents** (Stat): `sum(mfasrv_registered_agents)`
- **Active Policies** (Stat): `mfasrv_active_policies`
- **Leader Status** (Stat): `mfasrv_is_leader`
- **DB Size** (Stat): `mfasrv_db_size_bytes`

### Authentication Row
- **Auth Evaluations/sec** (Graph): `rate(mfasrv_auth_evaluations_total[5m])`
- **Auth Evaluation Latency** (Heatmap): `mfasrv_auth_evaluation_duration_seconds_bucket`
- **Decisions by Type** (Pie): `increase(mfasrv_auth_evaluations_total[24h])`

### MFA Row
- **MFA Challenges/sec** (Graph): `rate(mfasrv_mfa_challenges_issued_total[5m])`
- **MFA Success Rate** (Gauge): `rate(mfasrv_mfa_verifications_total{result="success"}[1h]) / rate(mfasrv_mfa_verifications_total[1h])`
- **Verification Latency by Method** (Graph): `histogram_quantile(0.95, rate(mfasrv_mfa_verification_duration_seconds_bucket[5m]))`

### Infrastructure Row
- **Agent Heartbeats/sec** (Graph): `rate(mfasrv_agent_heartbeats_total[5m])`
- **gRPC Calls/sec** (Graph): `rate(mfasrv_grpc_calls_total[5m])`
- **Leader Elections** (Graph): `increase(mfasrv_leader_elections_total[1h])`
- **Backup Status** (Table): `increase(mfasrv_db_backups_total[24h])`
