import type {
  DashboardStats,
  HourlyStat,
  PagedResult,
  UserListItem,
  User,
  Policy,
  CreatePolicyRequest,
  SessionListItem,
  AgentRegistration,
  AuditLogEntry,
  EnrollmentListItem,
  BeginEnrollmentRequest,
  BeginEnrollmentResponse,
  CompleteEnrollmentRequest,
  AuditEventType,
} from './types';

// ── Generic helpers ──

class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
    this.name = 'ApiError';
  }
}

async function fetchApi<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  });

  if (!res.ok) {
    const text = await res.text().catch(() => res.statusText);
    throw new ApiError(res.status, text);
  }

  // 204 No Content
  if (res.status === 204) return undefined as unknown as T;

  return res.json() as Promise<T>;
}

// ── Dashboard ──

export function getDashboardStats(): Promise<DashboardStats> {
  return fetchApi<DashboardStats>('/api/dashboard/stats');
}

export function getHourlyStats(hours = 24): Promise<HourlyStat[]> {
  return fetchApi<HourlyStat[]>(`/api/dashboard/stats/hourly?hours=${hours}`);
}

// ── Users ──

export function getUsers(
  page = 1,
  pageSize = 50,
  search = ''
): Promise<PagedResult<UserListItem>> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  if (search) params.set('search', search);
  return fetchApi<PagedResult<UserListItem>>(`/api/users?${params}`);
}

export function getUser(id: string): Promise<User> {
  return fetchApi<User>(`/api/users/${encodeURIComponent(id)}`);
}

export function triggerLdapSync(): Promise<{ message: string }> {
  return fetchApi<{ message: string }>('/api/users/sync', { method: 'POST' });
}

// ── Policies ──

export function getPolicies(): Promise<Policy[]> {
  return fetchApi<Policy[]>('/api/policies');
}

export function createPolicy(body: CreatePolicyRequest): Promise<Policy> {
  return fetchApi<Policy>('/api/policies', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function updatePolicy(
  id: string,
  body: CreatePolicyRequest
): Promise<Policy> {
  return fetchApi<Policy>(`/api/policies/${encodeURIComponent(id)}`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

export function deletePolicy(id: string): Promise<void> {
  return fetchApi<void>(`/api/policies/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
}

export function togglePolicy(
  id: string
): Promise<{ id: string; isEnabled: boolean }> {
  return fetchApi<{ id: string; isEnabled: boolean }>(
    `/api/policies/${encodeURIComponent(id)}/toggle`,
    { method: 'PATCH' }
  );
}

// ── Sessions ──

export function getSessions(
  page = 1,
  pageSize = 50
): Promise<PagedResult<SessionListItem>> {
  return fetchApi<PagedResult<SessionListItem>>(
    `/api/sessions?page=${page}&pageSize=${pageSize}`
  );
}

export function revokeSession(id: string): Promise<void> {
  return fetchApi<void>(`/api/sessions/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
}

// ── Agents ──

export function getAgents(): Promise<AgentRegistration[]> {
  return fetchApi<AgentRegistration[]>('/api/agents');
}

// ── Audit ──

export interface AuditQuery {
  page?: number;
  pageSize?: number;
  userId?: string;
  eventType?: AuditEventType | '';
  from?: string;
  to?: string;
}

export function getAuditLog(
  query: AuditQuery = {}
): Promise<PagedResult<AuditLogEntry>> {
  const params = new URLSearchParams();
  params.set('page', String(query.page ?? 1));
  params.set('pageSize', String(query.pageSize ?? 50));
  if (query.userId) params.set('userId', query.userId);
  if (query.eventType) params.set('eventType', query.eventType);
  if (query.from) params.set('from', query.from);
  if (query.to) params.set('to', query.to);
  return fetchApi<PagedResult<AuditLogEntry>>(`/api/audit?${params}`);
}

// ── Enrollments ──

export function getUserEnrollments(
  userId: string
): Promise<EnrollmentListItem[]> {
  return fetchApi<EnrollmentListItem[]>(
    `/api/enrollments/user/${encodeURIComponent(userId)}`
  );
}

export function beginEnrollment(
  body: BeginEnrollmentRequest
): Promise<BeginEnrollmentResponse> {
  return fetchApi<BeginEnrollmentResponse>('/api/enrollments/begin', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function completeEnrollment(
  body: CompleteEnrollmentRequest
): Promise<{ success: boolean }> {
  return fetchApi<{ success: boolean }>('/api/enrollments/complete', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function revokeEnrollment(id: string): Promise<void> {
  return fetchApi<void>(`/api/enrollments/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });
}

// ── Self-enrollment ──

export function getEnrollmentStatus(
  userId: string
): Promise<{
  userId: string;
  userName: string;
  mfaEnabled: boolean;
  enrollments: EnrollmentListItem[];
}> {
  return fetchApi(
    `/api/self-enrollment/${encodeURIComponent(userId)}/status`
  );
}
