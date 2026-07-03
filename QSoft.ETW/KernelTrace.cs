using System;
using System.Runtime.InteropServices;

namespace QSoft.ETW;

public sealed class KernelTrace : IDisposable
{
    private const uint ErrorInvalidState = 0xC00000BBu;

    private IntPtr _libraryHandle;
    private readonly StartKernelTraceDelegate? _startKernelTrace;
    private readonly StartHeapTraceDelegate? _startHeapTrace;
    private readonly UpdateHeapTraceDelegate? _updateHeapTrace;
    private readonly CreateMergedTraceFileDelegate? _createMergedTraceFile;

    public KernelTrace(string? libraryPath = null)
    {
        libraryPath ??= "KernelTraceControl.dll";
        _libraryHandle = NativeLibrary.Load(libraryPath);

        _startKernelTrace = LoadFunction<StartKernelTraceDelegate>("StartKernelTrace");
        _startHeapTrace = LoadFunction<StartHeapTraceDelegate>("StartHeapTrace");
        _updateHeapTrace = LoadFunction<UpdateHeapTraceDelegate>("UpdateHeapTrace");
        _createMergedTraceFile = LoadFunction<CreateMergedTraceFileDelegate>("CreateMergedTraceFile");
    }

    public bool Initialize() =>
        _startKernelTrace is not null &&
        _startHeapTrace is not null &&
        _updateHeapTrace is not null &&
        _createMergedTraceFile is not null;

    public uint StartKernelTrace(out nint traceHandle, byte[]? properties, uint cStackTracingEventIds)
    {
        if (_startKernelTrace is null)
        {
            Console.Error.WriteLine("StartKernelTrace function not initialized");
            traceHandle = nint.Zero;
            return ErrorInvalidState;
        }

        return _startKernelTrace(out traceHandle, properties, cStackTracingEventIds);
    }

    public uint StartHeapTrace(string sessionName, uint processIdCount, uint[]? processIds)
    {
        if (_startHeapTrace is null)
        {
            Console.Error.WriteLine("StartHeapTrace function not initialized");
            return ErrorInvalidState;
        }

        return _startHeapTrace(sessionName, processIdCount, processIds);
    }

    public uint UpdateHeapTrace(string sessionName, uint processIdCount, uint[]? processIds)
    {
        if (_updateHeapTrace is null)
        {
            Console.Error.WriteLine("UpdateHeapTrace function not initialized");
            return ErrorInvalidState;
        }

        return _updateHeapTrace(sessionName, processIdCount, processIds);
    }

    public uint CreateMergedTraceFile(string mergedFileName, string[]? traceFileNames, uint traceFileCount, uint extendedDataFlags)
    {
        if (_createMergedTraceFile is null)
        {
            Console.Error.WriteLine("CreateMergedTraceFile function not initialized");
            return ErrorInvalidState;
        }

        return _createMergedTraceFile(mergedFileName, traceFileNames, traceFileCount, extendedDataFlags);
    }

    private TDelegate? LoadFunction<TDelegate>(string functionName)
        where TDelegate : Delegate
    {
        IntPtr functionPtr = NativeLibrary.GetExport(_libraryHandle, functionName);
        if (functionPtr == IntPtr.Zero)
        {
            Console.Error.WriteLine($"GetExport failed for {functionName}");
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(functionPtr);
    }

    public void Dispose()
    {
        if (_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint StartKernelTraceDelegate(out nint traceHandle, byte[]? properties, uint cStackTracingEventIds);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate uint StartHeapTraceDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string sessionName,
        uint processIdCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U4)] uint[]? processIds);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate uint UpdateHeapTraceDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string sessionName,
        uint processIdCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U4)] uint[]? processIds);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate uint CreateMergedTraceFileDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string mergedFileName,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? traceFileNames,
        uint traceFileCount,
        uint extendedDataFlags);
}