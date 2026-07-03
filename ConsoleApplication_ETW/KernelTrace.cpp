#include "KernelTrace.h"
#include <stdio.h>

KernelTrace::KernelTrace()
    : hLib(NULL),
    pfnStartKernelTrace(NULL),
    pfnStartHeapTrace(NULL),
    pfnUpdateHeapTrace(NULL),
    pfnCreateMergedTraceFile(NULL)
{
    Initialize();
}

KernelTrace::~KernelTrace()
{
    if (hLib != NULL)
    {
        FreeLibrary(hLib);
        hLib = NULL;
    }
}

bool KernelTrace::Initialize()
{
    // 動態載入 KernelTraceControl.dll
    hLib = LoadLibraryW(L"KernelTraceControl.dll");
    if (hLib == NULL)
    {
        wprintf(L"Failed to load KernelTraceControl.dll: %lu\n", GetLastError());
        return false;
    }

    // 載入所有四個 API 函數
    bool success = true;
    success &= LoadFunction("StartKernelTrace", (void*&)pfnStartKernelTrace);
    success &= LoadFunction("StartHeapTrace", (void*&)pfnStartHeapTrace);
    success &= LoadFunction("UpdateHeapTrace", (void*&)pfnUpdateHeapTrace);
    success &= LoadFunction("CreateMergedTraceFile", (void*&)pfnCreateMergedTraceFile);

    if (!success)
    {
        FreeLibrary(hLib);
        hLib = NULL;
        return false;
    }

    return true;
}

bool KernelTrace::LoadFunction(const char* functionName, void*& functionPtr)
{
    functionPtr = GetProcAddress(hLib, functionName);
    if (functionPtr == NULL)
    {
        wprintf(L"GetProcAddress failed for %S: %lu\n", functionName, GetLastError());
        return false;
    }
    return true;
}

ULONG KernelTrace::StartKernelTrace(
    PTRACEHANDLE TraceHandle,
    PEVENT_TRACE_PROPERTIES Properties,
    ULONG cStackTracingEventIds)
{
    if (pfnStartKernelTrace == nullptr)
    {
        wprintf(L"StartKernelTrace function not initialized\n");
        return ERROR_INVALID_STATE;
    }
    return pfnStartKernelTrace(TraceHandle, Properties, cStackTracingEventIds);
}

ULONG KernelTrace::StartHeapTrace(
    const wchar_t* sessionName,
    ULONG processIdCount,
    const ULONG* processIds)
{
    if (pfnStartHeapTrace == NULL)
    {
        wprintf(L"StartHeapTrace function not initialized\n");
        return ERROR_INVALID_STATE;
    }
    return pfnStartHeapTrace(sessionName, processIdCount, processIds);
}

ULONG KernelTrace::UpdateHeapTrace(
    const wchar_t* sessionName,
    ULONG processIdCount,
    const ULONG* processIds)
{
    if (pfnUpdateHeapTrace == NULL)
    {
        wprintf(L"UpdateHeapTrace function not initialized\n");
        return ERROR_INVALID_STATE;
    }
    return pfnUpdateHeapTrace(sessionName, processIdCount, processIds);
}

ULONG KernelTrace::CreateMergedTraceFile(
    const wchar_t* mergedFileName,
    const wchar_t** traceFileNames,
    ULONG traceFileCount,
    DWORD extendedDataFlags)
{
    if (pfnCreateMergedTraceFile == NULL)
    {
        wprintf(L"CreateMergedTraceFile function not initialized\n");
        return ERROR_INVALID_STATE;
    }
    return pfnCreateMergedTraceFile(mergedFileName, traceFileNames, traceFileCount, extendedDataFlags);
}