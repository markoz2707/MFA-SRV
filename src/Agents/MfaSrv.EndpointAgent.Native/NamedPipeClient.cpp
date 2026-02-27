// MfaSrv Named Pipe Client Implementation
// All functions wrapped in SEH for safety in LogonUI.exe context.
// No C++ exceptions, no dynamic allocation beyond stack buffers.

#include "NamedPipeClient.h"

#pragma warning(push)
#pragma warning(disable: 4091) // 'typedef ': ignored on left of '' when no variable is declared

#define MFASRV_PIPE_NAME_A      "\\\\.\\pipe\\MfaSrvEndpointAgent"
#define MFASRV_PIPE_NAME_W      L"\\\\.\\pipe\\MfaSrvEndpointAgent"
#define MFASRV_PIPE_TIMEOUT_MS  3000

// ---------------------------------------------------------------------------
// MfaPipeConnect
// ---------------------------------------------------------------------------
HRESULT MfaPipeConnect(HANDLE* phPipe)
{
    __try
    {
        if (!phPipe)
            return E_INVALIDARG;

        *phPipe = INVALID_HANDLE_VALUE;

        // Wait for the pipe to become available (up to timeout)
        if (!WaitNamedPipeW(MFASRV_PIPE_NAME_W, MFASRV_PIPE_TIMEOUT_MS))
        {
            // Pipe not available within timeout - fail open
            return E_FAIL;
        }

        HANDLE hPipe = CreateFileW(
            MFASRV_PIPE_NAME_W,
            GENERIC_READ | GENERIC_WRITE,
            0,
            NULL,
            OPEN_EXISTING,
            0,
            NULL);

        if (hPipe == INVALID_HANDLE_VALUE)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        // Set the pipe to message-read mode
        DWORD dwMode = PIPE_READMODE_MESSAGE;
        if (!SetNamedPipeHandleState(hPipe, &dwMode, NULL, NULL))
        {
            // Fall back to byte mode - still usable
            dwMode = PIPE_READMODE_BYTE;
            SetNamedPipeHandleState(hPipe, &dwMode, NULL, NULL);
        }

        *phPipe = hPipe;
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// MfaPipeSend
// ---------------------------------------------------------------------------
HRESULT MfaPipeSend(HANDLE hPipe, const char* pszJson, DWORD cbJson)
{
    __try
    {
        if (hPipe == INVALID_HANDLE_VALUE || !pszJson)
            return E_INVALIDARG;

        DWORD cbWritten = 0;
        if (!WriteFile(hPipe, pszJson, cbJson, &cbWritten, NULL))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        if (cbWritten != cbJson)
        {
            return E_FAIL;
        }

        // Flush to ensure the message is sent immediately
        FlushFileBuffers(hPipe);

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// MfaPipeRead
// ---------------------------------------------------------------------------
HRESULT MfaPipeRead(HANDLE hPipe, char* pszBuffer, DWORD cbBuffer, DWORD* pcbRead)
{
    __try
    {
        if (hPipe == INVALID_HANDLE_VALUE || !pszBuffer || cbBuffer == 0)
            return E_INVALIDARG;

        pszBuffer[0] = '\0';

        DWORD cbTotalRead = 0;
        BOOL bSuccess = FALSE;

        // Read in a loop to handle MESSAGE_MORE_DATA
        while (cbTotalRead < cbBuffer - 1)
        {
            DWORD cbRead = 0;
            bSuccess = ReadFile(
                hPipe,
                pszBuffer + cbTotalRead,
                cbBuffer - 1 - cbTotalRead,
                &cbRead,
                NULL);

            cbTotalRead += cbRead;

            if (bSuccess)
            {
                // Complete message received
                break;
            }

            DWORD dwErr = GetLastError();
            if (dwErr == ERROR_MORE_DATA)
            {
                // More data in this message, continue reading
                continue;
            }
            else
            {
                // Actual error
                pszBuffer[cbTotalRead] = '\0';
                if (pcbRead)
                    *pcbRead = cbTotalRead;
                return HRESULT_FROM_WIN32(dwErr);
            }
        }

        pszBuffer[cbTotalRead] = '\0';
        if (pcbRead)
            *pcbRead = cbTotalRead;

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// MfaPipeClose
// ---------------------------------------------------------------------------
void MfaPipeClose(HANDLE hPipe)
{
    __try
    {
        if (hPipe != INVALID_HANDLE_VALUE && hPipe != NULL)
        {
            CloseHandle(hPipe);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // Swallow - nothing we can do
    }
}

// ---------------------------------------------------------------------------
// JsonAppendEscaped - Append a JSON-escaped string value to buffer
// ---------------------------------------------------------------------------
void JsonAppendEscaped(char* pszBuf, int cbBuf, int* piPos, const char* pszValue)
{
    __try
    {
        if (!pszBuf || !piPos || !pszValue)
            return;

        int pos = *piPos;
        for (const char* p = pszValue; *p && pos < cbBuf - 2; p++)
        {
            switch (*p)
            {
            case '"':  if (pos < cbBuf - 3) { pszBuf[pos++] = '\\'; pszBuf[pos++] = '"'; } break;
            case '\\': if (pos < cbBuf - 3) { pszBuf[pos++] = '\\'; pszBuf[pos++] = '\\'; } break;
            case '\n': if (pos < cbBuf - 3) { pszBuf[pos++] = '\\'; pszBuf[pos++] = 'n'; } break;
            case '\r': if (pos < cbBuf - 3) { pszBuf[pos++] = '\\'; pszBuf[pos++] = 'r'; } break;
            case '\t': if (pos < cbBuf - 3) { pszBuf[pos++] = '\\'; pszBuf[pos++] = 't'; } break;
            default:   pszBuf[pos++] = *p; break;
            }
        }
        pszBuf[pos] = '\0';
        *piPos = pos;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // Swallow
    }
}

// ---------------------------------------------------------------------------
// JsonAppendRaw - Append raw string without escaping
// ---------------------------------------------------------------------------
void JsonAppendRaw(char* pszBuf, int cbBuf, int* piPos, const char* pszRaw)
{
    __try
    {
        if (!pszBuf || !piPos || !pszRaw)
            return;

        int pos = *piPos;
        for (const char* p = pszRaw; *p && pos < cbBuf - 1; p++)
            pszBuf[pos++] = *p;
        pszBuf[pos] = '\0';
        *piPos = pos;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // Swallow
    }
}

// ---------------------------------------------------------------------------
// JsonGetString
// Minimal JSON parser: finds "key":"value" and extracts value.
// Handles escaped quotes within values.
// No dynamic allocation.
// ---------------------------------------------------------------------------
BOOL JsonGetString(const char* pszJson, const char* pszKey, char* pszOut, DWORD cchOut)
{
    __try
    {
        if (!pszJson || !pszKey || !pszOut || cchOut == 0)
            return FALSE;

        pszOut[0] = '\0';

        // Build the search pattern: "key":"
        char szPattern[256];
        int iPattern = 0;

        szPattern[iPattern++] = '"';
        for (const char* p = pszKey; *p && iPattern < 250; p++)
            szPattern[iPattern++] = *p;
        szPattern[iPattern++] = '"';
        szPattern[iPattern++] = ':';
        szPattern[iPattern++] = '"';
        szPattern[iPattern] = '\0';

        // Find the pattern in the JSON
        const char* pFound = NULL;
        for (const char* p = pszJson; *p; p++)
        {
            BOOL bMatch = TRUE;
            for (int i = 0; szPattern[i]; i++)
            {
                if (p[i] != szPattern[i])
                {
                    bMatch = FALSE;
                    break;
                }
            }
            if (bMatch)
            {
                pFound = p + iPattern;
                break;
            }
        }

        // Also try with space after colon: "key": "
        if (!pFound)
        {
            iPattern = 0;
            szPattern[iPattern++] = '"';
            for (const char* p = pszKey; *p && iPattern < 248; p++)
                szPattern[iPattern++] = *p;
            szPattern[iPattern++] = '"';
            szPattern[iPattern++] = ':';
            szPattern[iPattern++] = ' ';
            szPattern[iPattern++] = '"';
            szPattern[iPattern] = '\0';

            for (const char* p = pszJson; *p; p++)
            {
                BOOL bMatch = TRUE;
                for (int i = 0; szPattern[i]; i++)
                {
                    if (p[i] != szPattern[i])
                    {
                        bMatch = FALSE;
                        break;
                    }
                }
                if (bMatch)
                {
                    pFound = p + iPattern;
                    break;
                }
            }
        }

        if (!pFound)
            return FALSE;

        // Extract value until unescaped closing quote
        DWORD iOut = 0;
        const char* p = pFound;
        while (*p && iOut < cchOut - 1)
        {
            if (*p == '\\' && *(p + 1))
            {
                // Escaped character
                p++;
                switch (*p)
                {
                case '"':  pszOut[iOut++] = '"'; break;
                case '\\': pszOut[iOut++] = '\\'; break;
                case '/':  pszOut[iOut++] = '/'; break;
                case 'n':  pszOut[iOut++] = '\n'; break;
                case 'r':  pszOut[iOut++] = '\r'; break;
                case 't':  pszOut[iOut++] = '\t'; break;
                default:   pszOut[iOut++] = *p; break;
                }
                p++;
            }
            else if (*p == '"')
            {
                // End of value
                break;
            }
            else
            {
                pszOut[iOut++] = *p;
                p++;
            }
        }

        pszOut[iOut] = '\0';
        return (iOut > 0) ? TRUE : FALSE;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return FALSE;
    }
}

#pragma warning(pop)
