#pragma once
#include <Windows.h>
#include <memory>
#include <KernelTraceControl.h>
class KernelTrace
{
public:
    KernelTrace();
    ~KernelTrace();

    bool Initialize();

    ULONG StartKernelTrace(PTRACEHANDLE TraceHandle,
        PEVENT_TRACE_PROPERTIES Properties,
        ULONG cStackTracingEventIds);

    ULONG StartHeapTrace(
        const wchar_t* sessionName,
        ULONG processIdCount,
        const ULONG* processIds);

    ULONG UpdateHeapTrace(
        const wchar_t* sessionName,
        ULONG processIdCount,
        const ULONG* processIds);

    ULONG CreateMergedTraceFile(
        const wchar_t* mergedFileName,
        const wchar_t** traceFileNames,
        ULONG traceFileCount,
        DWORD extendedDataFlags);

private:
    HMODULE hLib;

    typedef ULONG
        (WINAPI* PFN_StartKernelTrace)(
            __out PTRACEHANDLE TraceHandle,
            __inout PEVENT_TRACE_PROPERTIES Properties,
            __in ULONG cStackTracingEventIds
        );

    typedef ULONG(WINAPI* PFN_StartHeapTrace)(
        const wchar_t* sessionName,
        ULONG processIdCount,
        const ULONG* processIds);

    typedef ULONG(WINAPI* PFN_UpdateHeapTrace)(
        const wchar_t* sessionName,
        ULONG processIdCount,
        const ULONG* processIds);

    typedef ULONG(WINAPI* PFN_CreateMergedTraceFile)(
        const wchar_t* mergedFileName,
        const wchar_t** traceFileNames,
        ULONG traceFileCount,
        DWORD extendedDataFlags);

    // 函式指標成員
    PFN_StartKernelTrace pfnStartKernelTrace;
    PFN_StartHeapTrace pfnStartHeapTrace;
    PFN_UpdateHeapTrace pfnUpdateHeapTrace;
    PFN_CreateMergedTraceFile pfnCreateMergedTraceFile;

    bool LoadFunction(const char* functionName, void*& functionPtr);
};