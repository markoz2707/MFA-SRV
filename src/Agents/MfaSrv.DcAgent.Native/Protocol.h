#pragma once

// Named Pipe protocol structures for LSA DLL <-> DC Agent communication
// JSON-based for simplicity and debuggability

// Query format (LSA -> DC Agent):
// {
//   "userName": "jsmith",
//   "domain": "CONTOSO",
//   "sourceIp": "10.0.0.5",
//   "workstation": "WS001",
//   "protocol": 1
// }

// Response format (DC Agent -> LSA):
// {
//   "decision": 0,           // 0=Allow, 1=RequireMfa, 2=Deny, 3=Pending
//   "sessionToken": "...",
//   "challengeId": "...",
//   "reason": "...",
//   "timeoutMs": 0
// }

// Protocol constants
#define PROTO_FIELD_USERNAME    "userName"
#define PROTO_FIELD_DOMAIN      "domain"
#define PROTO_FIELD_SOURCEIP    "sourceIp"
#define PROTO_FIELD_WORKSTATION "workstation"
#define PROTO_FIELD_PROTOCOL    "protocol"

#define PROTO_FIELD_DECISION    "decision"
#define PROTO_FIELD_SESSION     "sessionToken"
#define PROTO_FIELD_CHALLENGE   "challengeId"
#define PROTO_FIELD_REASON      "reason"
#define PROTO_FIELD_TIMEOUT     "timeoutMs"

// Auth protocol values
#define PROTO_AUTH_KERBEROS 1
#define PROTO_AUTH_NTLM     2
#define PROTO_AUTH_LDAP     3
#define PROTO_AUTH_RADIUS   4
#define PROTO_AUTH_UNKNOWN  0
