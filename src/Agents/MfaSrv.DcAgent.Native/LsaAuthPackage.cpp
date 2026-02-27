// MfaSrv LSA Authentication Package
// ===================================
// This DLL is loaded into the LSASS process on Domain Controllers.
// It intercepts authentication requests and queries the MfaSrv DC Agent
// via Named Pipe to determine if MFA is required.
//
// CRITICAL SAFETY REQUIREMENTS:
// 1. Every function body is wrapped in SEH (__try/__except)
// 2. Named pipe timeout is 3 seconds maximum
// 3. On ANY error, the default behavior is FAIL-OPEN (allow auth)
// 4. No C++ exceptions - C++ exception handling is disabled (/EHs-)
// 5. No dynamic memory allocation beyond stack buffers
// 6. Only links: ntdll.lib, kernel32.lib, advapi32.lib

#include "LsaAuthPackage.h"
#include "NamedPipeClient.h"
#include "SafeExceptionHandler.h"
#include "Logger.h"
#include "Protocol.h"

// Globals
PLSA_SECPKG_FUNCTION_TABLE g_LsaFunctions = NULL;
ULONG g_PackageId = 0;
BOOL g_Initialized = FALSE;

// LSA dispatch table provided during init
static LSA_DISPATCH_TABLE g_DispatchTable = { 0 };

// -----------------------------------------------------------
// SpLsaModeInitialize
// Called by LSA during system startup to get function pointers
// -----------------------------------------------------------

// Forward declarations for the authentication package functions
NTSTATUS NTAPI MfaSrv_InitializePackage(
    ULONG AuthenticationPackageId,
    PLSA_DISPATCH_TABLE LsaDispatchTable,
    PLSA_STRING Database,
    PLSA_STRING Confidentiality,
    PLSA_STRING* AuthenticationPackageName
);

NTSTATUS NTAPI MfaSrv_LogonUserEx2(
    PLSA_CLIENT_REQUEST ClientRequest,
    SECURITY_LOGON_TYPE LogonType,
    PVOID AuthenticationInformation,
    PVOID ClientAuthenticationBase,
    ULONG AuthenticationInformationLength,
    PVOID* ProfileBuffer,
    PULONG ProfileBufferLength,
    PLUID LogonId,
    PNTSTATUS SubStatus,
    PLSA_TOKEN_INFORMATION_TYPE TokenInformationType,
    PVOID* TokenInformation,
    PUNICODE_STRING* AccountName,
    PUNICODE_STRING* AuthenticatingAuthority,
    PUNICODE_STRING* MachineName,
    PSECPKG_PRIMARY_CRED PrimaryCredentials,
    PSECPKG_SUPPLEMENTAL_CRED_ARRAY* SupplementalCredentials
);

NTSTATUS NTAPI MfaSrv_CallPackage(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferLength,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferLength,
    PNTSTATUS ProtocolStatus
);

VOID NTAPI MfaSrv_LogonTerminated(PLUID LogonId);

NTSTATUS NTAPI MfaSrv_CallPackageUntrusted(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferLength,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferLength,
    PNTSTATUS ProtocolStatus
);

NTSTATUS NTAPI MfaSrv_CallPackagePassthrough(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferLength,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferLength,
    PNTSTATUS ProtocolStatus
);

// Security package function table
static SECPKG_FUNCTION_TABLE g_MfaSrvFunctionTable = {
    MfaSrv_InitializePackage,       // InitializePackage
    NULL,                            // LsaLogonUser (deprecated, use LogonUserEx2)
    MfaSrv_CallPackage,             // CallPackage
    MfaSrv_LogonTerminated,         // LogonTerminated
    MfaSrv_CallPackageUntrusted,    // CallPackageUntrusted
    MfaSrv_CallPackagePassthrough,  // CallPackagePassthrough
    NULL,                            // LogonUserEx
    MfaSrv_LogonUserEx2             // LogonUserEx2
};


// -----------------------------------------------------------
// Entry point called by LSA to enumerate packages
// -----------------------------------------------------------
extern "C" MFASRV_API NTSTATUS NTAPI SpLsaModeInitialize(
    ULONG LsaVersion,
    PULONG PackageVersion,
    PSECPKG_FUNCTION_TABLE* ppTables,
    PULONG pcTables)
{
    SAFE_NTSTATUS_BEGIN

    LogInit();
    LogMessage(MFASRV_LOG_INFO, "SpLsaModeInitialize: LsaVersion=%lu", LsaVersion);

    if (ppTables == NULL || pcTables == NULL || PackageVersion == NULL)
        return STATUS_INVALID_PARAMETER;

    *PackageVersion = SECPKG_INTERFACE_VERSION;
    *ppTables = &g_MfaSrvFunctionTable;
    *pcTables = 1;

    LogMessage(MFASRV_LOG_INFO, "MfaSrv LSA Auth Package loaded successfully");
    return STATUS_SUCCESS;

    SAFE_NTSTATUS_END("SpLsaModeInitialize")
}


// -----------------------------------------------------------
// InitializePackage - called once after SpLsaModeInitialize
// -----------------------------------------------------------
NTSTATUS NTAPI MfaSrv_InitializePackage(
    ULONG AuthenticationPackageId,
    PLSA_DISPATCH_TABLE LsaDispatchTable,
    PLSA_STRING Database,
    PLSA_STRING Confidentiality,
    PLSA_STRING* AuthenticationPackageName)
{
    SAFE_NTSTATUS_BEGIN

    UNREFERENCED_PARAMETER(Database);
    UNREFERENCED_PARAMETER(Confidentiality);

    g_PackageId = AuthenticationPackageId;

    if (LsaDispatchTable != NULL)
    {
        g_DispatchTable = *LsaDispatchTable;
    }

    // Allocate package name using LSA allocator
    if (AuthenticationPackageName != NULL)
    {
        PLSA_STRING name = (PLSA_STRING)g_DispatchTable.AllocateLsaHeap(sizeof(LSA_STRING));
        if (name != NULL)
        {
            const char* packageName = MFASRV_PACKAGE_NAME;
            USHORT len = (USHORT)strlen(packageName);
            name->Buffer = (PCHAR)g_DispatchTable.AllocateLsaHeap(len + 1);
            if (name->Buffer != NULL)
            {
                memcpy(name->Buffer, packageName, len + 1);
                name->Length = len;
                name->MaximumLength = len + 1;
            }
            *AuthenticationPackageName = name;
        }
    }

    g_Initialized = TRUE;
    LogMessage(MFASRV_LOG_INFO, "MfaSrv package initialized, ID=%lu", g_PackageId);

    return STATUS_SUCCESS;

    SAFE_NTSTATUS_END("MfaSrv_InitializePackage")
}


// -----------------------------------------------------------
// LogonUserEx2 - THE MAIN INTERCEPTION POINT
// Called for every authentication attempt on this DC
// -----------------------------------------------------------
NTSTATUS NTAPI MfaSrv_LogonUserEx2(
    PLSA_CLIENT_REQUEST ClientRequest,
    SECURITY_LOGON_TYPE LogonType,
    PVOID AuthenticationInformation,
    PVOID ClientAuthenticationBase,
    ULONG AuthenticationInformationLength,
    PVOID* ProfileBuffer,
    PULONG ProfileBufferLength,
    PLUID LogonId,
    PNTSTATUS SubStatus,
    PLSA_TOKEN_INFORMATION_TYPE TokenInformationType,
    PVOID* TokenInformation,
    PUNICODE_STRING* AccountName,
    PUNICODE_STRING* AuthenticatingAuthority,
    PUNICODE_STRING* MachineName,
    PSECPKG_PRIMARY_CRED PrimaryCredentials,
    PSECPKG_SUPPLEMENTAL_CRED_ARRAY* SupplementalCredentials)
{
    SAFE_NTSTATUS_BEGIN

    // We do NOT actually perform the authentication ourselves.
    // We intercept to check MFA status, then the real auth package
    // (Kerberos/NTLM/Negotiate) handles the actual credential validation.
    //
    // Our role:
    // 1. Extract username/domain from the auth info
    // 2. Query DC Agent via Named Pipe
    // 3. If DENY -> return STATUS_LOGON_FAILURE
    // 4. If ALLOW/PENDING/REQUIRE_MFA -> return STATUS_NOT_IMPLEMENTED
    //    (tells LSA to try the next package)

    // Extract user info from KERB_INTERACTIVE_LOGON or MSV1_0_INTERACTIVE_LOGON
    char userName[256] = { 0 };
    char domainName[256] = { 0 };

    // Try to extract from PrimaryCredentials if available
    if (PrimaryCredentials != NULL && PrimaryCredentials->DownlevelName.Buffer != NULL)
    {
        WideCharToMultiByte(CP_UTF8, 0,
            PrimaryCredentials->DownlevelName.Buffer,
            PrimaryCredentials->DownlevelName.Length / sizeof(WCHAR),
            userName, sizeof(userName) - 1, NULL, NULL);
    }

    if (PrimaryCredentials != NULL && PrimaryCredentials->DomainName.Buffer != NULL)
    {
        WideCharToMultiByte(CP_UTF8, 0,
            PrimaryCredentials->DomainName.Buffer,
            PrimaryCredentials->DomainName.Length / sizeof(WCHAR),
            domainName, sizeof(domainName) - 1, NULL, NULL);
    }

    // If we couldn't extract user info, pass through
    if (userName[0] == '\0')
    {
        LogMessage(MFASRV_LOG_DEBUG, "LogonUserEx2: no username extracted, passing through");
        return STATUS_NOT_IMPLEMENTED; // Let other packages handle it
    }

    LogMessage(MFASRV_LOG_INFO, "LogonUserEx2: user=%s domain=%s logonType=%d",
        userName, domainName, (int)LogonType);

    // Query DC Agent via Named Pipe
    int decision = QueryDcAgent(
        MFASRV_PIPE_NAME,
        userName,
        domainName,
        NULL,  // sourceIp - extracted by DC Agent from event context
        NULL,  // workstation
        PROTO_AUTH_KERBEROS, // default, could be refined based on LogonType
        MFASRV_PIPE_TIMEOUT);

    switch (decision)
    {
    case MFASRV_DECISION_DENY:
        LogMessage(MFASRV_LOG_WARNING, "MFA DENIED for %s\\%s", domainName, userName);
        if (SubStatus != NULL)
            *SubStatus = STATUS_ACCOUNT_RESTRICTION;
        return STATUS_LOGON_FAILURE;

    case MFASRV_DECISION_ALLOW:
        LogMessage(MFASRV_LOG_INFO, "MFA ALLOWED for %s\\%s", domainName, userName);
        // Fall through - let the real auth package handle it
        break;

    case MFASRV_DECISION_REQUIRE_MFA:
        LogMessage(MFASRV_LOG_INFO, "MFA REQUIRED for %s\\%s (handled out-of-band)", domainName, userName);
        // For network logons, MFA challenge is handled out-of-band by DC Agent
        // We allow the auth to proceed; the DC Agent holds the session until MFA completes
        break;

    case MFASRV_DECISION_PENDING:
        LogMessage(MFASRV_LOG_INFO, "MFA PENDING for %s\\%s", domainName, userName);
        break;

    default:
        LogMessage(MFASRV_LOG_WARNING, "Unknown decision %d for %s\\%s, allowing",
            decision, domainName, userName);
        break;
    }

    // Return STATUS_NOT_IMPLEMENTED so LSA delegates to the next auth package
    return STATUS_NOT_IMPLEMENTED;

    SAFE_NTSTATUS_END("MfaSrv_LogonUserEx2")
}


// -----------------------------------------------------------
// CallPackage - for custom IPC from user-mode
// -----------------------------------------------------------
NTSTATUS NTAPI MfaSrv_CallPackage(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferLength,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferLength,
    PNTSTATUS ProtocolStatus)
{
    SAFE_NTSTATUS_BEGIN

    UNREFERENCED_PARAMETER(ClientRequest);
    UNREFERENCED_PARAMETER(ProtocolSubmitBuffer);
    UNREFERENCED_PARAMETER(ClientBufferBase);
    UNREFERENCED_PARAMETER(SubmitBufferLength);
    UNREFERENCED_PARAMETER(ProtocolReturnBuffer);
    UNREFERENCED_PARAMETER(ReturnBufferLength);

    if (ProtocolStatus != NULL)
        *ProtocolStatus = STATUS_NOT_IMPLEMENTED;

    return STATUS_NOT_IMPLEMENTED;

    SAFE_NTSTATUS_END("MfaSrv_CallPackage")
}


// -----------------------------------------------------------
// LogonTerminated - cleanup when a logon session ends
// -----------------------------------------------------------
VOID NTAPI MfaSrv_LogonTerminated(PLUID LogonId)
{
    __try
    {
        UNREFERENCED_PARAMETER(LogonId);
        // Could notify DC Agent about session termination here
    }
    __except(MfaSrvExceptionFilter(GetExceptionCode(), "MfaSrv_LogonTerminated"))
    {
        // Never crash
    }
}


// -----------------------------------------------------------
// CallPackageUntrusted
// -----------------------------------------------------------
NTSTATUS NTAPI MfaSrv_CallPackageUntrusted(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferLength,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferLength,
    PNTSTATUS ProtocolStatus)
{
    SAFE_NTSTATUS_BEGIN

    UNREFERENCED_PARAMETER(ClientRequest);
    UNREFERENCED_PARAMETER(ProtocolSubmitBuffer);
    UNREFERENCED_PARAMETER(ClientBufferBase);
    UNREFERENCED_PARAMETER(SubmitBufferLength);
    UNREFERENCED_PARAMETER(ProtocolReturnBuffer);
    UNREFERENCED_PARAMETER(ReturnBufferLength);

    if (ProtocolStatus != NULL)
        *ProtocolStatus = STATUS_NOT_IMPLEMENTED;

    return STATUS_NOT_IMPLEMENTED;

    SAFE_NTSTATUS_END("MfaSrv_CallPackageUntrusted")
}


// -----------------------------------------------------------
// CallPackagePassthrough
// -----------------------------------------------------------
NTSTATUS NTAPI MfaSrv_CallPackagePassthrough(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferLength,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferLength,
    PNTSTATUS ProtocolStatus)
{
    SAFE_NTSTATUS_BEGIN

    UNREFERENCED_PARAMETER(ClientRequest);
    UNREFERENCED_PARAMETER(ProtocolSubmitBuffer);
    UNREFERENCED_PARAMETER(ClientBufferBase);
    UNREFERENCED_PARAMETER(SubmitBufferLength);
    UNREFERENCED_PARAMETER(ProtocolReturnBuffer);
    UNREFERENCED_PARAMETER(ReturnBufferLength);

    if (ProtocolStatus != NULL)
        *ProtocolStatus = STATUS_NOT_IMPLEMENTED;

    return STATUS_NOT_IMPLEMENTED;

    SAFE_NTSTATUS_END("MfaSrv_CallPackagePassthrough")
}


// -----------------------------------------------------------
// DllMain - minimal initialization
// -----------------------------------------------------------
BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpReserved)
{
    __try
    {
        UNREFERENCED_PARAMETER(lpReserved);

        switch (reason)
        {
        case DLL_PROCESS_ATTACH:
            DisableThreadLibraryCalls(hModule);
            break;

        case DLL_PROCESS_DETACH:
            LogShutdown();
            break;
        }
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        // Never crash
    }

    return TRUE;
}
