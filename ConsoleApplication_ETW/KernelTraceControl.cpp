#include <stdio.h>
#include <Windows.h>
#include <KernelTraceControl.h>

void CreateMergeFile(LPCWSTR wszMergedFileName,
    LPCWSTR wszTraceFileNames[],
    ULONG   cTraceFileNames,
    DWORD   dwExtendedDataFlags)
{
    // 定義函式指標型別
    typedef ULONG(WINAPI* PFN_CreateMergedTraceFile)(
        LPCWSTR wszMergedFileName,
        LPCWSTR wszTraceFileNames[],
        ULONG   cTraceFileNames,
        DWORD   dwExtendedDataFlags
        );

    // 動態載入 KernelTraceControl.dll
    HMODULE hLib = LoadLibraryW(L"KernelTraceControl.dll");
    if (hLib == NULL)
    {
        wprintf(L"LoadLibrary failed: %lu\n", GetLastError());
        return;
    }

    auto pfnCreateMergedTraceFile = (PFN_CreateMergedTraceFile)GetProcAddress(hLib, "CreateMergedTraceFile");

    if (pfnCreateMergedTraceFile == NULL)
    {
        wprintf(L"GetProcAddress failed: %lu\n", GetLastError());
        FreeLibrary(hLib);
        return;
    }

    LPCWSTR traceFiles[] = { L"test.etl" };
    ULONG status = pfnCreateMergedTraceFile(
        wszMergedFileName,
        wszTraceFileNames,
        cTraceFileNames,
        dwExtendedDataFlags);

    wprintf(L"CreateMergedTraceFile result: %lu\n", status);
    FreeLibrary(hLib);
}


void StartKernelTrace(LPCWSTR wszMergedFileName,
    LPCWSTR wszTraceFileNames[],
    ULONG   cTraceFileNames,
    DWORD   dwExtendedDataFlags)
{
    // 定義函式指標型別
    typedef ULONG(WINAPI* PFN_CreateMergedTraceFile)(
        LPCWSTR wszMergedFileName,
        LPCWSTR wszTraceFileNames[],
        ULONG   cTraceFileNames,
        DWORD   dwExtendedDataFlags
        );

    // 動態載入 KernelTraceControl.dll
    HMODULE hLib = LoadLibraryW(L"KernelTraceControl.dll");
    if (hLib == NULL)
    {
        wprintf(L"LoadLibrary failed: %lu\n", GetLastError());
        return;
    }

    auto pfnCreateMergedTraceFile = (PFN_CreateMergedTraceFile)GetProcAddress(hLib, "CreateMergedTraceFile");

    if (pfnCreateMergedTraceFile == NULL)
    {
        wprintf(L"GetProcAddress failed: %lu\n", GetLastError());
        FreeLibrary(hLib);
        return;
    }

    LPCWSTR traceFiles[] = { L"test.etl" };
    ULONG status = pfnCreateMergedTraceFile(
        wszMergedFileName,
        wszTraceFileNames,
        cTraceFileNames,
        dwExtendedDataFlags);

    wprintf(L"CreateMergedTraceFile result: %lu\n", status);
    FreeLibrary(hLib);
}