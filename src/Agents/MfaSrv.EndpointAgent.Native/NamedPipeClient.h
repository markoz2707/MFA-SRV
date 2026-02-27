#pragma once

// MfaSrv Named Pipe Client
// Communicates with the MfaSrv Endpoint Agent Windows service via
// named pipe \\.\pipe\MfaSrvEndpointAgent using JSON messages.
// All functions are SEH-safe and designed for use in LogonUI.exe.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// Connect to the Endpoint Agent pipe with MFASRV_PIPE_TIMEOUT_MS timeout.
// Returns S_OK on success, sets *phPipe to valid handle.
// Returns E_FAIL on timeout or error (fail-open semantics).
HRESULT MfaPipeConnect(HANDLE* phPipe);

// Send a JSON message to the pipe.
// pszJson: null-terminated UTF-8 JSON string.
// cbJson: byte length of pszJson (excluding null terminator).
HRESULT MfaPipeSend(HANDLE hPipe, const char* pszJson, DWORD cbJson);

// Read a JSON response from the pipe.
// pszBuffer: output buffer for null-terminated UTF-8 JSON string.
// cbBuffer: size of pszBuffer in bytes.
// pcbRead: optional, receives number of bytes actually read.
HRESULT MfaPipeRead(HANDLE hPipe, char* pszBuffer, DWORD cbBuffer, DWORD* pcbRead);

// Close the pipe handle.
void MfaPipeClose(HANDLE hPipe);

// Simple JSON string value extraction.
// Searches pszJson for "key":"value" and copies value to pszOut.
// Returns TRUE if found, FALSE otherwise.
BOOL JsonGetString(const char* pszJson, const char* pszKey, char* pszOut, DWORD cchOut);
