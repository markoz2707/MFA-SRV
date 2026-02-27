#pragma once

#include <windows.h>

// SEH wrapper macros for LSASS-safe execution
// Every exported function MUST be wrapped in SAFE_EXECUTE

// Exception filter that logs and returns EXCEPTION_EXECUTE_HANDLER
LONG MfaSrvExceptionFilter(DWORD exceptionCode, const char* functionName);

// Macro for wrapping function bodies in SEH
#define SAFE_BEGIN __try {
#define SAFE_END(defaultReturn, funcName) \
    } __except(MfaSrvExceptionFilter(GetExceptionCode(), funcName)) { \
        return defaultReturn; \
    }

// Safe NTSTATUS wrapper - returns STATUS_SUCCESS on exception (fail-open)
#define SAFE_NTSTATUS_BEGIN __try {
#define SAFE_NTSTATUS_END(funcName) \
    } __except(MfaSrvExceptionFilter(GetExceptionCode(), funcName)) { \
        return STATUS_SUCCESS; \
    }
