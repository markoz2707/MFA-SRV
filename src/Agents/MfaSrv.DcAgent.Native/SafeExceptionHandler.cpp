// MfaSrv LSA Auth Package - Exception Handler
// CRITICAL: This code runs in LSASS. All exceptions must be caught.

#include "SafeExceptionHandler.h"
#include "Logger.h"

LONG MfaSrvExceptionFilter(DWORD exceptionCode, const char* functionName)
{
    __try
    {
        LogMessage(MFASRV_LOG_ERROR,
            "EXCEPTION in %s: code=0x%08X. Fail-open applied.",
            functionName ? functionName : "unknown",
            exceptionCode);
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        // Even logging failed - just suppress
    }

    // Always handle the exception - never let it propagate to LSASS
    return EXCEPTION_EXECUTE_HANDLER;
}
