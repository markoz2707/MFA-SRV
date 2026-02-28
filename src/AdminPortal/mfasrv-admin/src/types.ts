// ── Enums (string unions matching C# enums) ──

export type MfaMethod = 'Totp' | 'Push' | 'Fido2' | 'FortiToken' | 'Sms' | 'Email';

export type EnrollmentStatus = 'Pending' | 'Active' | 'Disabled' | 'Revoked';

export type PolicyActionType = 'RequireMfa' | 'Deny' | 'Allow' | 'AlertOnly';

export type FailoverMode = 'FailOpen' | 'FailClose' | 'CachedOnly';

export type PolicyRuleType =
  | 'SourceUser'
  | 'SourceGroup'
  | 'SourceIp'
  | 'SourceOu'
  | 'TargetResource'
  | 'AuthProtocol'
  | 'TimeWindow'
  | 'RiskScore';

export type SessionStatus = 'Active' | 'Expired' | 'Revoked';

export type AgentType = 'DcAgent' | 'EndpointAgent';

export type AgentStatus = 'Online' | 'Offline' | 'Degraded';

export type AuditEventType =
  | 'AuthenticationAttempt'
  | 'MfaChallengeIssued'
  | 'MfaChallengeVerified'
  | 'MfaChallengeFailed'
  | 'MfaChallengeExpired'
  | 'PolicyEvaluated'
  | 'SessionCreated'
  | 'SessionExpired'
  | 'SessionRevoked'
  | 'UserEnrolled'
  | 'UserDisenrolled'
  | 'PolicyCreated'
  | 'PolicyUpdated'
  | 'PolicyDeleted'
  | 'AgentRegistered'
  | 'AgentHeartbeat'
  | 'AgentDisconnected'
  | 'FailoverActivated'
  | 'FailoverDeactivated'
  | 'ConfigurationChanged';

// ── Entities ──

export interface User {
  id: string;
  objectGuid: string;
  samAccountName: string;
  userPrincipalName: string;
  displayName: string;
  email: string | null;
  phoneNumber: string | null;
  distinguishedName: string;
  isEnabled: boolean;
  mfaEnabled: boolean;
  createdAt: string;
  updatedAt: string;
  lastSyncAt: string | null;
  lastAuthAt: string | null;
  enrollments: MfaEnrollment[];
  groupMemberships: UserGroupMembership[];
}

export interface UserGroupMembership {
  id: string;
  userId: string;
  groupSid: string;
  groupName: string;
  groupDn: string;
  syncedAt: string;
}

export interface MfaEnrollment {
  id: string;
  userId: string;
  method: MfaMethod;
  status: EnrollmentStatus;
  deviceIdentifier: string | null;
  friendlyName: string | null;
  createdAt: string;
  activatedAt: string | null;
  lastUsedAt: string | null;
}

export interface Policy {
  id: string;
  name: string;
  description: string | null;
  isEnabled: boolean;
  priority: number;
  failoverMode: FailoverMode;
  createdAt: string;
  updatedAt: string;
  ruleGroups: PolicyRuleGroup[];
  actions: PolicyAction[];
}

export interface PolicyRuleGroup {
  id: string;
  policyId: string;
  order: number;
  rules: PolicyRule[];
}

export interface PolicyRule {
  id: string;
  ruleGroupId: string;
  ruleType: PolicyRuleType;
  operator: string;
  value: string;
  negate: boolean;
}

export interface PolicyAction {
  id: string;
  policyId: string;
  actionType: PolicyActionType;
  requiredMethod: MfaMethod | null;
}

export interface MfaSession {
  id: string;
  userId: string;
  sourceIp: string;
  targetResource: string | null;
  verifiedMethod: MfaMethod;
  status: SessionStatus;
  createdAt: string;
  expiresAt: string;
  dcAgentId: string | null;
}

export interface AuditLogEntry {
  id: number;
  eventType: AuditEventType;
  userId: string | null;
  userName: string | null;
  sourceIp: string | null;
  targetResource: string | null;
  details: string | null;
  success: boolean;
  agentId: string | null;
  timestamp: string;
}

export interface AgentRegistration {
  id: string;
  agentType: AgentType;
  hostname: string;
  ipAddress: string | null;
  status: AgentStatus;
  certificateThumbprint: string | null;
  version: string | null;
  registeredAt: string;
  lastHeartbeatAt: string | null;
}

// ── API response shapes ──

export interface PagedResult<T> {
  total: number;
  page: number;
  pageSize: number;
  data: T[];
}

/** Projected user row returned by GET /api/users */
export interface UserListItem {
  id: string;
  samAccountName: string;
  userPrincipalName: string;
  displayName: string;
  email: string | null;
  isEnabled: boolean;
  mfaEnabled: boolean;
  lastAuthAt: string | null;
  lastSyncAt: string | null;
  enrollmentCount: number;
}

/** Projected session row returned by GET /api/sessions */
export interface SessionListItem {
  id: string;
  userId: string;
  sourceIp: string;
  targetResource: string | null;
  verifiedMethod: MfaMethod;
  createdAt: string;
  expiresAt: string;
}

/** Projected enrollment row returned by GET /api/enrollments/user/{id} */
export interface EnrollmentListItem {
  id: string;
  method: MfaMethod;
  status: EnrollmentStatus;
  friendlyName: string | null;
  deviceIdentifier: string | null;
  createdAt: string;
  activatedAt: string | null;
  lastUsedAt: string | null;
}

// ── Dashboard stats (designed for the described endpoint) ──

export interface DashboardStats {
  totalUsers: number;
  mfaEnabledUsers: number;
  activeSessions: number;
  onlineAgents: number;
  last24h: {
    authentications: number;
    mfaChallenges: number;
    successes: number;
    failures: number;
  };
  enrollmentsByMethod: Record<string, number>;
  recentEvents: AuditLogEntry[];
}

export interface HourlyStat {
  hour: string;
  authentications: number;
  mfaChallenges: number;
  successes: number;
  failures: number;
}

// ── Backup types ──

export interface BackupFileInfo {
  fileName: string;
  sizeBytes: number;
  createdUtc: string;
  lastModifiedUtc: string;
}

export interface BackupListResponse {
  total: number;
  data: BackupFileInfo[];
}

// ── Request DTOs ──

export interface CreatePolicyRequest {
  name: string;
  description: string | null;
  isEnabled: boolean;
  priority: number;
  failoverMode: FailoverMode;
  ruleGroups: RuleGroupRequest[] | null;
  actions: ActionRequest[] | null;
}

export interface RuleGroupRequest {
  order: number;
  rules: RuleRequest[] | null;
}

export interface RuleRequest {
  ruleType: PolicyRuleType;
  operator: string;
  value: string;
  negate: boolean;
}

export interface ActionRequest {
  actionType: PolicyActionType;
  requiredMethod: MfaMethod | null;
}

export interface BeginEnrollmentRequest {
  userId: string;
  method: MfaMethod;
  friendlyName: string | null;
}

export interface CompleteEnrollmentRequest {
  enrollmentId: string;
  verificationCode: string;
}

export interface BeginEnrollmentResponse {
  enrollmentId: string;
  provisioningUri: string | null;
  qrCodeDataUri: string | null;
  success: boolean;
}
