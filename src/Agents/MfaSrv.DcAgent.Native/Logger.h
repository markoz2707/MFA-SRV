#pragma once

#include <windows.h>

// Simple logging for the LSA DLL
// Uses ETW (Event Tracing for Windows) and optional file logging

// Log levels
#define MFASRV_LOG_ERROR   0
#define MFASRV_LOG_WARNING 1
#define MFASRV_LOG_INFO    2
#define MFASRV_LOG_DEBUG   3

// Initialize logging subsystem
void LogInit();

// Shutdown logging subsystem
void LogShutdown();

// Log a message
void LogMessage(int level, const char* format, ...);

// Log with wide string
void LogMessageW(int level, const wchar_t* format, ...);
