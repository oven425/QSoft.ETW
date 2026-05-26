#include "ETW.h"
#include <vector>
#include <iostream>
#include <windows.h>
#include <evntrace.h>
#include <strsafe.h>

#define LOGFILE_PATH L"test.etl"
#define LOGSESSION_NAME L"My Event Trace Session"

static const GUID SessionGuid =
{ 0xae44cb98, 0xbd11, 0x4069, { 0x80, 0x93, 0x77, 0xe, 0xc9, 0x25, 0x8a, 0x12 } };

// {D8909C24-5BE9-4502-98CA-AB7BDC24899D}
static const GUID ProviderGuid =
{ 0xd8909c24, 0x5be9, 0x4502, {0x98, 0xca, 0xab, 0x7b, 0xdc, 0x24, 0x89, 0x9d } };
//{331c3b3a-2005-44c2-ac5e-77220c37d6b4}
EVENT_TRACE_PROPERTIES* AllocateTraceProperties(const wchar_t* logFilePath, const wchar_t* sessionName)
{
	auto filelen = ::wcslen(logFilePath);
    auto sessionlen = ::wcslen(sessionName);
    auto BufferSize = sizeof(EVENT_TRACE_PROPERTIES) + filelen + sessionlen;
    BufferSize = BufferSize + 81920000;
    auto pSessionProperties = (EVENT_TRACE_PROPERTIES*)malloc(BufferSize);
    ZeroMemory(pSessionProperties, BufferSize);
	return pSessionProperties;
}
void ETW::SaveKernel()
{
    const wchar_t* kernelEtl = L"kernel.etl";
    const wchar_t* userEtl = L"user.etl";
    const wchar_t* mergedEtl = L"merged.etl";

    // 確保日誌資料夾存在
    //CreateDirectory(L"C:\\Logs", NULL);

    TRACEHANDLE hKernelSession = 0;
    TRACEHANDLE hUserSession = 0;
    ULONG status = ERROR_SUCCESS;

    wprintf(L"=== 1. 配置 ETW 屬性緩衝區 ===\n");
    // Kernel 固定綁定 KERNEL_LOGGER_NAME
    PEVENT_TRACE_PROPERTIES pKernelProps = AllocateTraceProperties(kernelEtl, KERNEL_LOGGER_NAME);
    pKernelProps->EnableFlags = EVENT_TRACE_FLAG_PROCESS | EVENT_TRACE_FLAG_THREAD | EVENT_TRACE_FLAG_CSWITCH;

    // User Trace 自訂 Session 名稱
    const wchar_t* myUserSessionName = L"MyUserTraceSession";
    PEVENT_TRACE_PROPERTIES pUserProps = AllocateTraceProperties(userEtl, myUserSessionName);

    // -------------------------------------------------------------
    wprintf(L"=== 2. 啟動 Trace Sessions ===\n");

    // 啟動 Kernel 追蹤 (無額外 Stack Walking 事件需求可傳入空陣列)
    status = this->m_KenerlTrace.StartKernelTrace(&hKernelSession, pKernelProps, 0);
    if (status != ERROR_SUCCESS) {
        wprintf(L"StartKernelTrace 失敗，錯誤代碼: %lu (是否未開管理員權限?)\n", status);
        goto CLEANUP;
    }
    wprintf(L"-> Kernel 追蹤已啟動，寫入中: \n");

    // 啟動 User 追蹤
    status = StartTrace(&hUserSession, myUserSessionName, pUserProps);
    if (status != ERROR_SUCCESS) {
        wprintf(L"StartTrace (User) 失敗，錯誤代碼: %lu\n", status);
        goto CLEANUP;
    }
    wprintf(L"-> User 追蹤已啟動，寫入中: \n");

    // 在這裡你可以透過 EnableTraceEx2 將你特定的 Provider GUID 掛載到 hUserSession 
    // 為了範例簡潔，此處略過特定 Provider 的掛載

    // -------------------------------------------------------------
    wprintf(L"=== 3. 正在收集資料 (模擬系統運行 5 秒) ===\n");
    Sleep(5000);

    // -------------------------------------------------------------
    wprintf(L"=== 4. 停止 Sessions 以確保快取全部寫入硬碟 ===\n");

    // 停止 Kernel 追蹤
    status = ControlTrace(hKernelSession, KERNEL_LOGGER_NAME, pKernelProps, EVENT_TRACE_CONTROL_STOP);
    if (status == ERROR_SUCCESS) wprintf(L"-> Kernel 追蹤已成功停止並存檔。\n");

    // 停止 User 追蹤
    status = ControlTrace(hUserSession, myUserSessionName, pUserProps, EVENT_TRACE_CONTROL_STOP);
    if (status == ERROR_SUCCESS) wprintf(L"-> User 追蹤已成功停止並存檔。\n");

    // -------------------------------------------------------------
    wprintf(L"=== 5. 合併 ETL 檔案 ===\n");
    {
        // 建立要合併的來源檔案路徑陣列
        LPCWSTR traceFiles[] = { kernelEtl, userEtl };
        ULONG fileCount = sizeof(traceFiles) / sizeof(traceFiles[0]);

        // 執行合併：EVENT_TRACE_MERGE_EXTENDED_DATA_DEFAULT 會自動注入符號解析與 OS Build 所需的元數據
        status = this->m_KenerlTrace.CreateMergedTraceFile(mergedEtl, traceFiles, fileCount, EVENT_TRACE_MERGE_EXTENDED_DATA_DEFAULT);

        if (status == ERROR_SUCCESS) {
            wprintf(L"🎉 恭喜！合併成功！\n");
            wprintf(L"最終成品檔案位於: %ls\n", mergedEtl);
            wprintf(L"您現在可以直接將此檔案拖入 Windows Performance Analyzer (WPA) 進行分析。\n");
        }
        else {
            wprintf(L"CreateMergedTraceFile 失敗，錯誤代碼: %lu\n", status);
        }
    }

CLEANUP:
    if (pKernelProps) free(pKernelProps);
    if (pUserProps) free(pUserProps);
    return;
}

void ETW::Save(const TCHAR* filename)
{
    EVENT_TRACE_PROPERTIES all = { 0 };
    ULONG querycount = 0;
    auto status = QueryAllTraces(NULL, 0, &querycount);
    std::vector<EVENT_TRACE_PROPERTIES*> properties(querycount);
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
    pSessionProperties->LogFileMode = EVENT_TRACE_FILE_MODE_SEQUENTIAL | EVENT_TRACE_SYSTEM_LOGGER_MODE;
    pSessionProperties->EnableFlags = EVENT_TRACE_FLAG_PROCESS | EVENT_TRACE_FLAG_THREAD | EVENT_TRACE_FLAG_DISK_IO | EVENT_TRACE_FLAG_FORWARD_WMI;
    pSessionProperties->MaximumFileSize = 100;  // 1 MB
    pSessionProperties->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
    pSessionProperties->LogFileNameOffset = sizeof(EVENT_TRACE_PROPERTIES) + sizeof(LOGSESSION_NAME);
    StringCbCopy((LPWSTR)((char*)pSessionProperties + pSessionProperties->LogFileNameOffset), sizeof(LOGFILE_PATH), LOGFILE_PATH);

    // Create the trace session.
    //status = StartKernelTrace(&SessionHandle, pSessionProperties, nullptr, 0);
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

#include <evntrace.h>
#include <tdh.h>
#pragma comment(lib, "tdh.lib")

static void EVENT_CALLBACK(PEVENT_TRACE pEvent)
{

}

static void EVENT_RECORD_CALLBACK(PEVENT_RECORD EventRecord)
{
    DWORD bufferSize = 0;
    EventRecord->EventHeader.EventDescriptor.Id;
    TdhGetEventInformation(EventRecord, 0, nullptr, nullptr, &bufferSize);
    if (bufferSize <= 0)
    {
        return;
    }

	
	std::vector<TRACE_EVENT_INFO> buffer(bufferSize / sizeof(TRACE_EVENT_INFO));
    TdhGetEventInformation(EventRecord, 0, nullptr, buffer.data(), &bufferSize);
}


void ETW::Open(const TCHAR* filename)
{
	EVENT_TRACE_LOGFILE etloptions = {};
    etloptions.ProcessTraceMode = PROCESS_TRACE_MODE_EVENT_RECORD| PROCESS_TRACE_MODE_RAW_TIMESTAMP;
    etloptions.EventRecordCallback = &EVENT_RECORD_CALLBACK;
    //etloptions.EventCallback = &EVENT_CALLBACK;
    etloptions.Context = this;
    
	etloptions.LogFileName = (TCHAR*)filename;
	auto hTrace = ::OpenTrace(&etloptions);

    if (hTrace == INVALID_PROCESSTRACE_HANDLE) 
    {
        wprintf(L"無法開啟 ETL 檔案。錯誤碼: %u\n", GetLastError());
        return;
    }

    wprintf(L"正在解析檔案: %ls ...\n", filename);

    ULONG status = ProcessTrace(&hTrace, 1, NULL, NULL);
    if (status != ERROR_SUCCESS) 
    {
        wprintf(L"ProcessTrace 發生錯誤: %u\n", status);
    }

    CloseTrace(hTrace);

}