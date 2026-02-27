// MfaSrv LSA Auth Package - Logging
// Lightweight logging for LSASS-hosted DLL
// Uses Windows Event Log for production, OutputDebugString for debug

#include "Logger.h"
#include <stdio.h>
#include <stdarg.h>

static HANDLE g_EventSource = NULL;
static int g_LogLevel = MFASRV_LOG_INFO;

#define MAX_LOG_MESSAGE 1024

void LogInit()
{
    __try
    {
        g_EventSource = RegisterEventSourceW(NULL, L"MfaSrvLsaAuth");

        // Check registry for debug log level
        HKEY hKey = NULL;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\MfaSrv\\DcAgent",
            0, KEY_READ, &hKey) == ERROR_SUCCESS)
        {
            DWORD logLevel = 0;
            DWORD size = sizeof(DWORD);
            if (RegQueryValueExW(hKey, L"LogLevel", NULL, NULL,
                (LPBYTE)&logLevel, &size) == ERROR_SUCCESS)
            {
                g_LogLevel = (int)logLevel;
            }
            RegCloseKey(hKey);
        }
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        // Silently fail - we cannot crash in LSASS
    }
}

void LogShutdown()
{
    __try
    {
        if (g_EventSource != NULL)
        {
            DeregisterEventSource(g_EventSource);
            g_EventSource = NULL;
        }
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        // Silently fail
    }
}

void LogMessage(int level, const char* format, ...)
{
    __try
    {
        if (level > g_LogLevel)
            return;

        char buffer[MAX_LOG_MESSAGE];
        va_list args;
        va_start(args, format);
        int written = _vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, format, args);
        va_end(args);

        if (written <= 0)
            return;

        // OutputDebugString for all messages in debug builds
#ifdef _DEBUG
        OutputDebugStringA("[MfaSrvLsa] ");
        OutputDebugStringA(buffer);
        OutputDebugStringA("\n");
#endif

        // Event Log for warnings and errors
        if (g_EventSource != NULL && level <= MFASRV_LOG_WARNING)
        {
            wchar_t wBuffer[MAX_LOG_MESSAGE];
            MultiByteToWideChar(CP_UTF8, 0, buffer, -1, wBuffer, MAX_LOG_MESSAGE);

            LPCWSTR strings[1] = { wBuffer };
            WORD eventType = (level == MFASRV_LOG_ERROR) ? EVENTLOG_ERROR_TYPE : EVENTLOG_WARNING_TYPE;

            ReportEventW(g_EventSource, eventType, 0, 1000 + level,
                NULL, 1, 0, strings, NULL);
        }
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        // Never crash
    }
}

void LogMessageW(int level, const wchar_t* format, ...)
{
    __try
    {
        if (level > g_LogLevel)
            return;

        wchar_t wBuffer[MAX_LOG_MESSAGE];
        va_list args;
        va_start(args, format);
        _vsnwprintf_s(wBuffer, MAX_LOG_MESSAGE, _TRUNCATE, format, args);
        va_end(args);

#ifdef _DEBUG
        OutputDebugStringW(L"[MfaSrvLsa] ");
        OutputDebugStringW(wBuffer);
        OutputDebugStringW(L"\n");
#endif

        if (g_EventSource != NULL && level <= MFASRV_LOG_WARNING)
        {
            LPCWSTR strings[1] = { wBuffer };
            WORD eventType = (level == MFASRV_LOG_ERROR) ? EVENTLOG_ERROR_TYPE : EVENTLOG_WARNING_TYPE;

            ReportEventW(g_EventSource, eventType, 0, 1000 + level,
                NULL, 1, 0, strings, NULL);
        }
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        // Never crash
    }
}
