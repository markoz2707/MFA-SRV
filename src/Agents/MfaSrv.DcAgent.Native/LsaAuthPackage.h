#pragma once

// MfaSrv LSA Authentication Package
// Intercepts Windows authentication and queries DC Agent via Named Pipe
// CRITICAL: This DLL is loaded into LSASS - must NEVER crash

#define WIN32_LEAN_AND_MEAN
#define SECURITY_WIN32
#define WIN32_NO_STATUS

#include <windows.h>
#include <security.h>
#include <ntsecapi.h>
#include <ntsecpkg.h>
#include <SubAuth.h>

#undef WIN32_NO_STATUS
#include <ntstatus.h>

// Export markers
#ifdef MFASRV_EXPORTS
#define MFASRV_API __declspec(dllexport)
#else
#define MFASRV_API __declspec(dllimport)
#endif

// LSA dispatch table - provided by LSASS during initialization
extern PLSA_SECPKG_FUNCTION_TABLE g_LsaFunctions;

// Package name
#define MFASRV_PACKAGE_NAME   "MfaSrvLsaAuth"
#define MFASRV_PACKAGE_NAME_W L"MfaSrvLsaAuth"

// Configuration
#define MFASRV_PIPE_NAME      L"\\\\.\\pipe\\MfaSrvDcAgent"
#define MFASRV_PIPE_TIMEOUT   3000  // 3 seconds max
#define MFASRV_BUFFER_SIZE    4096

// Package ID assigned by LSA
extern ULONG g_PackageId;
extern BOOL  g_Initialized;
