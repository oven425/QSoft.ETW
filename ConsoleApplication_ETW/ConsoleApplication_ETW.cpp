// ConsoleApplication_ETW.cpp : 此檔案包含 'main' 函式。程式會於該處開始執行及結束執行。
//
#include <vector>
#include <windows.h>
#include <stdio.h>
#include <conio.h>
#include <strsafe.h>
#include <wmistr.h>
#include <evntrace.h>
void CreateMergeFile(LPCWSTR wszMergedFileName,
    LPCWSTR wszTraceFileNames[],
    ULONG   cTraceFileNames,
    DWORD   dwExtendedDataFlags);
int QuerytAll();
#define LOGFILE_PATH L"test.etl"
#define LOGSESSION_NAME L"My Event Trace Session"

static const GUID SessionGuid =
{ 0xae44cb98, 0xbd11, 0x4069, { 0x80, 0x93, 0x77, 0xe, 0xc9, 0x25, 0x8a, 0x12 } };

// {D8909C24-5BE9-4502-98CA-AB7BDC24899D}
static const GUID ProviderGuid =
{ 0xd8909c24, 0x5be9, 0x4502, {0x98, 0xca, 0xab, 0x7b, 0xdc, 0x24, 0x89, 0x9d } };
//{331c3b3a-2005-44c2-ac5e-77220c37d6b4}
int main()
{
    
    QuerytAll();
    ULONG status = ERROR_SUCCESS;
    EVENT_TRACE_PROPERTIES all = { 0 };
    ULONG querycount = 0;
    status = QueryAllTraces(NULL, 0, &querycount);
	std::vector< EVENT_TRACE_PROPERTIES*> properties(querycount);
    properties.resize(querycount);


    status = QueryAllTraces(&properties[0], properties.size(), &querycount);
    TRACEHANDLE SessionHandle = 0;
    EVENT_TRACE_PROPERTIES* pSessionProperties = NULL;
    ULONG BufferSize = 0;
    BOOL TraceOn = TRUE;

    // Allocate memory for the session properties. The memory must
    // be large enough to include the log file name and session name,
    // which get appended to the end of the session properties structure.

    BufferSize = sizeof(EVENT_TRACE_PROPERTIES) + sizeof(LOGFILE_PATH) + sizeof(LOGSESSION_NAME);
    BufferSize = BufferSize + 81920000;
    pSessionProperties = (EVENT_TRACE_PROPERTIES*)malloc(BufferSize);
    if (NULL == pSessionProperties)
    {
        wprintf(L"Unable to allocate %d bytes for properties structure.\n", BufferSize);
        goto cleanup;
    }

    // Set the session properties. You only append the log file name
    // to the properties structure; the StartTrace function appends
    // the session name for you.

    ZeroMemory(pSessionProperties, BufferSize);
    pSessionProperties->Wnode.BufferSize = BufferSize;
    pSessionProperties->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
    pSessionProperties->Wnode.ClientContext = 1; //QPC clock resolution
    pSessionProperties->Wnode.Guid = SessionGuid;
    pSessionProperties->LogFileMode = EVENT_TRACE_FILE_MODE_SEQUENTIAL| EVENT_TRACE_SYSTEM_LOGGER_MODE;
    pSessionProperties->EnableFlags = EVENT_TRACE_FLAG_PROCESS| EVENT_TRACE_FLAG_THREAD| EVENT_TRACE_FLAG_DISK_IO| EVENT_TRACE_FLAG_FORWARD_WMI;
    pSessionProperties->MaximumFileSize = 100;  // 1 MB
    pSessionProperties->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
    pSessionProperties->LogFileNameOffset = sizeof(EVENT_TRACE_PROPERTIES) + sizeof(LOGSESSION_NAME);
    StringCbCopy((LPWSTR)((char*)pSessionProperties + pSessionProperties->LogFileNameOffset), sizeof(LOGFILE_PATH), LOGFILE_PATH);

    // Create the trace session.

    status = StartTrace((PTRACEHANDLE)&SessionHandle, LOGSESSION_NAME, pSessionProperties);
    if (ERROR_SUCCESS != status)
    {
        wprintf(L"StartTrace() failed with %lu\n", status);
        goto cleanup;
    }

    //status = EnableTraceEx2(
    //    SessionHandle,
    //    (LPCGUID)&ProviderGuid,
    //    EVENT_CONTROL_CODE_ENABLE_PROVIDER,
    //    TRACE_LEVEL_INFORMATION,
    //    0,
    //    0,
    //    0,
    //    NULL
    //);

    //if (ERROR_SUCCESS != status)
    //{
    //    wprintf(L"EnableTrace() failed with %lu\n", status);
    //    TraceOn = FALSE;
    //    goto cleanup;
    //}

    wprintf(L"wait 10 second.\n");
    ::Sleep(10000);

cleanup:

    if (SessionHandle)
    {
        //if (TraceOn)
        //{
        //    status = EnableTraceEx2(
        //        SessionHandle,
        //        (LPCGUID)&ProviderGuid,
        //        EVENT_CONTROL_CODE_DISABLE_PROVIDER,
        //        TRACE_LEVEL_INFORMATION,
        //        0,
        //        0,
        //        0,
        //        NULL
        //    );
        //}

        status = ControlTrace(SessionHandle, NULL, pSessionProperties, EVENT_TRACE_CONTROL_STOP);

        if (ERROR_SUCCESS != status)
        {
            wprintf(L"ControlTrace(stop) failed with %lu\n", status);
        }
        //CreateMergeFile();
    }

    if (pSessionProperties)
    {
        free(pSessionProperties);
        pSessionProperties = NULL;
    }
}

