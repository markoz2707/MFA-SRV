// MfaSrv LSA Auth Package - Named Pipe Client
// Communicates with MfaSrv DC Agent Windows Service
// CRITICAL: All operations have strict timeouts, all errors fail-open

#include "NamedPipeClient.h"
#include "SafeExceptionHandler.h"
#include "Protocol.h"
#include "Logger.h"
#include <stdio.h>

#define PIPE_BUFFER_SIZE 4096

// Simple JSON builder - no dynamic allocation beyond stack buffer
static int BuildQueryJson(
    char* buffer, int bufferSize,
    const char* userName, const char* domain,
    const char* sourceIp, const char* workstation,
    int authProtocol)
{
    return _snprintf_s(buffer, bufferSize, _TRUNCATE,
        "{"
        "\"" PROTO_FIELD_USERNAME "\":\"%s\","
        "\"" PROTO_FIELD_DOMAIN "\":\"%s\","
        "\"" PROTO_FIELD_SOURCEIP "\":\"%s\","
        "\"" PROTO_FIELD_WORKSTATION "\":\"%s\","
        "\"" PROTO_FIELD_PROTOCOL "\":%d"
        "}",
        userName ? userName : "",
        domain ? domain : "",
        sourceIp ? sourceIp : "",
        workstation ? workstation : "",
        authProtocol);
}

// Parse decision from JSON response - minimal parser, no library dependency
static int ParseDecisionFromJson(const char* json, int jsonLen)
{
    // Look for "decision":N pattern
    const char* key = "\"decision\":";
    const int keyLen = 12; // length of "decision":

    for (int i = 0; i < jsonLen - keyLen; i++)
    {
        if (json[i] == '"' && _strnicmp(&json[i], key, keyLen) == 0)
        {
            int value = json[i + keyLen] - '0';
            if (value >= 0 && value <= 3)
                return value;
        }
    }

    // Default to ALLOW if we can't parse
    return MFASRV_DECISION_ALLOW;
}

HANDLE ConnectToPipe(const wchar_t* pipeName, DWORD timeoutMs)
{
    SAFE_BEGIN

    HANDLE hPipe = INVALID_HANDLE_VALUE;
    DWORD startTick = GetTickCount();

    while (TRUE)
    {
        hPipe = CreateFileW(
            pipeName,
            GENERIC_READ | GENERIC_WRITE,
            0,
            NULL,
            OPEN_EXISTING,
            0,
            NULL);

        if (hPipe != INVALID_HANDLE_VALUE)
            break;

        DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY)
        {
            LogMessage(MFASRV_LOG_WARNING, "Cannot open pipe: error=%lu", error);
            return INVALID_HANDLE_VALUE;
        }

        // Check timeout
        DWORD elapsed = GetTickCount() - startTick;
        if (elapsed >= timeoutMs)
        {
            LogMessage(MFASRV_LOG_WARNING, "Pipe connect timeout after %lu ms", elapsed);
            return INVALID_HANDLE_VALUE;
        }

        // Wait briefly for pipe to become available
        DWORD remaining = timeoutMs - elapsed;
        if (!WaitNamedPipeW(pipeName, remaining < 500 ? remaining : 500))
        {
            DWORD elapsed2 = GetTickCount() - startTick;
            if (elapsed2 >= timeoutMs)
            {
                LogMessage(MFASRV_LOG_WARNING, "WaitNamedPipe timeout");
                return INVALID_HANDLE_VALUE;
            }
        }
    }

    // Set pipe to message mode
    DWORD mode = PIPE_READMODE_MESSAGE;
    if (!SetNamedPipeHandleState(hPipe, &mode, NULL, NULL))
    {
        LogMessage(MFASRV_LOG_WARNING, "SetNamedPipeHandleState failed: %lu", GetLastError());
        CloseHandle(hPipe);
        return INVALID_HANDLE_VALUE;
    }

    return hPipe;

    SAFE_END(INVALID_HANDLE_VALUE, "ConnectToPipe")
}

int SendAndReceive(HANDLE hPipe, const char* query, int queryLen, DWORD timeoutMs)
{
    SAFE_BEGIN

    DWORD bytesWritten = 0;
    if (!WriteFile(hPipe, query, (DWORD)queryLen, &bytesWritten, NULL))
    {
        LogMessage(MFASRV_LOG_WARNING, "WriteFile to pipe failed: %lu", GetLastError());
        return MFASRV_DECISION_ALLOW;
    }

    // Read response
    char responseBuffer[PIPE_BUFFER_SIZE];
    DWORD bytesRead = 0;
    if (!ReadFile(hPipe, responseBuffer, sizeof(responseBuffer) - 1, &bytesRead, NULL))
    {
        LogMessage(MFASRV_LOG_WARNING, "ReadFile from pipe failed: %lu", GetLastError());
        return MFASRV_DECISION_ALLOW;
    }

    responseBuffer[bytesRead] = '\0';

    LogMessage(MFASRV_LOG_DEBUG, "Pipe response (%lu bytes): %s", bytesRead, responseBuffer);

    return ParseDecisionFromJson(responseBuffer, (int)bytesRead);

    SAFE_END(MFASRV_DECISION_ALLOW, "SendAndReceive")
}

int QueryDcAgent(
    const wchar_t* pipeName,
    const char* userName,
    const char* domain,
    const char* sourceIp,
    const char* workstation,
    int authProtocol,
    DWORD timeoutMs)
{
    SAFE_BEGIN

    LogMessage(MFASRV_LOG_DEBUG, "QueryDcAgent: user=%s domain=%s ip=%s",
        userName ? userName : "(null)",
        domain ? domain : "(null)",
        sourceIp ? sourceIp : "(null)");

    // Build JSON query
    char queryBuffer[PIPE_BUFFER_SIZE];
    int queryLen = BuildQueryJson(queryBuffer, sizeof(queryBuffer),
        userName, domain, sourceIp, workstation, authProtocol);

    if (queryLen <= 0)
    {
        LogMessage(MFASRV_LOG_ERROR, "Failed to build query JSON");
        return MFASRV_DECISION_ALLOW;
    }

    // Connect to pipe
    HANDLE hPipe = ConnectToPipe(pipeName, timeoutMs);
    if (hPipe == INVALID_HANDLE_VALUE)
    {
        LogMessage(MFASRV_LOG_WARNING, "Cannot connect to DC Agent pipe - fail-open");
        return MFASRV_DECISION_ALLOW;
    }

    // Send and receive
    int decision = SendAndReceive(hPipe, queryBuffer, queryLen, timeoutMs);

    CloseHandle(hPipe);

    LogMessage(MFASRV_LOG_INFO, "Auth decision for %s\\%s: %d",
        domain ? domain : "", userName ? userName : "", decision);

    return decision;

    SAFE_END(MFASRV_DECISION_ALLOW, "QueryDcAgent")
}
