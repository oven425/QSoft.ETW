
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Services;
using Microsoft.Windows.EventTracing.Symbols;
using QSoft.ETW;

internal sealed record ProcessSnapshot(
    long ProcessId,
    long ParentProcessId,
    long? ResolvedParentProcessId,
    long? ParentObjectAddress,
    long SessionId,
    long ObjectAddress,
    string ImageFileName,
    DateTime? CreateTime,
    DateTime? ExitTime,
    double? DurationMilliseconds,
    int? ExitCode,
    string UserSid,
    string CommandLine,
    ProcessPackageSnapshot? Package,
    IReadOnlyList<ProcessImageSnapshot> Images,
    ProcessStackSnapshot? CreateStack,
    ProcessStackSnapshot? ExitStack);

internal sealed record ProcessPackageSnapshot(
    string FullName,
    string RelativeApplicationId,
    string? FriendlyName);

internal sealed record ProcessImageSnapshot(
    long ProcessId,
    string FileName,
    string Path,
    long ImageBase,
    long ImageLimit,
    long ImageSize,
    long DefaultBase,
    long Checksum,
    long Timestamp,
    DateTime? LoadTime,
    DateTime? UnloadTime,
    double? LoadedDurationMilliseconds,
    long? LoadingThreadId,
    long? UnloadingThreadId);

internal sealed record ProcessStackSnapshot(
    DateTime Timestamp,
    long ProcessId,
    long ThreadId,
    int Processor,
    bool IsIdle,
    IReadOnlyList<long> FrameAddresses);

internal sealed record MemoryUtilizationSnapshot(
    DateTime Timestamp,
    long ZeroListSize,
    long FreeListSize,
    long InUseListSize,
    long ModifiedListSize,
    long ModifiedNoWriteListSize,
    long BadListSize,
    long StandbyListSize,
    long NonSuperfetchStandbyListSize,
    long RepurposedListSize,
    IReadOnlyList<long> StandbySubListSizes,
    IReadOnlyList<long> RepurposedSubListSizes,
    long ModifiedPageFileDataSize,
    long PagedPoolCommitSize,
    long NonPagedPoolSize,
    long MemoryDescriptorListAllocationsSize,
    long CommitSize);

internal sealed record ServiceDataSnapshot(
    IReadOnlyList<ServiceObjectSnapshot> Services,
    IReadOnlyList<ServiceConfigurationSnapshot> Snapshots);

internal sealed record ServiceObjectSnapshot(
    string Name,
    int? StartType,
    bool StartTypeUnavailable,
    ServiceGroupSnapshot? Group,
    IReadOnlyList<ServiceStateChangeSnapshot> StateChanges,
    IReadOnlyList<ServiceTransitionSnapshot> Transitions);

internal sealed record ServiceGroupSnapshot(
    string Name,
    long ThreadId,
    DateTime? StartTime,
    DateTime? StopTime,
    long? DurationTicks);

internal sealed record ServiceStateChangeSnapshot(
    DateTime Timestamp,
    int State,
    long ProcessId,
    string ImageName,
    long ServiceManagementThreadId,
    long? ResolvedProcessId,
    long? ResolvedProcessObjectAddress,
    long? ResolvedServiceManagementThreadId,
    long? ResolvedServiceManagementThreadProcessId,
    DateTime? ResolvedServiceManagementThreadCreateTime,
    long? ResolvedServiceManagementProcessId,
    long? ResolvedServiceManagementProcessObjectAddress);

internal sealed record ServiceTransitionSnapshot(
    int TransitionType,
    int? StartEventIndex,
    int? EndEventIndex,
    IReadOnlyList<int> CheckpointIndices,
    DateTime? ServiceMainCallStartTime,
    DateTime? ServiceMainCallEndTime,
    long? ServiceMainCallDurationTicks);

internal sealed record ServiceConfigurationSnapshot(
    int Id,
    string Name,
    string DisplayName,
    string GroupName,
    int ProcessId,
    string ProcessName,
    int State);

internal sealed record ThreadSnapshot(
    long ThreadId,
    long ProcessId,
    string Name,
    DateTime? CreateTime,
    DateTime? ExitTime,
    int BasePriority,
    int? PagePriority,
    int? IoPriority,
    long ProcessorAffinity,
    long? StackBase,
    long? StackLimit,
    long? UserStackBase,
    long? UserStackLimit,
    long? StartAddress,
    long? TebBase,
    int? ServiceId,
    string StartAddressModuleName);


internal sealed record InterruptModuleSnapshot(string ModuleName, int IsrCount, int DpcCount);

internal sealed record DpcSnapshot(
    int Processor,
    DateTime StartTime,
    DateTime StopTime,
    long HandlerRoutineAddress,
    string HandlerModuleName,
    long? HandlerRelativeVirtualAddress,
    string? HandlerSymbolName,
    string CallType);

/// <summary>
/// EtlFileReader 的 <see cref="InterruptServiceRoutineInterval"/> 與 TraceProcessor 的
/// <see cref="IInterruptServiceRoutineInterval"/> 共用的比對快照。
/// </summary>
internal sealed record IsrSnapshot(
    int Processor,
    DateTime StartTime,
    DateTime StopTime,
    long HandlerRoutineAddress,
    string HandlerModuleName,
    long? HandlerRelativeVirtualAddress,
    string? HandlerSymbolName,
    int? Vector,
    byte? ReturnValue);

internal sealed record DemoResult_Etl(
    TimeSpan Elapsed,
    DateTime? TraceStartTime,
    DateTime? TraceEndTime,
    IReadOnlyList<ProcessRecord> Processes,
    ServiceDataSnapshot Services,
    IReadOnlyList<MemoryUtilizationEventInfo> MemoryUtilizationSnapshots,
    IReadOnlyList<InterruptServiceRoutineInterval> InterruptServiceRoutines,
    IReadOnlyList<DeferredProcedureCallInterval> DeferredProcedureCalls);

internal sealed record DemoResult_Tp(
    TimeSpan Elapsed,
    IReadOnlyList<ProcessSnapshot> Processes,
    ServiceDataSnapshot Services,
    IReadOnlyList<MemoryUtilizationSnapshot> MemoryUtilizationSnapshots,
    IReadOnlyList<IsrSnapshot> InterruptServiceRoutines,
    IReadOnlyList<DpcSnapshot> DeferredProcedureCalls);

internal sealed record DemoResult(
    TimeSpan Elapsed,
    IReadOnlyList<ProcessSnapshot> Processes,
    IReadOnlyList<ServiceObjectSnapshot> Services,
    int EnergyIntervalCount,
    double EnergyTotalMilliJoules,
    
    IReadOnlyList<InterruptModuleSnapshot> InterruptModules,
    IReadOnlyList<ThreadSnapshot> Threads);

internal static class DemoResultValidator
{
    private const double ProcessTimestampToleranceMilliseconds = 0.01;

    public static void Validate(DemoResult_Etl etlFileReaderResult, DemoResult_Tp traceProcessorResult)
    {
        Console.WriteLine();
        Console.WriteLine("=== 驗證：EtlFileReader 與 TraceProcessor 解析同一份 ETL 的結果比對（以 TraceProcessor 為基準） ===");
        ValidateProcesses(
            etlFileReaderResult.Processes,
            traceProcessorResult.Processes,
            etlFileReaderResult.TraceStartTime,
            etlFileReaderResult.TraceEndTime);
        ValidateServices(etlFileReaderResult.Services, traceProcessorResult.Services);
        ValidateMemoryUtilization(
            etlFileReaderResult.MemoryUtilizationSnapshots,
            traceProcessorResult.MemoryUtilizationSnapshots);
        ValidateDeferredProcedureCalls(etlFileReaderResult.DeferredProcedureCalls, traceProcessorResult.DeferredProcedureCalls);
        ValidateInterruptServiceRoutines(etlFileReaderResult.InterruptServiceRoutines, traceProcessorResult.InterruptServiceRoutines);
        //ValidateThreads(etlFileReaderResult.Threads, traceProcessorResult.Threads);
        //ValidateEnergyEstimation(etlFileReaderResult, traceProcessorResult);
        //ValidateInterruptHandling(etlFileReaderResult.InterruptModules, traceProcessorResult.InterruptModules);
    }

    public static IReadOnlyList<InterruptModuleSnapshot> BuildInterruptModuleSnapshots(
        IEnumerable<string> isrModuleNames,
        IEnumerable<string> dpcModuleNames)
    {
        Dictionary<string, int> isrCounts = CountByModule(isrModuleNames);
        Dictionary<string, int> dpcCounts = CountByModule(dpcModuleNames);

        return [.. isrCounts.Keys
            .Union(dpcCounts.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(module => new InterruptModuleSnapshot(
                module,
                isrCounts.GetValueOrDefault(module),
                dpcCounts.GetValueOrDefault(module)))];

        static Dictionary<string, int> CountByModule(IEnumerable<string> moduleNames) =>
            moduleNames
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    internal static ProcessSnapshot ToProcessSnapshot(IProcess process)
    {
        ProcessPackageSnapshot? package = process.Package is IPackageMetadata packageMetadata
            ? new ProcessPackageSnapshot(
                packageMetadata.FullName ?? string.Empty,
                packageMetadata.RelativeApplicationId ?? string.Empty,
                packageMetadata.FriendlyName)
            : null;

        return new ProcessSnapshot(
            process.Id,
            process.ParentId,
            process.Parent?.Id,
            process.Parent is null ? null : NormalizeAddress(process.Parent.ObjectAddress.Value),
            process.SessionId,
            NormalizeAddress(process.ObjectAddress.Value),
            process.ImageName ?? string.Empty,
            process.CreateTime?.DateTimeOffset.UtcDateTime,
            process.ExitTime?.DateTimeOffset.UtcDateTime,
            process.Duration is TraceDuration duration ? (double)duration.TotalMilliseconds : null,
            process.ExitCode,
            process.User?.Value ?? string.Empty,
            process.CommandLine ?? string.Empty,
            package,
            [.. process.Images.Select(ToProcessImageSnapshot)],
            ToProcessStackSnapshot(process.CreateStack),
            ToProcessStackSnapshot(process.ExitStack));
    }

    private static ProcessSnapshot ToProcessSnapshot(ProcessRecord process)
    {
        ProcessPackageSnapshot? package =
            string.IsNullOrEmpty(process.PackageFullName) && string.IsNullOrEmpty(process.ApplicationId)
                ? null
                : new ProcessPackageSnapshot(process.PackageFullName, process.ApplicationId, null);

        return new ProcessSnapshot(
            ToSignedId(process.ProcessId),
            ToSignedId(process.ParentProcessId),
            process.Parent is null ? null : ToSignedId(process.Parent.ProcessId),
            process.Parent is null ? null : NormalizeAddress(process.Parent.ObjectAddress),
            ToSignedId(process.SessionId),
            NormalizeAddress(process.ObjectAddress),
            process.ImageFileName,
            process.StartTime?.ToUniversalTime(),
            process.EndTime?.ToUniversalTime(),
            process.Duration?.TotalMilliseconds,
            process.ExitCode,
            process.UserSid,
            process.CommandLine,
            package,
            [.. process.Modules.Select(ToProcessImageSnapshot)],
            null,
            null);
    }

    private static ProcessImageSnapshot ToProcessImageSnapshot(IImage image) => new(
        image.ProcessId,
        image.FileName ?? string.Empty,
        image.Path ?? string.Empty,
        image.AddressRange.BaseAddress.Value,
        image.AddressRange.LimitAddress.Value,
        image.Size.Bytes,
        image.DefaultBaseAddress.Value,
        image.Checksum,
        image.Timestamp,
        image.LoadTime?.DateTimeOffset.UtcDateTime,
        image.UnloadTime?.DateTimeOffset.UtcDateTime,
        image.LoadedDuration is TraceDuration duration ? (double)duration.TotalMilliseconds : null,
        NormalizeOptionalId(image.LoadingThreadId),
        NormalizeOptionalId(image.UnloadingThreadId));

    private static ProcessImageSnapshot ToProcessImageSnapshot(ModuleInfo image)
    {
        ulong limit = unchecked(image.ImageBase + image.ImageSize);
        return new ProcessImageSnapshot(
            ToSignedId(image.ProcessId),
            Path.GetFileName(image.FileName),
            image.FileName,
            unchecked((long)image.ImageBase),
            unchecked((long)limit),
            unchecked((long)image.ImageSize),
            image.DefaultBase is ulong defaultBase ? unchecked((long)defaultBase) : 0,
            image.ImageCheckSum ?? 0,
            image.TimeDateStamp ?? 0,
            image.IsRundown ? null : image.LoadTime.ToUniversalTime(),
            image.UnloadTime?.ToUniversalTime(),
            !image.IsRundown && image.UnloadTime is DateTime unloadTime
                ? (unloadTime - image.LoadTime).TotalMilliseconds
                : null,
            NormalizeOptionalId(image.LoadingThreadId),
            NormalizeOptionalId(image.UnloadingThreadId));
    }

    private static ProcessStackSnapshot? ToProcessStackSnapshot(IStackSnapshot? stack)
    {
        if (stack is null || stack.Frames.Count == 0)
        {
            return null;
        }

        return new ProcessStackSnapshot(
            stack.Timestamp.DateTimeOffset.UtcDateTime,
            stack.ProcessId,
            stack.ThreadId,
            stack.Processor,
            stack.IsIdle,
            [.. stack.Frames.Select(frame => frame.Address.Value)]);
    }

    private static long ToSignedId(uint value) => unchecked((int)value);

    private static long? NormalizeOptionalId(int? value) =>
        value is null or <= 0 ? null : value;

    private static long? NormalizeOptionalId(uint? value) =>
        value is null or 0 or uint.MaxValue ? null : ToSignedId(value.Value);

    private static long NormalizeAddress(long value) =>
        value & 0x0000_FFFF_FFFF_FFFFL;

    private static long NormalizeAddress(ulong value) =>
        unchecked((long)(value & 0x0000_FFFF_FFFF_FFFFUL));

    internal static ServiceDataSnapshot ToServiceDataSnapshot(QSoft.ETW.IServiceDataSource dataSource)
    {
        List<ServiceObjectSnapshot> services = [];
        foreach (ServiceRecord service in dataSource.Services)
        {
            IReadOnlyList<ServiceStateChangeSnapshot> stateChanges =
                [.. service.StateChanges.Select(ToServiceStateChangeSnapshot)];
            IReadOnlyList<ServiceTransitionSnapshot> transitions =
                [.. service.Transitions.Select(transition =>
                    new ServiceTransitionSnapshot(
                        (int)transition.TransitionType,
                        service.StateChanges.IndexOf(transition.StartEvent),
                        transition.EndEvent is null ? null : service.StateChanges.IndexOf(transition.EndEvent),
                        [.. transition.Checkpoints.Select(checkpoint => service.StateChanges.IndexOf(checkpoint))],
                        transition.ServiceMainCallTimeRange?.StartTime.ToUniversalTime(),
                        transition.ServiceMainCallTimeRange?.EndTime.ToUniversalTime(),
                        transition.ServiceMainCallTimeRange?.Duration.Ticks))];

            services.Add(new ServiceObjectSnapshot(
                service.Name,
                service.StartType is QSoft.ETW.ServiceStartType startType ? (int)startType : null,
                service.StartType is null,
                service.Group is null
                    ? null
                    : new ServiceGroupSnapshot(
                        service.Group.Name,
                        ToSignedId(service.Group.ThreadId),
                        service.Group.StartTime?.ToUniversalTime(),
                        service.Group.StopTime?.ToUniversalTime(),
                        service.Group.Duration?.Ticks),
                stateChanges,
                transitions));
        }

        return new ServiceDataSnapshot(
            services,
            [.. dataSource.Snapshots.Select(snapshot => new ServiceConfigurationSnapshot(
                snapshot.Id,
                snapshot.Name,
                snapshot.DisplayName,
                snapshot.GroupName,
                snapshot.ProcessId,
                snapshot.ProcessName,
                (int)snapshot.State))]);
    }

    internal static ServiceDataSnapshot ToServiceDataSnapshot(
        Microsoft.Windows.EventTracing.Services.IServiceDataSource dataSource)
    {
        List<ServiceObjectSnapshot> services = [];
        foreach (IService service in dataSource.Services)
        {
            IReadOnlyList<IServiceStateChange> sourceStateChanges = service.StateChanges;
            IReadOnlyList<ServiceStateChangeSnapshot> stateChanges =
                [.. sourceStateChanges.Select(ToServiceStateChangeSnapshot)];
            List<ServiceTransitionSnapshot> transitions = [];
            foreach (IServiceTransition transition in service.Transitions)
            {
                TraceTimeRange? range = transition.ServiceMainCallTimeRange;
                transitions.Add(new ServiceTransitionSnapshot(
                    (int)transition.TransitionType,
                    FindStateChangeIndex(sourceStateChanges, transition.StartEvent),
                    FindStateChangeIndex(sourceStateChanges, transition.EndEvent),
                    [.. transition.Checkpoints.Select(checkpoint =>
                        FindStateChangeIndex(sourceStateChanges, checkpoint) ?? -1)],
                    range?.StartTime?.DateTimeOffset.UtcDateTime,
                    range?.EndTime?.DateTimeOffset.UtcDateTime,
                    range?.Duration?.TimeSpan.Ticks));
            }

            int? startType = null;
            bool startTypeUnavailable = false;
            try
            {
                startType = (int)service.StartType;
            }
            catch (InvalidOperationException)
            {
                startTypeUnavailable = true;
            }

            IServiceGroup? group = service.Group;
            services.Add(new ServiceObjectSnapshot(
                service.Name ?? string.Empty,
                startType,
                startTypeUnavailable,
                group is null
                    ? null
                    : new ServiceGroupSnapshot(
                        group.Name ?? string.Empty,
                        group.ThreadId,
                        group.StartTime?.DateTimeOffset.UtcDateTime,
                        group.StopTime?.DateTimeOffset.UtcDateTime,
                        group.Duration?.TimeSpan.Ticks),
                stateChanges,
                transitions));
        }

        return new ServiceDataSnapshot(
            services,
            [.. dataSource.Snapshots.Select(snapshot => new ServiceConfigurationSnapshot(
                snapshot.Id,
                snapshot.Name ?? string.Empty,
                snapshot.DisplayName ?? string.Empty,
                snapshot.GroupName ?? string.Empty,
                snapshot.ProcessId,
                snapshot.ProcessName ?? string.Empty,
                (int)snapshot.State))]);
    }

    private static ServiceStateChangeSnapshot ToServiceStateChangeSnapshot(ServiceStateChangeRecord stateChange)
    {
        ThreadRecord? managementThread = stateChange.ServiceManagementThread;
        return new ServiceStateChangeSnapshot(
            stateChange.Timestamp.ToUniversalTime(),
            (int)stateChange.State,
            ToSignedId(stateChange.ProcessId),
            stateChange.ImageName,
            ToSignedId(stateChange.ServiceManagementThreadId),
            stateChange.Process is null ? null : ToSignedId(stateChange.Process.ProcessId),
            stateChange.Process is null ? null : NormalizeAddress(stateChange.Process.ObjectAddress),
            managementThread is null ? null : ToSignedId(managementThread.ThreadId),
            managementThread is null ? null : ToSignedId(managementThread.ProcessId),
            managementThread?.CreateTime?.ToUniversalTime(),
            stateChange.ServiceManagementProcess is null
                ? null
                : ToSignedId(stateChange.ServiceManagementProcess.ProcessId),
            stateChange.ServiceManagementProcess is null
                ? null
                : NormalizeAddress(stateChange.ServiceManagementProcess.ObjectAddress));
    }

    private static ServiceStateChangeSnapshot ToServiceStateChangeSnapshot(IServiceStateChange stateChange)
    {
        IProcess? process = stateChange.Process;
        Microsoft.Windows.EventTracing.Processes.IThread? managementThread =
            stateChange.ServiceManagementThread;
        IProcess? managementProcess = stateChange.ServiceManagementProcess;
        return new ServiceStateChangeSnapshot(
            stateChange.Timestamp.DateTimeOffset.UtcDateTime,
            (int)stateChange.State,
            stateChange.ProcessId,
            stateChange.ImageName ?? string.Empty,
            stateChange.ServiceManagementThreadId,
            process?.Id,
            process is null ? null : NormalizeAddress(process.ObjectAddress.Value),
            managementThread?.Id,
            managementThread?.ProcessId,
            managementThread?.CreateTime?.DateTimeOffset.UtcDateTime,
            managementProcess?.Id,
            managementProcess is null ? null : NormalizeAddress(managementProcess.ObjectAddress.Value));
    }

    private static int? FindStateChangeIndex(
        IReadOnlyList<IServiceStateChange> stateChanges,
        IServiceStateChange? target)
    {
        if (target is null)
        {
            return null;
        }

        for (int i = 0; i < stateChanges.Count; i++)
        {
            if (ReferenceEquals(stateChanges[i], target))
            {
                return i;
            }
        }
        return -1;
    }

    internal static MemoryUtilizationSnapshot ToMemoryUtilizationSnapshot(
        IMemoryUtilizationSnapshot snapshot) => new(
            snapshot.Timestamp.DateTimeOffset.UtcDateTime,
            snapshot.ZeroListSize.Bytes,
            snapshot.FreeListSize.Bytes,
            snapshot.InUseListSize.Bytes,
            snapshot.ModifiedListSize.Bytes,
            snapshot.ModifiedNoWriteListSize.Bytes,
            snapshot.BadListSize.Bytes,
            snapshot.StandbyListSize.Bytes,
            snapshot.NonSuperfetchStandbyListSize.Bytes,
            snapshot.RepurposedListSize.Bytes,
            [.. snapshot.StandbySubListSizes.Select(size => size.Bytes)],
            [.. snapshot.RepurposedSubListSizes.Select(size => size.Bytes)],
            snapshot.ModifiedPageFileDataSize.Bytes,
            snapshot.PagedPoolCommitSize.Bytes,
            snapshot.NonPagedPoolSize.Bytes,
            snapshot.MemoryDescriptorListAllocationsSize.Bytes,
            snapshot.CommitSize.Bytes);

    private static MemoryUtilizationSnapshot ToMemoryUtilizationSnapshot(
        MemoryUtilizationEventInfo snapshot)
    {
        long[] standbySubListSizes =
            [.. snapshot.StandbyPriorityPageCounts.Select(PageCountToBytes)];

        return new MemoryUtilizationSnapshot(
            snapshot.Timestamp.ToUniversalTime(),
            PageCountToBytes(snapshot.ZeroPageCount),
            PageCountToBytes(snapshot.FreePageCount),
            PageCountToBytes(snapshot.InUsePageCount),
            PageCountToBytes(snapshot.ModifiedPageCount),
            PageCountToBytes(snapshot.ModifiedNoWritePageCount),
            PageCountToBytes(snapshot.BadPageCount),
            standbySubListSizes.Sum(),
            PageCountToBytes(snapshot.NonSuperfetchStandbyPageCount),
            PageCountToBytes(snapshot.RepurposedTotalPageCount),
            standbySubListSizes,
            [.. snapshot.RepurposedSubListPageCounts.Select(PageCountToBytes)],
            PageCountToBytes(snapshot.ModifiedPageFilePageCount),
            PageCountToBytes(snapshot.PagedPoolPageCount),
            PageCountToBytes(snapshot.NonPagedPoolPageCount),
            PageCountToBytes(snapshot.MdlPageCount),
            PageCountToBytes(snapshot.CommitPageCount));
    }

    private static long PageCountToBytes(long pageCount) =>
        checked(pageCount * MemoryUtilizationEventInfo.PageSizeBytes);

    private static void ValidateMemoryUtilization(
        IReadOnlyList<MemoryUtilizationEventInfo> etl,
        IReadOnlyList<MemoryUtilizationSnapshot> tp)
    {
        List<MemoryUtilizationSnapshot> etlSnapshots =
            [.. etl.Select(ToMemoryUtilizationSnapshot).OrderBy(snapshot => snapshot.Timestamp)];
        List<MemoryUtilizationSnapshot> tpSnapshots =
            [.. tp.OrderBy(snapshot => snapshot.Timestamp)];
        int pairCount = Math.Min(etlSnapshots.Count, tpSnapshots.Count);
        var differences =
            new List<(int Index, MemoryUtilizationSnapshot Etl, MemoryUtilizationSnapshot Tp, IReadOnlyList<string> Fields)>();
        Dictionary<string, int> mismatchCounts = new(StringComparer.Ordinal);

        for (int index = 0; index < pairCount; index++)
        {
            MemoryUtilizationSnapshot etlSnapshot = etlSnapshots[index];
            MemoryUtilizationSnapshot tpSnapshot = tpSnapshots[index];
            List<string> fields = GetMemoryUtilizationDifferences(etlSnapshot, tpSnapshot);
            if (fields.Count == 0)
            {
                continue;
            }

            differences.Add((index, etlSnapshot, tpSnapshot, fields));
            foreach (string field in fields)
            {
                mismatchCounts[field] = mismatchCounts.GetValueOrDefault(field) + 1;
            }
        }

        Console.WriteLine(
            $"[UseMemoryUtilizationData] EtlFileReader={etlSnapshots.Count} 筆, " +
            $"TraceProcessor={tpSnapshots.Count} 筆, 依 Timestamp 排序配對={pairCount} 筆, " +
            $"僅 EtlFileReader 多出={etlSnapshots.Count - pairCount} 筆, " +
            $"僅 TraceProcessor 多出={tpSnapshots.Count - pairCount} 筆, " +
            $"內容不同={differences.Count} 筆");
        Console.WriteLine("  全部 IMemoryUtilizationSnapshot 屬性差異：" +
            (mismatchCounts.Count == 0
                ? "無"
                : string.Join(", ", mismatchCounts
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}"))));

        foreach ((int index, MemoryUtilizationSnapshot etlSnapshot, MemoryUtilizationSnapshot tpSnapshot, IReadOnlyList<string> fields)
                 in differences.Take(MaxDifferenceSamplesToPrint))
        {
            Console.WriteLine(
                $"  #{index} @ {etlSnapshot.Timestamp:O}: 不同欄位={string.Join(", ", fields)}");
            Console.WriteLine($"    EtlFileReader: {FormatMemoryUtilization(etlSnapshot)}");
            Console.WriteLine($"    TraceProcessor: {FormatMemoryUtilization(tpSnapshot)}");
        }
    }

    private static List<string> GetMemoryUtilizationDifferences(
        MemoryUtilizationSnapshot etl,
        MemoryUtilizationSnapshot tp)
    {
        List<string> fields = [];
        AddIfDifferent(fields, "Timestamp", !TimestampEquals(etl.Timestamp, tp.Timestamp));
        AddIfDifferent(fields, "ZeroListSize", etl.ZeroListSize != tp.ZeroListSize);
        AddIfDifferent(fields, "FreeListSize", etl.FreeListSize != tp.FreeListSize);
        AddIfDifferent(fields, "InUseListSize", etl.InUseListSize != tp.InUseListSize);
        AddIfDifferent(fields, "ModifiedListSize", etl.ModifiedListSize != tp.ModifiedListSize);
        AddIfDifferent(
            fields,
            "ModifiedNoWriteListSize",
            etl.ModifiedNoWriteListSize != tp.ModifiedNoWriteListSize);
        AddIfDifferent(fields, "BadListSize", etl.BadListSize != tp.BadListSize);
        AddIfDifferent(fields, "StandbyListSize", etl.StandbyListSize != tp.StandbyListSize);
        AddIfDifferent(
            fields,
            "NonSuperfetchStandbyListSize",
            etl.NonSuperfetchStandbyListSize != tp.NonSuperfetchStandbyListSize);
        AddIfDifferent(
            fields,
            "RepurposedListSize",
            etl.RepurposedListSize != tp.RepurposedListSize);
        AddIfDifferent(
            fields,
            "StandbySubListSizes",
            !etl.StandbySubListSizes.SequenceEqual(tp.StandbySubListSizes));
        AddIfDifferent(
            fields,
            "RepurposedSubListSizes",
            !etl.RepurposedSubListSizes.SequenceEqual(tp.RepurposedSubListSizes));
        AddIfDifferent(
            fields,
            "ModifiedPageFileDataSize",
            etl.ModifiedPageFileDataSize != tp.ModifiedPageFileDataSize);
        AddIfDifferent(
            fields,
            "PagedPoolCommitSize",
            etl.PagedPoolCommitSize != tp.PagedPoolCommitSize);
        AddIfDifferent(fields, "NonPagedPoolSize", etl.NonPagedPoolSize != tp.NonPagedPoolSize);
        AddIfDifferent(
            fields,
            "MemoryDescriptorListAllocationsSize",
            etl.MemoryDescriptorListAllocationsSize != tp.MemoryDescriptorListAllocationsSize);
        AddIfDifferent(fields, "CommitSize", etl.CommitSize != tp.CommitSize);
        return fields;
    }

    private static string FormatMemoryUtilization(MemoryUtilizationSnapshot snapshot) =>
        $"Timestamp={snapshot.Timestamp:O}, Zero={snapshot.ZeroListSize}, Free={snapshot.FreeListSize}, " +
        $"InUse={snapshot.InUseListSize}, Modified={snapshot.ModifiedListSize}, " +
        $"ModifiedNoWrite={snapshot.ModifiedNoWriteListSize}, Bad={snapshot.BadListSize}, " +
        $"Standby={snapshot.StandbyListSize}, NonSuperfetchStandby={snapshot.NonSuperfetchStandbyListSize}, " +
        $"Repurposed={snapshot.RepurposedListSize}, " +
        $"StandbySubLists=[{string.Join(", ", snapshot.StandbySubListSizes)}], " +
        $"RepurposedSubLists=[{string.Join(", ", snapshot.RepurposedSubListSizes)}], " +
        $"ModifiedPageFile={snapshot.ModifiedPageFileDataSize}, PagedPool={snapshot.PagedPoolCommitSize}, " +
        $"NonPagedPool={snapshot.NonPagedPoolSize}, MDL={snapshot.MemoryDescriptorListAllocationsSize}, " +
        $"Commit={snapshot.CommitSize}";

    private static void ValidateProcesses(
        IReadOnlyList<ProcessRecord> etl,
        IReadOnlyList<ProcessSnapshot> tp,
        DateTime? traceStartTime,
        DateTime? traceEndTime)
    {
        List<ProcessSnapshot> etlSnapshots = [.. etl.Select(ToProcessSnapshot)];
        (
            List<(ProcessSnapshot Etl, ProcessSnapshot Tp)> matched,
            int pidCountMismatch,
            List<ProcessSnapshot> etlOnly,
            List<ProcessSnapshot> tpOnly) = MatchProcessesById(etlSnapshots, tp);

        var differences = new List<(ProcessSnapshot Etl, ProcessSnapshot Tp, IReadOnlyList<string> Fields)>();
        var traceProcessorAnomalies =
            new List<(ProcessSnapshot Etl, ProcessSnapshot Tp, IReadOnlyList<string> Fields)>();
        Dictionary<string, int> mismatchCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> anomalyCounts = new(StringComparer.Ordinal);
        foreach ((ProcessSnapshot etlProcess, ProcessSnapshot tpProcess) in matched)
        {
            (IReadOnlyList<string> fields, IReadOnlyList<string> anomalyFields) =
                GetProcessDifferences(etlProcess, tpProcess, traceStartTime, traceEndTime);
            if (fields.Count > 0)
            {
                differences.Add((etlProcess, tpProcess, fields));
                foreach (string field in fields.Distinct(StringComparer.Ordinal))
                {
                    mismatchCounts[field] = mismatchCounts.GetValueOrDefault(field) + 1;
                }
            }

            if (anomalyFields.Count > 0)
            {
                traceProcessorAnomalies.Add((etlProcess, tpProcess, anomalyFields));
                foreach (string field in anomalyFields.Distinct(StringComparer.Ordinal))
                {
                    anomalyCounts[field] = anomalyCounts.GetValueOrDefault(field) + 1;
                }
            }
        }

        Console.WriteLine($"[UseProcesses] EtlFileReader={etl.Count} 筆, TraceProcessor={tp.Count} 筆, " +
            $"依 PID 分組、ObjectAddress 優先且生命週期排序備援配對成功={matched.Count} 筆" +
            $"(PID 筆數不同的群組={pidCountMismatch}, " +
            $"僅 EtlFileReader 多出={etlOnly.Count} 筆, 僅 TraceProcessor 多出={tpOnly.Count} 筆), " +
            $"實質內容不同={differences.Count} 筆, TraceProcessor 異常值={traceProcessorAnomalies.Count} 筆");
        Console.WriteLine("  全部可直接比較的 IProcess 屬性差異：" +
            (mismatchCounts.Count == 0
                ? "無"
                : string.Join(", ", mismatchCounts
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}"))));
        Console.WriteLine("  TraceProcessor 內部不一致或超出 ETL 時間範圍的值：" +
            (anomalyCounts.Count == 0
                ? "無"
                : string.Join(", ", anomalyCounts
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}"))));

        PrintProcessSample("僅 EtlFileReader 有", etlOnly);
        PrintProcessSample("僅 TraceProcessor 有", tpOnly);
        foreach ((ProcessSnapshot etlProcess, ProcessSnapshot tpProcess) in matched
                     .Where(pair => pair.Etl.Images.Count != pair.Tp.Images.Count)
                     .Take(MaxDifferenceSamplesToPrint))
        {
            Console.WriteLine(
                $"  Images.Count 不同：PID={etlProcess.ProcessId}, " +
                $"EtlFileReader={etlProcess.Images.Count}, TraceProcessor={tpProcess.Images.Count}");
            foreach (ProcessImageSnapshot image in GetImagesOnlyInSecond(
                         etlProcess.Images,
                         tpProcess.Images).Take(3))
            {
                Console.WriteLine($"    僅 TraceProcessor 有：{FormatImage(image)}");
            }
        }

        foreach ((ProcessSnapshot etlProcess, ProcessSnapshot tpProcess, IReadOnlyList<string> fields) in
                 differences.Take(MaxDifferenceSamplesToPrint))
        {
            Console.WriteLine(
                $"  PID={etlProcess.ProcessId}, ObjectAddress=0x{etlProcess.ObjectAddress:X}: " +
                $"不同欄位={string.Join(", ", fields)}");
            Console.WriteLine($"    EtlFileReader: {FormatProcess(etlProcess)}");
            Console.WriteLine($"    TraceProcessor: {FormatProcess(tpProcess)}");
            PrintFirstImageDifference(
                etlProcess.Images,
                tpProcess.Images,
                traceStartTime,
                traceEndTime,
                printAnomalies: false);
        }

        if (differences.Count > MaxDifferenceSamplesToPrint)
        {
            Console.WriteLine($"  ...其餘 {differences.Count - MaxDifferenceSamplesToPrint} 筆不同省略");
        }

        foreach ((ProcessSnapshot etlProcess, ProcessSnapshot tpProcess, IReadOnlyList<string> fields) in
                 traceProcessorAnomalies.Take(MaxDifferenceSamplesToPrint))
        {
            Console.WriteLine(
                $"  TraceProcessor 異常：PID={etlProcess.ProcessId}, " +
                $"欄位={string.Join(", ", fields)}");
            PrintFirstImageDifference(
                etlProcess.Images,
                tpProcess.Images,
                traceStartTime,
                traceEndTime,
                printAnomalies: true);
        }

        int createStackCount = tp.Count(process => process.CreateStack is not null);
        int exitStackCount = tp.Count(process => process.ExitStack is not null);
        int packageFriendlyNameCount = tp.Count(process =>
            !string.IsNullOrEmpty(process.Package?.FriendlyName));
        Console.WriteLine(
            "  TraceProcessor-only 衍生資料（EtlFileReader 尚無對應來源；僅在兩邊都有值時比較內容）：" +
            $"CreateStack={createStackCount}, ExitStack={exitStackCount}, " +
            $"Package.FriendlyName={packageFriendlyNameCount}");
        Console.WriteLine(
            "  導覽屬性可用數：" +
            $"Parent EtlFileReader={etlSnapshots.Count(process => process.ResolvedParentProcessId is not null)}, " +
            $"TraceProcessor={tp.Count(process => process.ResolvedParentProcessId is not null)}");
    }

    private static (
        List<(ProcessSnapshot Etl, ProcessSnapshot Tp)> Matched,
        int PidCountMismatch,
        List<ProcessSnapshot> EtlOnly,
        List<ProcessSnapshot> TpOnly)
        MatchProcessesById(
            IReadOnlyList<ProcessSnapshot> etl,
            IReadOnlyList<ProcessSnapshot> tp)
    {
        Dictionary<long, List<ProcessSnapshot>> etlByPid = etl
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.ToList());
        Dictionary<long, List<ProcessSnapshot>> tpByPid = tp
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.ToList());

        List<(ProcessSnapshot, ProcessSnapshot)> matched = [];
        List<ProcessSnapshot> etlOnly = [];
        List<ProcessSnapshot> tpOnly = [];
        int pidCountMismatch = 0;

        foreach (long processId in etlByPid.Keys.Union(tpByPid.Keys))
        {
            List<ProcessSnapshot> etlGroup = etlByPid.GetValueOrDefault(processId) ?? [];
            List<ProcessSnapshot> unmatchedTp = [.. tpByPid.GetValueOrDefault(processId) ?? []];
            if (etlGroup.Count != unmatchedTp.Count)
            {
                pidCountMismatch++;
            }

            List<ProcessSnapshot> unmatchedEtl = [];
            foreach (ProcessSnapshot etlProcess in etlGroup)
            {
                int matchIndex = etlProcess.ObjectAddress == 0
                    ? -1
                    : unmatchedTp.FindIndex(tpProcess =>
                        tpProcess.ObjectAddress == etlProcess.ObjectAddress);
                if (matchIndex >= 0)
                {
                    matched.Add((etlProcess, unmatchedTp[matchIndex]));
                    unmatchedTp.RemoveAt(matchIndex);
                }
                else
                {
                    unmatchedEtl.Add(etlProcess);
                }
            }

            List<ProcessSnapshot> orderedEtl = [.. OrderProcesses(unmatchedEtl)];
            List<ProcessSnapshot> orderedTp = [.. OrderProcesses(unmatchedTp)];
            int pairCount = Math.Min(orderedEtl.Count, orderedTp.Count);
            for (int index = 0; index < pairCount; index++)
            {
                matched.Add((orderedEtl[index], orderedTp[index]));
            }

            etlOnly.AddRange(orderedEtl.Skip(pairCount));
            tpOnly.AddRange(orderedTp.Skip(pairCount));
        }

        return (matched, pidCountMismatch, etlOnly, tpOnly);
    }

    private static IOrderedEnumerable<ProcessSnapshot> OrderProcesses(IEnumerable<ProcessSnapshot> processes) =>
        processes
            .OrderBy(process => process.CreateTime ?? DateTime.MinValue)
            .ThenBy(process => process.ExitTime ?? DateTime.MaxValue)
            .ThenBy(process => process.ObjectAddress);

    private static (IReadOnlyList<string> Differences, IReadOnlyList<string> TraceProcessorAnomalies)
        GetProcessDifferences(
            ProcessSnapshot etl,
            ProcessSnapshot tp,
            DateTime? traceStartTime,
            DateTime? traceEndTime)
    {
        List<string> fields = [];
        AddIfDifferent(fields, "Id", etl.ProcessId != tp.ProcessId);
        AddIfDifferent(fields, "ParentId", etl.ParentProcessId != tp.ParentProcessId);
        AddIfDifferent(
            fields,
            "Parent",
            etl.ResolvedParentProcessId is not null &&
            tp.ResolvedParentProcessId is not null &&
            etl.ResolvedParentProcessId != tp.ResolvedParentProcessId);
        AddIfDifferent(
            fields,
            "Parent.ObjectAddress",
            etl.ParentObjectAddress is not null &&
            tp.ParentObjectAddress is not null &&
            etl.ParentObjectAddress != tp.ParentObjectAddress);
        AddIfDifferent(fields, "SessionId", etl.SessionId != tp.SessionId);
        AddIfDifferent(fields, "ObjectAddress", etl.ObjectAddress != tp.ObjectAddress);
        AddIfDifferent(fields, "ImageName", !StringEquals(etl.ImageFileName, tp.ImageFileName));
        AddIfDifferent(fields, "CreateTime", !TimestampEquals(etl.CreateTime, tp.CreateTime));
        AddIfDifferent(fields, "ExitTime", !TimestampEquals(etl.ExitTime, tp.ExitTime));
        AddIfDifferent(
            fields,
            "Duration",
            !NullableDoubleEquals(etl.DurationMilliseconds, tp.DurationMilliseconds));
        AddIfDifferent(fields, "ExitCode", etl.ExitCode != tp.ExitCode);
        AddIfDifferent(fields, "User", !StringEquals(etl.UserSid, tp.UserSid));
        AddIfDifferent(
            fields,
            "CommandLine",
            !string.Equals(etl.CommandLine, tp.CommandLine, StringComparison.Ordinal));
        AddIfDifferent(
            fields,
            "Package",
            !PackageEquals(etl.Package, tp.Package, compareFriendlyName: false));
        AddIfDifferent(
            fields,
            "Package.FriendlyName",
            etl.Package?.FriendlyName is not null &&
            tp.Package?.FriendlyName is not null &&
            !string.Equals(
                etl.Package?.FriendlyName,
                tp.Package?.FriendlyName,
                StringComparison.Ordinal));
        AddIfDifferent(
            fields,
            "CreateStack",
            etl.CreateStack is not null &&
            tp.CreateStack is not null &&
            !StackEquals(etl.CreateStack, tp.CreateStack));
        AddIfDifferent(
            fields,
            "ExitStack",
            etl.ExitStack is not null &&
            tp.ExitStack is not null &&
            !StackEquals(etl.ExitStack, tp.ExitStack));
        (IReadOnlyList<string> imageDifferences, IReadOnlyList<string> imageAnomalies) =
            GetImageDifferences(etl.Images, tp.Images, traceStartTime, traceEndTime);
        fields.AddRange(imageDifferences);
        return (fields, imageAnomalies);
    }

    private static (IReadOnlyList<string> Differences, IReadOnlyList<string> TraceProcessorAnomalies)
        GetImageDifferences(
        IReadOnlyList<ProcessImageSnapshot> etl,
        IReadOnlyList<ProcessImageSnapshot> tp,
        DateTime? traceStartTime,
        DateTime? traceEndTime)
    {
        List<string> fields = [];
        List<string> anomalies = [];
        AddIfDifferent(fields, "Images.Count", etl.Count != tp.Count);

        IReadOnlyList<(ProcessImageSnapshot Etl, ProcessImageSnapshot Tp)> matched = MatchImages(etl, tp);
        AddIfDifferent(fields, "Images.Identity", matched.Count != Math.Min(etl.Count, tp.Count));
        foreach ((ProcessImageSnapshot e, ProcessImageSnapshot t) in matched)
        {
            foreach (string field in GetImagePairDifferences(e, t))
            {
                if (IsTraceProcessorImageAnomaly(field, e, t, traceStartTime, traceEndTime))
                {
                    anomalies.Add(field);
                }
                else
                {
                    fields.Add(field);
                }
            }
        }

        return (
            [.. fields.Distinct(StringComparer.Ordinal)],
            [.. anomalies.Distinct(StringComparer.Ordinal)]);
    }

    private static IReadOnlyList<(ProcessImageSnapshot Etl, ProcessImageSnapshot Tp)> MatchImages(
        IReadOnlyList<ProcessImageSnapshot> etl,
        IReadOnlyList<ProcessImageSnapshot> tp)
    {
        List<(ProcessImageSnapshot, ProcessImageSnapshot)> matched = [];
        List<ProcessImageSnapshot> unmatchedTp = [.. tp];

        foreach (ProcessImageSnapshot etlImage in etl)
        {
            int matchIndex = unmatchedTp.FindIndex(tpImage =>
                etlImage.ImageBase == tpImage.ImageBase &&
                etlImage.ImageSize == tpImage.ImageSize &&
                StringEquals(etlImage.Path, tpImage.Path) &&
                TimestampEquals(etlImage.LoadTime, tpImage.LoadTime) &&
                TimestampEquals(etlImage.UnloadTime, tpImage.UnloadTime));
            if (matchIndex < 0)
            {
                matchIndex = unmatchedTp.FindIndex(tpImage =>
                    etlImage.ImageBase == tpImage.ImageBase &&
                    StringEquals(etlImage.Path, tpImage.Path));
            }
            if (matchIndex < 0)
            {
                matchIndex = unmatchedTp.FindIndex(tpImage =>
                    etlImage.ImageBase == tpImage.ImageBase);
            }
            if (matchIndex < 0)
            {
                continue;
            }

            matched.Add((etlImage, unmatchedTp[matchIndex]));
            unmatchedTp.RemoveAt(matchIndex);
        }

        return matched;
    }

    private static IReadOnlyList<ProcessImageSnapshot> GetImagesOnlyInSecond(
        IReadOnlyList<ProcessImageSnapshot> first,
        IReadOnlyList<ProcessImageSnapshot> second)
    {
        List<ProcessImageSnapshot> unmatched = [.. second];
        foreach (ProcessImageSnapshot image in first)
        {
            int matchIndex = unmatched.FindIndex(candidate =>
                candidate.ImageBase == image.ImageBase &&
                candidate.ImageSize == image.ImageSize &&
                StringEquals(candidate.Path, image.Path));
            if (matchIndex >= 0)
            {
                unmatched.RemoveAt(matchIndex);
            }
        }

        return unmatched;
    }

    private static IReadOnlyList<string> GetImagePairDifferences(
        ProcessImageSnapshot etl,
        ProcessImageSnapshot tp)
    {
        List<string> fields = [];
        AddIfDifferent(fields, "Images.ProcessId", etl.ProcessId != tp.ProcessId);
        AddIfDifferent(fields, "Images.FileName", !StringEquals(etl.FileName, tp.FileName));
        AddIfDifferent(fields, "Images.Path", !StringEquals(etl.Path, tp.Path));
        AddIfDifferent(fields, "Images.AddressRange.BaseAddress", etl.ImageBase != tp.ImageBase);
        AddIfDifferent(fields, "Images.AddressRange.LimitAddress", etl.ImageLimit != tp.ImageLimit);
        AddIfDifferent(fields, "Images.Size", etl.ImageSize != tp.ImageSize);
        AddIfDifferent(fields, "Images.DefaultBaseAddress", etl.DefaultBase != tp.DefaultBase);
        AddIfDifferent(fields, "Images.Checksum", etl.Checksum != tp.Checksum);
        AddIfDifferent(fields, "Images.Timestamp", etl.Timestamp != tp.Timestamp);
        AddIfDifferent(fields, "Images.LoadTime", !TimestampEquals(etl.LoadTime, tp.LoadTime));
        AddIfDifferent(fields, "Images.UnloadTime", !TimestampEquals(etl.UnloadTime, tp.UnloadTime));
        AddIfDifferent(
            fields,
            "Images.LoadedDuration",
            !NullableDoubleEquals(etl.LoadedDurationMilliseconds, tp.LoadedDurationMilliseconds));
        AddIfDifferent(fields, "Images.LoadingThreadId", etl.LoadingThreadId != tp.LoadingThreadId);
        AddIfDifferent(fields, "Images.UnloadingThreadId", etl.UnloadingThreadId != tp.UnloadingThreadId);
        return fields;
    }

    private static bool IsTraceProcessorImageAnomaly(
        string field,
        ProcessImageSnapshot etl,
        ProcessImageSnapshot tp,
        DateTime? traceStartTime,
        DateTime? traceEndTime)
    {
        bool unloadTimeOutsideTrace =
            tp.UnloadTime is DateTime unloadTime &&
            ((traceStartTime is DateTime start && unloadTime < start.ToUniversalTime()) ||
             (traceEndTime is DateTime end && unloadTime > end.ToUniversalTime()));

        return field switch
        {
            "Images.UnloadTime" or "Images.LoadedDuration" => unloadTimeOutsideTrace,
            "Images.UnloadingThreadId" =>
                etl.UnloadingThreadId is null &&
                tp.UnloadingThreadId is not null &&
                tp.UnloadTime is null,
            _ => false,
        };
    }

    private static bool PackageEquals(
        ProcessPackageSnapshot? etl,
        ProcessPackageSnapshot? tp,
        bool compareFriendlyName)
    {
        if (etl is null || tp is null)
        {
            return etl is null && tp is null;
        }

        return StringEquals(etl.FullName, tp.FullName) &&
            StringEquals(etl.RelativeApplicationId, tp.RelativeApplicationId) &&
            (!compareFriendlyName || string.Equals(
                etl.FriendlyName,
                tp.FriendlyName,
                StringComparison.Ordinal));
    }

    private static bool StackEquals(ProcessStackSnapshot? etl, ProcessStackSnapshot? tp)
    {
        if (etl is null || tp is null)
        {
            return etl is null && tp is null;
        }

        return TimestampEquals(etl.Timestamp, tp.Timestamp) &&
            etl.ProcessId == tp.ProcessId &&
            etl.ThreadId == tp.ThreadId &&
            etl.Processor == tp.Processor &&
            etl.IsIdle == tp.IsIdle &&
            etl.FrameAddresses.SequenceEqual(tp.FrameAddresses);
    }

    private static bool TimestampEquals(DateTime? etl, DateTime? tp)
    {
        if (etl.HasValue != tp.HasValue)
        {
            return false;
        }

        return !etl.HasValue ||
            Math.Abs((etl.Value - tp!.Value).TotalMilliseconds) <=
            ProcessTimestampToleranceMilliseconds;
    }

    private static bool NullableDoubleEquals(double? etl, double? tp)
    {
        if (etl.HasValue != tp.HasValue)
        {
            return false;
        }

        return !etl.HasValue ||
            Math.Abs(etl.Value - tp!.Value) <= ProcessTimestampToleranceMilliseconds;
    }

    private static bool StringEquals(string? etl, string? tp) =>
        string.Equals(etl ?? string.Empty, tp ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static void AddIfDifferent(List<string> fields, string field, bool different)
    {
        if (different)
        {
            fields.Add(field);
        }
    }

    private static void PrintProcessSample(string label, IReadOnlyList<ProcessSnapshot> processes)
    {
        if (processes.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  {label}（共 {processes.Count} 筆，僅列前 5 筆）：");
        foreach (ProcessSnapshot process in processes.Take(5))
        {
            Console.WriteLine($"    {FormatProcess(process)}");
        }
    }

    private static void PrintFirstImageDifference(
        IReadOnlyList<ProcessImageSnapshot> etl,
        IReadOnlyList<ProcessImageSnapshot> tp,
        DateTime? traceStartTime,
        DateTime? traceEndTime,
        bool printAnomalies)
    {
        foreach ((ProcessImageSnapshot e, ProcessImageSnapshot t) in MatchImages(etl, tp))
        {
            IReadOnlyList<string> fields = [.. GetImagePairDifferences(e, t).Where(field =>
                IsTraceProcessorImageAnomaly(field, e, t, traceStartTime, traceEndTime) == printAnomalies)];
            if (fields.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"    第一筆映像差異欄位={string.Join(", ", fields)}");
            Console.WriteLine($"      EtlFileReader: {FormatImage(e)}");
            Console.WriteLine($"      TraceProcessor: {FormatImage(t)}");
            return;
        }
    }

    private static string FormatProcess(ProcessSnapshot process) =>
        $"PID={process.ProcessId}, ParentId={process.ParentProcessId}, SessionId={process.SessionId}, " +
        $"ObjectAddress=0x{process.ObjectAddress:X}, ImageName=\"{process.ImageFileName}\", " +
        $"ResolvedParent={process.ResolvedParentProcessId?.ToString() ?? "<null>"}" +
        $"@0x{process.ParentObjectAddress:X}, " +
        $"CreateTime={FormatTimestamp(process.CreateTime)}, ExitTime={FormatTimestamp(process.ExitTime)}, " +
        $"DurationMS={process.DurationMilliseconds?.ToString("F6") ?? "<null>"}, " +
        $"ExitCode={process.ExitCode?.ToString() ?? "<null>"}, User=\"{process.UserSid}\", " +
        $"CommandLine=\"{process.CommandLine}\", Package=\"{process.Package?.FullName ?? "<null>"}\", " +
        $"Images={process.Images.Count}, CreateStackFrames={process.CreateStack?.FrameAddresses.Count ?? 0}, " +
        $"ExitStackFrames={process.ExitStack?.FrameAddresses.Count ?? 0}";

    private static string FormatTimestamp(DateTime? timestamp) =>
        timestamp?.ToString("O") ?? "<null>";

    private static string FormatImage(ProcessImageSnapshot image) =>
        $"\"{image.Path}\", Base=0x{image.ImageBase:X}, Limit=0x{image.ImageLimit:X}, " +
        $"Size=0x{image.ImageSize:X}, DefaultBase=0x{image.DefaultBase:X}, " +
        $"Checksum=0x{image.Checksum:X}, Timestamp=0x{image.Timestamp:X}, " +
        $"LoadTime={FormatTimestamp(image.LoadTime)}, UnloadTime={FormatTimestamp(image.UnloadTime)}, " +
        $"LoadingTID={image.LoadingThreadId?.ToString() ?? "<null>"}, " +
        $"UnloadingTID={image.UnloadingThreadId?.ToString() ?? "<null>"}";

    private static void ValidateServices(ServiceDataSnapshot etl, ServiceDataSnapshot tp)
    {
        Dictionary<string, ServiceObjectSnapshot> etlByName =
            ToLookup(etl.Services, service => service.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ServiceObjectSnapshot> tpByName =
            ToLookup(tp.Services, service => service.Name, StringComparer.OrdinalIgnoreCase);
        List<string> onlyInEtl =
            [.. etlByName.Keys.Except(tpByName.Keys, StringComparer.OrdinalIgnoreCase)];
        List<string> onlyInTp =
            [.. tpByName.Keys.Except(etlByName.Keys, StringComparer.OrdinalIgnoreCase)];
        List<string> common =
            [.. etlByName.Keys.Intersect(tpByName.Keys, StringComparer.OrdinalIgnoreCase)];

        Dictionary<string, int> mismatchesByArea = new(StringComparer.Ordinal);
        List<string> details = [];

        void Compare<T>(string area, string path, T etlValue, T tpValue)
        {
            if (EqualityComparer<T>.Default.Equals(etlValue, tpValue))
            {
                return;
            }

            mismatchesByArea[area] = mismatchesByArea.GetValueOrDefault(area) + 1;
            if (details.Count < 20)
            {
                details.Add($"{path}: Etl={etlValue?.ToString() ?? "<null>"}, TP={tpValue?.ToString() ?? "<null>"}");
            }
        }

        foreach (string name in common)
        {
            ServiceObjectSnapshot left = etlByName[name];
            ServiceObjectSnapshot right = tpByName[name];
            string servicePath = $"Service[{name}]";
            Compare("IService", $"{servicePath}.Name", left.Name, right.Name);
            Compare("IService", $"{servicePath}.StartType", left.StartType, right.StartType);
            Compare("IService", $"{servicePath}.StartTypeUnavailable", left.StartTypeUnavailable, right.StartTypeUnavailable);

            Compare("IServiceGroup", $"{servicePath}.Group null", left.Group is null, right.Group is null);
            if (left.Group is not null && right.Group is not null)
            {
                Compare("IServiceGroup", $"{servicePath}.Group.Name", left.Group.Name, right.Group.Name);
                Compare("IServiceGroup", $"{servicePath}.Group.ThreadId", left.Group.ThreadId, right.Group.ThreadId);
                Compare("IServiceGroup", $"{servicePath}.Group.StartTime", left.Group.StartTime, right.Group.StartTime);
                Compare("IServiceGroup", $"{servicePath}.Group.StopTime", left.Group.StopTime, right.Group.StopTime);
                Compare("IServiceGroup", $"{servicePath}.Group.Duration", left.Group.DurationTicks, right.Group.DurationTicks);
            }

            Compare("IServiceStateChange", $"{servicePath}.StateChanges.Count", left.StateChanges.Count, right.StateChanges.Count);
            int stateCount = Math.Min(left.StateChanges.Count, right.StateChanges.Count);
            for (int i = 0; i < stateCount; i++)
            {
                ServiceStateChangeSnapshot leftState = left.StateChanges[i];
                ServiceStateChangeSnapshot rightState = right.StateChanges[i];
                string statePath = $"{servicePath}.StateChanges[{i}]";
                Compare("IServiceStateChange", $"{statePath}.Timestamp", leftState.Timestamp, rightState.Timestamp);
                Compare("IServiceStateChange", $"{statePath}.State", leftState.State, rightState.State);
                Compare("IServiceStateChange", $"{statePath}.ProcessId", leftState.ProcessId, rightState.ProcessId);
                Compare("IServiceStateChange", $"{statePath}.ImageName", leftState.ImageName, rightState.ImageName);
                Compare("IServiceStateChange", $"{statePath}.ServiceManagementThreadId", leftState.ServiceManagementThreadId, rightState.ServiceManagementThreadId);
                Compare("IServiceStateChange", $"{statePath}.Process.Id", leftState.ResolvedProcessId, rightState.ResolvedProcessId);
                Compare("IServiceStateChange", $"{statePath}.Process.ObjectAddress", leftState.ResolvedProcessObjectAddress, rightState.ResolvedProcessObjectAddress);
                Compare("IServiceStateChange", $"{statePath}.ServiceManagementThread.Id", leftState.ResolvedServiceManagementThreadId, rightState.ResolvedServiceManagementThreadId);
                Compare("IServiceStateChange", $"{statePath}.ServiceManagementThread.ProcessId", leftState.ResolvedServiceManagementThreadProcessId, rightState.ResolvedServiceManagementThreadProcessId);
                Compare("IServiceStateChange", $"{statePath}.ServiceManagementThread.CreateTime", leftState.ResolvedServiceManagementThreadCreateTime, rightState.ResolvedServiceManagementThreadCreateTime);
                Compare("IServiceStateChange", $"{statePath}.ServiceManagementProcess.Id", leftState.ResolvedServiceManagementProcessId, rightState.ResolvedServiceManagementProcessId);
                Compare("IServiceStateChange", $"{statePath}.ServiceManagementProcess.ObjectAddress", leftState.ResolvedServiceManagementProcessObjectAddress, rightState.ResolvedServiceManagementProcessObjectAddress);
            }

            Compare("IServiceTransition", $"{servicePath}.Transitions.Count", left.Transitions.Count, right.Transitions.Count);
            int transitionCount = Math.Min(left.Transitions.Count, right.Transitions.Count);
            for (int i = 0; i < transitionCount; i++)
            {
                ServiceTransitionSnapshot leftTransition = left.Transitions[i];
                ServiceTransitionSnapshot rightTransition = right.Transitions[i];
                string transitionPath = $"{servicePath}.Transitions[{i}]";
                Compare("IServiceTransition", $"{transitionPath}.TransitionType", leftTransition.TransitionType, rightTransition.TransitionType);
                Compare("IServiceTransition", $"{transitionPath}.StartEvent", leftTransition.StartEventIndex, rightTransition.StartEventIndex);
                Compare("IServiceTransition", $"{transitionPath}.EndEvent", leftTransition.EndEventIndex, rightTransition.EndEventIndex);
                Compare(
                    "IServiceTransition",
                    $"{transitionPath}.Checkpoints",
                    string.Join(",", leftTransition.CheckpointIndices),
                    string.Join(",", rightTransition.CheckpointIndices));
                Compare("IServiceTransition", $"{transitionPath}.ServiceMainCallTimeRange.StartTime", leftTransition.ServiceMainCallStartTime, rightTransition.ServiceMainCallStartTime);
                Compare("IServiceTransition", $"{transitionPath}.ServiceMainCallTimeRange.EndTime", leftTransition.ServiceMainCallEndTime, rightTransition.ServiceMainCallEndTime);
                Compare("IServiceTransition", $"{transitionPath}.ServiceMainCallTimeRange.Duration", leftTransition.ServiceMainCallDurationTicks, rightTransition.ServiceMainCallDurationTicks);
            }
        }

        Dictionary<string, ServiceConfigurationSnapshot> etlSnapshots =
            ToLookup(etl.Snapshots, snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ServiceConfigurationSnapshot> tpSnapshots =
            ToLookup(tp.Snapshots, snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase);
        List<string> snapshotOnlyInEtl =
            [.. etlSnapshots.Keys.Except(tpSnapshots.Keys, StringComparer.OrdinalIgnoreCase)];
        List<string> snapshotOnlyInTp =
            [.. tpSnapshots.Keys.Except(etlSnapshots.Keys, StringComparer.OrdinalIgnoreCase)];
        foreach (string name in etlSnapshots.Keys.Intersect(tpSnapshots.Keys, StringComparer.OrdinalIgnoreCase))
        {
            ServiceConfigurationSnapshot left = etlSnapshots[name];
            ServiceConfigurationSnapshot right = tpSnapshots[name];
            string path = $"Snapshot[{name}]";
            Compare("IServiceSnapshot", $"{path}.Id", left.Id, right.Id);
            Compare("IServiceSnapshot", $"{path}.Name", left.Name, right.Name);
            Compare("IServiceSnapshot", $"{path}.DisplayName", left.DisplayName, right.DisplayName);
            Compare("IServiceSnapshot", $"{path}.GroupName", left.GroupName, right.GroupName);
            Compare("IServiceSnapshot", $"{path}.ProcessId", left.ProcessId, right.ProcessId);
            Compare("IServiceSnapshot", $"{path}.ProcessName", left.ProcessName, right.ProcessName);
            Compare("IServiceSnapshot", $"{path}.State", left.State, right.State);
        }

        int totalMismatches = mismatchesByArea.Values.Sum();
        Console.WriteLine(
            $"[UseServices] Services: EtlFileReader={etl.Services.Count}, TraceProcessor={tp.Services.Count}, " +
            $"共同={common.Count}, 僅 EtlFileReader={onlyInEtl.Count}, 僅 TraceProcessor={onlyInTp.Count}; " +
            $"Snapshots: EtlFileReader={etl.Snapshots.Count}, TraceProcessor={tp.Snapshots.Count}, " +
            $"僅 EtlFileReader={snapshotOnlyInEtl.Count}, 僅 TraceProcessor={snapshotOnlyInTp.Count}; " +
            $"全部欄位差異={totalMismatches}");
        foreach ((string area, int count) in mismatchesByArea)
        {
            Console.WriteLine($"  {area}: {count} 個欄位差異");
        }
        foreach (string detail in details)
        {
            Console.WriteLine($"  - {detail}");
        }
        PrintSample("僅 EtlFileReader 有的服務", onlyInEtl);
        PrintSample("僅 TraceProcessor 有的服務", onlyInTp);
        PrintSample("僅 EtlFileReader 有的服務快照", snapshotOnlyInEtl);
        PrintSample("僅 TraceProcessor 有的服務快照", snapshotOnlyInTp);
    }

    private const double TimestampToleranceSeconds = 1.0;

    private static void ValidateThreads(IReadOnlyList<ThreadSnapshot> etl, IReadOnlyList<ThreadSnapshot> tp)
    {
        Dictionary<long, ThreadSnapshot> etlByTid = ToLookup(etl, t => t.ThreadId);
        Dictionary<long, ThreadSnapshot> tpByTid = ToLookup(tp, t => t.ThreadId);

        List<long> onlyInEtl = [.. etlByTid.Keys.Except(tpByTid.Keys)];
        List<long> onlyInTp = [.. tpByTid.Keys.Except(etlByTid.Keys)];
        List<long> common = [.. etlByTid.Keys.Intersect(tpByTid.Keys)];

        int processIdMismatch = common.Count(tid => etlByTid[tid].ProcessId != tpByTid[tid].ProcessId);
        int nameMismatch = common.Count(tid => !string.Equals(etlByTid[tid].Name, tpByTid[tid].Name, StringComparison.Ordinal));
        int basePriorityMismatch = common.Count(tid => etlByTid[tid].BasePriority != tpByTid[tid].BasePriority);
        int pagePriorityMismatch = common.Count(tid => etlByTid[tid].PagePriority != tpByTid[tid].PagePriority);
        int ioPriorityMismatch = common.Count(tid => etlByTid[tid].IoPriority != tpByTid[tid].IoPriority);
        int affinityMismatch = common.Count(tid => etlByTid[tid].ProcessorAffinity != tpByTid[tid].ProcessorAffinity);
        int stackBaseMismatch = common.Count(tid => etlByTid[tid].StackBase != tpByTid[tid].StackBase);
        int stackLimitMismatch = common.Count(tid => etlByTid[tid].StackLimit != tpByTid[tid].StackLimit);
        int userStackBaseMismatch = common.Count(tid => etlByTid[tid].UserStackBase != tpByTid[tid].UserStackBase);
        int userStackLimitMismatch = common.Count(tid => etlByTid[tid].UserStackLimit != tpByTid[tid].UserStackLimit);
        int startAddressMismatch = common.Count(tid => etlByTid[tid].StartAddress != tpByTid[tid].StartAddress);
        int tebBaseMismatch = common.Count(tid => etlByTid[tid].TebBase != tpByTid[tid].TebBase);
        int serviceIdMismatch = common.Count(tid => etlByTid[tid].ServiceId != tpByTid[tid].ServiceId);
        int startAddressModuleMismatch = common.Count(tid =>
            !string.Equals(etlByTid[tid].StartAddressModuleName, tpByTid[tid].StartAddressModuleName, StringComparison.OrdinalIgnoreCase));

        (int createTimeMismatch, double createTimeMaxDelta) = CompareTimestamps(common, etlByTid, tpByTid, s => s.CreateTime);
        (int exitTimeMismatch, double exitTimeMaxDelta) = CompareTimestamps(common, etlByTid, tpByTid, s => s.ExitTime);

        Console.WriteLine($"[UseThreads] EtlFileReader={etl.Count} 筆, TraceProcessor={tp.Count} 筆, 共同 TID={common.Count} 筆, " +
            $"僅 EtlFileReader 有={onlyInEtl.Count} 筆, 僅 TraceProcessor 有={onlyInTp.Count} 筆, 所屬 PID 不同={processIdMismatch} 筆");
        Console.WriteLine($"  Name 不同={nameMismatch} 筆" +
            "（取自 Start/DCStart payload 尾端字串，並疊加事後 SetThreadDescription() 的 ThreadSetName 事件）");
        Console.WriteLine($"  CreateTime 不同(容許誤差 {TimestampToleranceSeconds:F0} 秒)={createTimeMismatch} 筆, 實際最大誤差={createTimeMaxDelta:F3} 秒");
        Console.WriteLine($"  ExitTime 不同(容許誤差 {TimestampToleranceSeconds:F0} 秒)={exitTimeMismatch} 筆, 實際最大誤差={exitTimeMaxDelta:F3} 秒");
        Console.WriteLine($"  BasePriority 不同={basePriorityMismatch} 筆, PagePriority 不同={pagePriorityMismatch} 筆, IoPriority 不同={ioPriorityMismatch} 筆, ProcessorAffinity 不同={affinityMismatch} 筆");
        Console.WriteLine($"  StackBase 不同={stackBaseMismatch} 筆, StackLimit 不同={stackLimitMismatch} 筆, UserStackBase 不同={userStackBaseMismatch} 筆, UserStackLimit 不同={userStackLimitMismatch} 筆");
        Console.WriteLine($"  StartAddress 不同={startAddressMismatch} 筆, TebBase 不同={tebBaseMismatch} 筆, ServiceId 不同={serviceIdMismatch} 筆, StartAddress 所屬模組不同={startAddressModuleMismatch} 筆");
        PrintSample("僅 EtlFileReader 有的 TID", onlyInEtl);
        PrintSample("僅 TraceProcessor 有的 TID", onlyInTp);
    }

    private static (int Mismatch, double MaxDeltaSeconds) CompareTimestamps<TKey, TSnapshot>(
        IReadOnlyList<TKey> commonKeys,
        Dictionary<TKey, TSnapshot> etlByKey,
        Dictionary<TKey, TSnapshot> tpByKey,
        Func<TSnapshot, DateTime?> selector)
        where TKey : notnull
    {
        int mismatch = 0;
        double maxDelta = 0;
        foreach (TKey key in commonKeys)
        {
            DateTime? a = selector(etlByKey[key]);
            DateTime? b = selector(tpByKey[key]);
            if (a.HasValue != b.HasValue)
            {
                mismatch++;
                continue;
            }
            if (a.HasValue && b.HasValue)
            {
                double delta = Math.Abs((a.Value.ToUniversalTime() - b.Value.ToUniversalTime()).TotalSeconds);
                maxDelta = Math.Max(maxDelta, delta);
                if (delta > TimestampToleranceSeconds)
                {
                    mismatch++;
                }
            }
        }
        return (mismatch, maxDelta);
    }

    /// <summary>DPC/ISR 時間戳容許誤差(毫秒)。兩邊都由相同的原始 ETW clock timestamp 換算，
    /// 理論上應只相差在換算捨入誤差範圍內。</summary>
    private const double InterruptTimestampToleranceMilliseconds = 0.01;

    /// <summary>內容不同的紀錄最多列出幾筆，避免筆數龐大時洗版。</summary>
    private const int MaxDifferenceSamplesToPrint = 10;

    private static DpcSnapshot ToDpcSnapshot(DeferredProcedureCallInterval i) => new(
        i.Processor,
        i.StartTime.ToUniversalTime(),
        i.StopTime.ToUniversalTime(),
        i.HandlerRoutineAddress.HasValue ? unchecked((long)i.HandlerRoutineAddress.Value) : 0L,
        NormalizeModuleName(i.HandlerModuleName),
        i.HandlerStackFrame?.RelativeVirtualAddress is ulong rva ? unchecked((long)rva) : null,
        i.HandlerStackFrame?.SymbolName,
        (i.CallType == EtlDeferredProcedureCallType.ThreadedDpc ? EtlDeferredProcedureCallType.Normal : i.CallType).ToString());

    internal static DpcSnapshot ToDpcSnapshot(IDeferredProcedureCallInterval i) => new(
        i.Processor,
        i.StartTime.DateTimeOffset.UtcDateTime,
        i.StopTime.DateTimeOffset.UtcDateTime,
        i.HandlerRoutineAddress.Value,
        NormalizeModuleName(i.HandlerImage?.FileName),
        i.HandlerStackFrame.HasValue ? i.HandlerStackFrame.RelativeVirtualAddress.Value : null,
        i.HandlerStackFrame.Symbol?.FunctionName,
        i.CallType.ToString());

    private static IsrSnapshot ToIsrSnapshot(InterruptServiceRoutineInterval i) => new(
        i.Processor,
        i.StartTime.ToUniversalTime(),
        i.StopTime.ToUniversalTime(),
        i.HandlerRoutineAddress.HasValue ? unchecked((long)i.HandlerRoutineAddress.Value) : 0L,
        NormalizeModuleName(i.HandlerModuleName),
        i.HandlerStackFrame?.RelativeVirtualAddress is ulong rva ? unchecked((long)rva) : null,
        i.HandlerStackFrame?.SymbolName,
        i.Vector,
        i.ReturnValue);

    internal static IsrSnapshot ToIsrSnapshot(IInterruptServiceRoutineInterval i) => new(
        i.Processor,
        i.StartTime.DateTimeOffset.UtcDateTime,
        i.StopTime.DateTimeOffset.UtcDateTime,
        i.HandlerRoutineAddress.Value,
        NormalizeModuleName(i.HandlerImage?.FileName),
        i.HandlerStackFrame.HasValue ? i.HandlerStackFrame.RelativeVirtualAddress.Value : null,
        i.HandlerStackFrame.Symbol?.FunctionName,
        i.Vector,
        i.ReturnValue);

    private static string NormalizeModuleName(string? moduleName) =>
        string.IsNullOrEmpty(moduleName) ? "<未映射>" : Path.GetFileName(moduleName);

    /// <summary>
    /// ISR/DPC 兩邊都沒有可跨實作對應的唯一 ID(不像 Process 有 PID、Thread 有 TID)，
    /// 故以「同一顆處理器內、依 StopTime(原始事件時間戳，未經任何推算)排序後逐筆配對」取代查表比對：
    /// 只要兩邊確實解析自同一份 ETL、同一個處理器上的事件發生順序不會被任一邊打亂，此法即等同逐筆比對。
    /// 若某處理器兩邊筆數不同，多出的尾端筆數會分別計入 EtlOnlyCount/TpOnlyCount，不參與配對。
    /// </summary>
    private static (List<(T Etl, T Tp)> Matched, int ProcessorCountMismatch, int EtlOnlyCount, int TpOnlyCount)
        MatchIntervalsByProcessor<T>(
            IReadOnlyList<T> etl,
            IReadOnlyList<T> tp,
            Func<T, int> processorSelector,
            Func<T, DateTime> orderSelector)
    {
        Dictionary<int, List<T>> etlByProcessor = etl
            .GroupBy(processorSelector)
            .ToDictionary(g => g.Key, g => g.OrderBy(orderSelector).ToList());
        Dictionary<int, List<T>> tpByProcessor = tp
            .GroupBy(processorSelector)
            .ToDictionary(g => g.Key, g => g.OrderBy(orderSelector).ToList());

        List<(T, T)> matched = [];
        int processorCountMismatch = 0;
        int etlOnlyCount = 0;
        int tpOnlyCount = 0;

        foreach (int processor in etlByProcessor.Keys.Union(tpByProcessor.Keys))
        {
            List<T> etlList = etlByProcessor.TryGetValue(processor, out List<T>? e) ? e : [];
            List<T> tpList = tpByProcessor.TryGetValue(processor, out List<T>? t) ? t : [];
            if (etlList.Count != tpList.Count)
            {
                processorCountMismatch++;
            }

            int pairCount = Math.Min(etlList.Count, tpList.Count);
            for (int i = 0; i < pairCount; i++)
            {
                matched.Add((etlList[i], tpList[i]));
            }
            etlOnlyCount += etlList.Count - pairCount;
            tpOnlyCount += tpList.Count - pairCount;
        }

        return (matched, processorCountMismatch, etlOnlyCount, tpOnlyCount);
    }

    private static void ValidateDeferredProcedureCalls(
        IReadOnlyList<DeferredProcedureCallInterval> etl,
        IReadOnlyList<DpcSnapshot> tp)
    {
        List<DpcSnapshot> etlSnapshots = [.. etl.Select(ToDpcSnapshot)];

        (List<(DpcSnapshot Etl, DpcSnapshot Tp)> matched, int processorCountMismatch, int etlOnlyCount, int tpOnlyCount) =
            MatchIntervalsByProcessor(etlSnapshots, tp, s => s.Processor, s => s.StopTime);

        List<(int Index, DpcSnapshot Etl, DpcSnapshot Tp)> different = [];
        for (int i = 0; i < matched.Count; i++)
        {
            (DpcSnapshot e, DpcSnapshot t) = matched[i];
            bool startTimeMismatch = Math.Abs((e.StartTime - t.StartTime).TotalMilliseconds) > InterruptTimestampToleranceMilliseconds;
            bool stopTimeMismatch = Math.Abs((e.StopTime - t.StopTime).TotalMilliseconds) > InterruptTimestampToleranceMilliseconds;
            bool addressMismatch = e.HandlerRoutineAddress != t.HandlerRoutineAddress;
            bool moduleMismatch = !string.Equals(e.HandlerModuleName, t.HandlerModuleName, StringComparison.OrdinalIgnoreCase);
            bool relativeVirtualAddressMismatch =
                e.HandlerRelativeVirtualAddress != t.HandlerRelativeVirtualAddress;
            bool callTypeMismatch = !string.Equals(e.CallType, t.CallType, StringComparison.Ordinal);
            bool symbolMismatch =
                e.HandlerSymbolName is not null &&
                t.HandlerSymbolName is not null &&
                !string.Equals(e.HandlerSymbolName, t.HandlerSymbolName, StringComparison.Ordinal);
            if (startTimeMismatch ||
                stopTimeMismatch ||
                addressMismatch ||
                moduleMismatch ||
                relativeVirtualAddressMismatch ||
                symbolMismatch ||
                callTypeMismatch)
            {
                different.Add((i, e, t));
            }
        }

        Console.WriteLine($"[UseInterruptHandlingData-DPC] EtlFileReader={etl.Count} 筆, TraceProcessor={tp.Count} 筆, " +
            $"依 Processor 分組+StopTime 排序配對成功={matched.Count} 筆(筆數不同的處理器數={processorCountMismatch}, " +
            $"僅 EtlFileReader 多出={etlOnlyCount} 筆, 僅 TraceProcessor 多出={tpOnlyCount} 筆), 內容不同={different.Count} 筆");

        foreach ((int index, DpcSnapshot e, DpcSnapshot t) in different.Take(MaxDifferenceSamplesToPrint))
        {
            Console.WriteLine($"  #{index} Processor={e.Processor}: " +
                $"StartTime EtlFileReader={e.StartTime:HH:mm:ss.fffffff} vs TraceProcessor={t.StartTime:HH:mm:ss.fffffff}; " +
                $"StopTime EtlFileReader={e.StopTime:HH:mm:ss.ffffff} vs TraceProcessor={t.StopTime:HH:mm:ss.ffffff}; " +
                $"HandlerRoutineAddress 0x{e.HandlerRoutineAddress:X} vs 0x{t.HandlerRoutineAddress:X}; " +
                $"HandlerModuleName \"{e.HandlerModuleName}\" vs \"{t.HandlerModuleName}\"; " +
                $"HandlerRVA 0x{e.HandlerRelativeVirtualAddress:X} vs 0x{t.HandlerRelativeVirtualAddress:X}; " +
                $"HandlerSymbol \"{e.HandlerSymbolName ?? "<未載入>"}\" vs \"{t.HandlerSymbolName ?? "<未載入>"}\"; " +
                $"CallType {e.CallType} vs {t.CallType}");
        }
        if (different.Count > MaxDifferenceSamplesToPrint)
        {
            Console.WriteLine($"  ...其餘 {different.Count - MaxDifferenceSamplesToPrint} 筆不同省略");
        }
    }

    private static void ValidateInterruptServiceRoutines(
        IReadOnlyList<InterruptServiceRoutineInterval> etl,
        IReadOnlyList<IsrSnapshot> tp)
    {
        List<IsrSnapshot> etlSnapshots = [.. etl.Select(ToIsrSnapshot)];

        (List<(IsrSnapshot Etl, IsrSnapshot Tp)> matched, int processorCountMismatch, int etlOnlyCount, int tpOnlyCount) =
            MatchIntervalsByProcessor(etlSnapshots, tp, s => s.Processor, s => s.StopTime);

        List<(int Index, IsrSnapshot Etl, IsrSnapshot Tp)> different = [];
        for (int i = 0; i < matched.Count; i++)
        {
            (IsrSnapshot e, IsrSnapshot t) = matched[i];
            bool startTimeMismatch = Math.Abs((e.StartTime - t.StartTime).TotalMilliseconds) > InterruptTimestampToleranceMilliseconds;
            bool stopTimeMismatch = Math.Abs((e.StopTime - t.StopTime).TotalMilliseconds) > InterruptTimestampToleranceMilliseconds;
            bool addressMismatch = e.HandlerRoutineAddress != t.HandlerRoutineAddress;
            bool moduleMismatch = !string.Equals(e.HandlerModuleName, t.HandlerModuleName, StringComparison.OrdinalIgnoreCase);
            bool relativeVirtualAddressMismatch =
                e.HandlerRelativeVirtualAddress != t.HandlerRelativeVirtualAddress;
            bool vectorMismatch = e.Vector != t.Vector;
            bool returnValueMismatch = e.ReturnValue != t.ReturnValue;
            bool symbolMismatch =
                e.HandlerSymbolName is not null &&
                t.HandlerSymbolName is not null &&
                !string.Equals(e.HandlerSymbolName, t.HandlerSymbolName, StringComparison.Ordinal);
            if (startTimeMismatch ||
                stopTimeMismatch ||
                addressMismatch ||
                moduleMismatch ||
                relativeVirtualAddressMismatch ||
                symbolMismatch ||
                vectorMismatch ||
                returnValueMismatch)
            {
                different.Add((i, e, t));
            }
        }

        Console.WriteLine($"[UseInterruptHandlingData-ISR] EtlFileReader={etl.Count} 筆, TraceProcessor={tp.Count} 筆, " +
            $"依 Processor 分組+StopTime 排序配對成功={matched.Count} 筆(筆數不同的處理器數={processorCountMismatch}, " +
            $"僅 EtlFileReader 多出={etlOnlyCount} 筆, 僅 TraceProcessor 多出={tpOnlyCount} 筆), 內容不同={different.Count} 筆");

        foreach ((int index, IsrSnapshot e, IsrSnapshot t) in different.Take(MaxDifferenceSamplesToPrint))
        {
            Console.WriteLine($"  #{index} Processor={e.Processor}: " +
                $"StartTime EtlFileReader={e.StartTime:HH:mm:ss.fffffff} vs TraceProcessor={t.StartTime:HH:mm:ss.fffffff}; " +
                $"StopTime EtlFileReader={e.StopTime:HH:mm:ss.ffffff} vs TraceProcessor={t.StopTime:HH:mm:ss.ffffff}; " +
                $"HandlerRoutineAddress 0x{e.HandlerRoutineAddress:X} vs 0x{t.HandlerRoutineAddress:X}; " +
                $"HandlerModuleName \"{e.HandlerModuleName}\" vs \"{t.HandlerModuleName}\"; " +
                $"HandlerRVA 0x{e.HandlerRelativeVirtualAddress:X} vs 0x{t.HandlerRelativeVirtualAddress:X}; " +
                $"HandlerSymbol \"{e.HandlerSymbolName ?? "<未載入>"}\" vs \"{t.HandlerSymbolName ?? "<未載入>"}\"; " +
                $"Vector {e.Vector} vs {t.Vector}; ReturnValue {e.ReturnValue} vs {t.ReturnValue}");
        }
        if (different.Count > MaxDifferenceSamplesToPrint)
        {
            Console.WriteLine($"  ...其餘 {different.Count - MaxDifferenceSamplesToPrint} 筆不同省略");
        }
    }

    private static void ValidateEnergyEstimation(DemoResult etl, DemoResult tp)
    {
        double diffPercent = tp.EnergyTotalMilliJoules == 0
            ? 0
            : Math.Abs(etl.EnergyTotalMilliJoules - tp.EnergyTotalMilliJoules) / tp.EnergyTotalMilliJoules * 100.0;
        string countMatch = etl.EnergyIntervalCount == tp.EnergyIntervalCount ? "一致" : "不一致";

        Console.WriteLine($"[UseEnergyEstimationData] 週期筆數 EtlFileReader={etl.EnergyIntervalCount} vs " +
            $"TraceProcessor={tp.EnergyIntervalCount}（{countMatch}），總能耗 EtlFileReader={etl.EnergyTotalMilliJoules:F1} mJ vs " +
            $"TraceProcessor={tp.EnergyTotalMilliJoules:F1} mJ（差異 {diffPercent:F2}%）。");

        if (etl.EnergyIntervalCount > 0 && tp.EnergyIntervalCount == 0)
        {
            Console.WriteLine("  已知落差(非本專案 Bug，已確認根因)：本機的 EnergyEstimate(Event 37)為 Version 2，" +
                "TraceProcessor 內建的 UseEnergyEstimationData() 對此版本完全不回傳資料；但用 TraceProcessor 自己的" +
                "UseGenericEvents() 通用解析交叉比對，同一份 ETL 卻能正確解出與 EtlFileReader 相同的筆數，" +
                "證實事件確實存在且可正確解析，純粹是 TraceProcessor 這個專用資料來源不支援 Version 2 schema，" +
                "屬 TraceProcessor 套件本身的相容性限制，並非 EtlFileReader 的錯誤，也無法從本專案端修正。");
        }
        else if (etl.EnergyIntervalCount > 0 || tp.EnergyIntervalCount > 0)
        {
            Console.WriteLine("  注意：EtlFileReader 的 TotalEnergy 是自行加總各分項欄位的近似值，TraceProcessor 內部實際公式未公開，兩者不保證完全相等，僅供參考。");
        }
    }

    private static void ValidateInterruptHandling(IReadOnlyList<InterruptModuleSnapshot> etl, IReadOnlyList<InterruptModuleSnapshot> tp)
    {
        Dictionary<string, InterruptModuleSnapshot> etlByModule = ToLookup(etl, m => m.ModuleName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, InterruptModuleSnapshot> tpByModule = ToLookup(tp, m => m.ModuleName, StringComparer.OrdinalIgnoreCase);

        List<string> onlyInEtl = [.. etlByModule.Keys.Except(tpByModule.Keys, StringComparer.OrdinalIgnoreCase)];
        List<string> onlyInTp = [.. tpByModule.Keys.Except(etlByModule.Keys, StringComparer.OrdinalIgnoreCase)];
        List<string> common = [.. etlByModule.Keys.Intersect(tpByModule.Keys, StringComparer.OrdinalIgnoreCase)];
        int isrCountMismatch = common.Count(m => etlByModule[m].IsrCount != tpByModule[m].IsrCount);
        int dpcCountMismatch = common.Count(m => etlByModule[m].DpcCount != tpByModule[m].DpcCount);

        int etlIsrTotal = etl.Sum(m => m.IsrCount);
        int tpIsrTotal = tp.Sum(m => m.IsrCount);
        int etlDpcTotal = etl.Sum(m => m.DpcCount);
        int tpDpcTotal = tp.Sum(m => m.DpcCount);

        Console.WriteLine($"[UseInterruptHandlingData] ISR 總筆數 EtlFileReader={etlIsrTotal} vs TraceProcessor={tpIsrTotal}, " +
            $"DPC 總筆數 EtlFileReader={etlDpcTotal} vs TraceProcessor={tpDpcTotal}" +
            "（依文件記載，筆數、所屬模組與執行時間應一致）");
        Console.WriteLine($"  模組數 EtlFileReader={etlByModule.Count} vs TraceProcessor={tpByModule.Count}, 共同模組={common.Count}, " +
            $"僅 EtlFileReader 有={onlyInEtl.Count}, 僅 TraceProcessor 有={onlyInTp.Count}, " +
            $"ISR 筆數不同的模組={isrCountMismatch}, DPC 筆數不同的模組={dpcCountMismatch}");
        PrintSample("僅 EtlFileReader 有的模組", onlyInEtl);
        PrintSample("僅 TraceProcessor 有的模組", onlyInTp);
    }

    private static Dictionary<TKey, TValue> ToLookup<TKey, TValue>(
        IReadOnlyList<TValue> items,
        Func<TValue, TKey> keySelector,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        comparer ??= EqualityComparer<TKey>.Default;
        return items.GroupBy(keySelector, comparer).ToDictionary(g => g.Key, g => g.First(), comparer);
    }

    private static void PrintSample<T>(string label, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  {label}（共 {items.Count} 筆，僅列前 5 筆）: {string.Join(", ", items.Take(5))}");
    }
}
