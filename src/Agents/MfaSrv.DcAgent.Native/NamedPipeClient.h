#pragma once

#include <windows.h>

// Named Pipe client for communicating with DC Agent Windows Service
// All operations have strict timeouts to prevent LSASS blocking

// Query the DC Agent for an authentication decision
// Returns: auth decision code (MFASRV_DECISION_*)
// On any error, returns MFASRV_DECISION_ALLOW (fail-open)
int QueryDcAgent(
    const wchar_t* pipeName,
    const char* userName,
    const char* domain,
    const char* sourceIp,
    const char* workstation,
    int authProtocol,
    DWORD timeoutMs
);

// Internal: Connect to named pipe with timeout
HANDLE ConnectToPipe(const wchar_t* pipeName, DWORD timeoutMs);

// Internal: Send query and receive response
int SendAndReceive(HANDLE hPipe, const char* query, int queryLen, DWORD timeoutMs);
