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
static const GUID SystemTraceControlGuid = 
{ 0x9e814aad, 0x3204, 0x11d2, {0x9a, 0x82, 0x00, 0x60, 0x08, 0xa8, 0x69, 0x39} };


// {D8909C24-5BE9-4502-98CA-AB7BDC24899D}
static const GUID ProviderGuid =
{ 0xd8909c24, 0x5be9, 0x4502, {0x98, 0xca, 0xab, 0x7b, 0xdc, 0x24, 0x89, 0x9d } };
//{331c3b3a-2005-44c2-ac5e-77220c37d6b4}
EVENT_TRACE_PROPERTIES* AllocateTraceProperties(const TCHAR* logFilePath, const TCHAR* sessionName)
{
	auto filelen = ::_tcslen(logFilePath);
    auto sessionlen = ::_tcslen(sessionName);
    auto BufferSize = sizeof(EVENT_TRACE_PROPERTIES) + (filelen + 1) * sizeof(TCHAR) + (sessionlen + 1) * sizeof(TCHAR);
    BufferSize = BufferSize + 81920000;
    auto pSessionProperties = (EVENT_TRACE_PROPERTIES*)malloc(BufferSize);
    ZeroMemory(pSessionProperties, BufferSize);
    
    // === 關鍵初始化 ===
    pSessionProperties->Wnode.BufferSize = BufferSize;
    pSessionProperties->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
    pSessionProperties->Wnode.ClientContext = 1; // QPC clock resolution
    pSessionProperties->LogFileMode = EVENT_TRACE_FILE_MODE_SEQUENTIAL | EVENT_TRACE_SYSTEM_LOGGER_MODE;
    pSessionProperties->MaximumFileSize = 1024; // MB
    pSessionProperties->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
    pSessionProperties->LogFileNameOffset = sizeof(EVENT_TRACE_PROPERTIES) + (sessionlen + 1) * sizeof(TCHAR);
    
    // 複製名稱
    StringCbCopy((LPWSTR)((char*)pSessionProperties + pSessionProperties->LoggerNameOffset), 
                 (sessionlen + 1) * sizeof(TCHAR), sessionName);
    StringCbCopy((LPWSTR)((char*)pSessionProperties + pSessionProperties->LogFileNameOffset), 
                 (filelen + 1) * sizeof(TCHAR), logFilePath);
    
	return pSessionProperties;
}
void ETW::SaveKernel()
{
    const TCHAR* kernelEtl = _T("kernel.etl");
    const TCHAR* userEtl = _T("user.etl");
    const TCHAR* mergedEtl = _T("merged.etl");

    // 確保日誌資料夾存在
    //CreateDirectory(L"C:\\Logs", NULL);

    TRACEHANDLE hKernelSession = 0;
    TRACEHANDLE hUserSession = 0;
    ULONG status = ERROR_SUCCESS;

    wprintf(L"=== 1. 配置 ETW 屬性緩衝區 ===\n");
    // Kernel 固定綁定 KERNEL_LOGGER_NAME
    PEVENT_TRACE_PROPERTIES pKernelProps = AllocateTraceProperties(kernelEtl, KERNEL_LOGGER_NAME);
    pKernelProps->Wnode.Guid = SystemTraceControlGuid;
    
    // 加入所有可用的 Kernel Trace Flags
    pKernelProps->EnableFlags = 
        EVENT_TRACE_FLAG_PROCESS              |  // 進程建立/終止
        EVENT_TRACE_FLAG_THREAD               |  // 線程建立/終止
        EVENT_TRACE_FLAG_IMAGE_LOAD           |  // 模組載入
        EVENT_TRACE_FLAG_DISK_IO              |  // 磁碟 I/O
        EVENT_TRACE_FLAG_DISK_FILE_IO         |  // 磁碟檔案 I/O
        EVENT_TRACE_FLAG_MEMORY_PAGE_FAULTS   |  // 內存頁面缺陷
        EVENT_TRACE_FLAG_MEMORY_HARD_FAULTS   |  // 硬頁面缺陷 (實際讀寫)
        EVENT_TRACE_FLAG_NETWORK_TCPIP        |  // 網絡 TCP/IP
        EVENT_TRACE_FLAG_REGISTRY             |  // 登錄檔操作
        EVENT_TRACE_FLAG_DBGPRINT             |  // 調試列印
        EVENT_TRACE_FLAG_PROCESS_COUNTERS     |  // 進程計數器
        EVENT_TRACE_FLAG_CSWITCH              |  // 上下文切換
        EVENT_TRACE_FLAG_DPC                  |  // 延遲過程調用
        EVENT_TRACE_FLAG_INTERRUPT            |  // 中斷
        EVENT_TRACE_FLAG_SYSTEMCALL           |  // 系統調用
        EVENT_TRACE_FLAG_DISK_IO_INIT         |  // 磁碟 I/O 初始化
        EVENT_TRACE_FLAG_ALPC                 |  // ALPC 操作
        EVENT_TRACE_FLAG_SPLIT_IO;               // 分割 I/O 操作

    // User Trace 自訂 Session 名稱
    const TCHAR* myUserSessionName = L"MyUserTraceSession";
    PEVENT_TRACE_PROPERTIES pUserProps = AllocateTraceProperties(userEtl, myUserSessionName);

    // -------------------------------------------------------------
    _tprintf(_T("=== 2. 啟動 Trace Sessions ===\n"));

    // 啟動 Kernel 追蹤 (無額外 Stack Walking 事件需求可傳入空陣列)
    status = this->m_KenerlTrace.StartKernelTrace(&hKernelSession, pKernelProps, 0);
    if (status != ERROR_SUCCESS) {
        wprintf(L"StartKernelTrace 失敗，錯誤代碼: %lu (是否未開管理員權限?)\n", status);
        goto CLEANUP;
    }
    _tprintf(_T("-> Kernel 追蹤已啟動，寫入中: \n"));

    // 啟動 User 追蹤
    status = StartTrace(&hUserSession, myUserSessionName, pUserProps);
    if (status != ERROR_SUCCESS) {
        wprintf(L"StartTrace (User) 失敗，錯誤代碼: %lu\n", status);
        goto CLEANUP;
    }
    _tprintf(_T("-> User 追蹤已啟動，寫入中: \n"));

    // 在這裡你可以透過 EnableTraceEx2 將你特定的 Provider GUID 掛載到 hUserSession 
    // 為了範例簡潔，此處略過特定 Provider 的掛載

    // -------------------------------------------------------------
    _tprintf(_T("=== 3. 正在收集資料 (模擬系統運行 5 秒) ===\n"));
    Sleep(5000);

    // -------------------------------------------------------------
    _tprintf(_T("=== 4. 停止 Sessions 以確保快取全部寫入硬碟 ===\n"));

    // 停止 Kernel 追蹤
    status = ControlTrace(hKernelSession, KERNEL_LOGGER_NAME, pKernelProps, EVENT_TRACE_CONTROL_STOP);
    if (status == ERROR_SUCCESS) _tprintf(_T("-> Kernel 追蹤已成功停止並存檔。\n"));

    // 停止 User 追蹤
    status = ControlTrace(hUserSession, myUserSessionName, pUserProps, EVENT_TRACE_CONTROL_STOP);
    if (status == ERROR_SUCCESS) _tprintf(_T("-> User 追蹤已成功停止並存檔。\n"));

    // -------------------------------------------------------------
    _tprintf(_T("=== 5. 合併 ETL 檔案 ===\n"));
    {
        // 建立要合併的來源檔案路徑陣列
        LPCWSTR traceFiles[] = { kernelEtl, userEtl };
        ULONG fileCount = sizeof(traceFiles) / sizeof(traceFiles[0]);

        // 執行合併：EVENT_TRACE_MERGE_EXTENDED_DATA_DEFAULT 會自動注入符號解析與 OS Build 所需的元數據
        status = this->m_KenerlTrace.CreateMergedTraceFile(mergedEtl, traceFiles, fileCount, EVENT_TRACE_MERGE_EXTENDED_DATA_DEFAULT);

        if (status == ERROR_SUCCESS) {
            _tprintf(_T("🎉 恭喜！合併成功！\n"));
            _tprintf(_T("最終成品檔案位於: %ls\n"), mergedEtl);
            _tprintf(_T("您現在可以直接將此檔案拖入 Windows Performance Analyzer (WPA) 進行分析。\n"));
        }
        else {
            _tprintf(_T("CreateMergedTraceFile 失敗，錯誤代碼: %lu\n"), status);
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
    auto sz_1 = sizeof(EVENT_TRACE_PROPERTIES);
    auto sz_2 = sizeof(LOGFILE_PATH);
    auto sz_3 = sizeof(LOGSESSION_NAME);
    BufferSize = sizeof(EVENT_TRACE_PROPERTIES) + sizeof(LOGFILE_PATH) + sizeof(LOGSESSION_NAME);
    BufferSize = BufferSize;
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

const unsigned MAX_SESSION_NAME_LEN = 1024;
const unsigned MAX_LOGFILE_PATH_LEN = 1024;
const unsigned PropertiesSize = sizeof(EVENT_TRACE_PROPERTIES) + (MAX_SESSION_NAME_LEN * sizeof(TCHAR)) + (MAX_LOGFILE_PATH_LEN * sizeof(TCHAR));
void ETW::CurrentTraces()
{
    ULONG status;
    std::vector<EVENT_TRACE_PROPERTIES*> sessions; // Array of pointers to property structures
    std::vector<BYTE> buffer;                      // Buffer that contains all the property structures
    ULONG sessionCount;                            // Actual number of sessions started on the computer

    // The size of the session name and log file name used by the
    // controllers are not known, therefore create a properties structure that allows
    // for the maximum size of both.
    auto sz_1 = sizeof(EVENT_TRACE_PROPERTIES);
    auto sz_2 = MAX_SESSION_NAME_LEN * sizeof(TCHAR);
	auto sz_3 = MAX_LOGFILE_PATH_LEN * sizeof(TCHAR);
    
    try
    {
        sessionCount = 64; // Start with room for 64 sessions.
        auto bufferszize = PropertiesSize * sessionCount;
        do
        {
            sessions.resize(sessionCount);
            buffer.resize(PropertiesSize * sessionCount);

            for (size_t i = 0; i != sessions.size(); i += 1)
            {
                sessions[i] = (EVENT_TRACE_PROPERTIES*)&buffer[i * PropertiesSize];
                sessions[i]->Wnode.BufferSize = PropertiesSize;
                sessions[i]->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
                sessions[i]->LogFileNameOffset = sizeof(EVENT_TRACE_PROPERTIES) + (MAX_SESSION_NAME_LEN * sizeof(TCHAR));
            }
            auto ff = fopen("win32.bin", "wb");
			fwrite(buffer.data(), 1, buffer.size(), ff);
			fclose(ff);
            status = QueryAllTraces(&sessions[0], sessionCount, &sessionCount);
        } while (status == ERROR_MORE_DATA);

        if (status != ERROR_SUCCESS)
        {
            printf("Error calling QueryAllTraces: %u\n", status);
        }
        else
        {
            printf("Actual session count: %u.\n\n", sessionCount);

            for (ULONG i = 0; i < sessionCount; i++)
            {
                TCHAR sessionGuid[50];
                (void)StringFromGUID2(sessions[i]->Wnode.Guid, sessionGuid, _countof(sessionGuid));

                wprintf(
                    _T("Session GUID: %ls\n"
                    "Session ID: %llu\n"
                    "Session name: %s\n"
                    "Log file: %s\n"
                    "min buffers: %u\n"
                    "max buffers: %u\n"
                    "buffers: %u\n"
                    "buffers written: %u\n"
                    "buffers lost: %u\n"
                    "events lost: %u\n"
                    "\n"),
                    sessionGuid,
                    sessions[i]->Wnode.HistoricalContext,
                    (TCHAR*)((LPCBYTE)sessions[i] + sessions[i]->LoggerNameOffset),
                    (TCHAR*)((LPCBYTE)sessions[i] + sessions[i]->LogFileNameOffset),
                    sessions[i]->MinimumBuffers,
                    sessions[i]->MaximumBuffers,
                    sessions[i]->NumberOfBuffers,
                    sessions[i]->BuffersWritten,
                    sessions[i]->LogBuffersLost,
                    sessions[i]->EventsLost);
                if (wcscmp(sessionGuid, L"{AE44CB98-BD11-4069-8093-770EC9258A12}") == 0)
                {
                    auto hr = ControlTrace((TRACEHANDLE)NULL, (TCHAR*)((LPCBYTE)sessions[i] + sessions[i]->LoggerNameOffset), sessions[i], EVENT_TRACE_CONTROL_STOP);
                }
            }
        }
    }
    catch (std::bad_alloc const&)
    {
        printf("Error allocating memory for properties.\n");
        status = ERROR_OUTOFMEMORY;
    }
}

#pragma comment(lib, "tdh.lib")

#define MAX_GUID_SIZE 39
void ETW::AllProviders()
{
    DWORD status = ERROR_SUCCESS;
    PROVIDER_ENUMERATION_INFO* penum = NULL;    // Buffer that contains provider information
    PROVIDER_ENUMERATION_INFO* ptemp = NULL;
    DWORD BufferSize = 0;                       // Size of the penum buffer
    HRESULT hr = S_OK;                          // Return value for StringFromGUID2
    WCHAR StringGuid[MAX_GUID_SIZE];
    DWORD RegisteredMOFCount = 0;
    DWORD RegisteredManifestCount = 0;

    // Retrieve the required buffer size.

    status = TdhEnumerateProviders(penum, &BufferSize);

    // Allocate the required buffer and call TdhEnumerateProviders. The list of 
    // providers can change between the time you retrieved the required buffer 
    // size and the time you enumerated the providers, so call TdhEnumerateProviders
    // in a loop until the function does not return ERROR_INSUFFICIENT_BUFFER.

    while (ERROR_INSUFFICIENT_BUFFER == status)
    {
        ptemp = (PROVIDER_ENUMERATION_INFO*)realloc(penum, BufferSize);
        if (NULL == ptemp)
        {
            wprintf(L"Allocation failed (size=%lu).\n", BufferSize);
            goto cleanup;
        }

        penum = ptemp;
        ptemp = NULL;

        status = TdhEnumerateProviders(penum, &BufferSize);
    }

    if (ERROR_SUCCESS != status)
    {
        wprintf(L"TdhEnumerateProviders failed with %lu.\n", status);
    }
    else
    {
        // Loop through the list of providers and print the provider's name, GUID, 
        // and the source of the information (MOF class or instrumentation manifest).

        for (DWORD i = 0; i < penum->NumberOfProviders; i++)
        {
            hr = StringFromGUID2(penum->TraceProviderInfoArray[i].ProviderGuid, StringGuid, ARRAYSIZE(StringGuid));

            if (FAILED(hr))
            {
                wprintf(L"StringFromGUID2 failed with 0x%x\n", hr);
                goto cleanup;
            }

            wprintf(L"Provider name: %s\nProvider GUID: %s\nSource: %s\n\n",
                (LPWSTR)((PBYTE)(penum)+penum->TraceProviderInfoArray[i].ProviderNameOffset),
                StringGuid,
                (penum->TraceProviderInfoArray[i].SchemaSource) ? L"WMI MOF class" : L"XML manifest");

            (penum->TraceProviderInfoArray[i].SchemaSource) ? RegisteredMOFCount++ : RegisteredManifestCount++;
        }

        wprintf(L"\nThere are %d registered providers; %lu are registered via MOF class and\n%lu are registered via a manifest.\n",
            penum->NumberOfProviders,
            RegisteredMOFCount,
            RegisteredManifestCount);
    }

cleanup:

    if (penum)
    {
        free(penum);
        penum = NULL;
    }
}