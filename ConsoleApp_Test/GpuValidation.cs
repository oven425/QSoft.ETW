using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Graphics;
using Microsoft.Windows.EventTracing.Processes;
using QSoft.ETW;
using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// 驗證 <c>EtlFileReader.UseGpuData()</c> 與 TraceProcessor <c>trace.UseDirectXData()</c> 解析同一份 ETL 的結果是否完全相同。
/// TraceProcessor 只提供 <see cref="IDirectXDataSource.DmaPackets"/>，因此 DmaPackets 逐欄位比對；
/// 其餘 GPU 資料（Adapter / Node / Segment / 記憶體 / 效能 / 硬體執行區間）改以 TraceProcessor 的 GenericEvents 解出原始事件欄位後交叉驗證。
/// 用法：<c>ConsoleApp_Test gpu-validate &lt;etl 路徑&gt; [傾印資料夾]</c>
/// </summary>
internal static partial class GpuValidation
{
    private const int MaxDifferenceSamplesToPrint = 10;

    /// <summary>一筆 DmaPacket 的可比較內容。時間用 UTC Ticks（100ns），持續時間用奈秒。</summary>
    private sealed record DmaRow(
        long AdapterId,
        long NodeId,
        long NodeEngineId,
        long Type,
        long SoftwareSubmitTicks,
        long HardwareSubmitTicks,
        long ExecutionStartTicks,
        long ExecutionCompleteTicks,
        long SoftwareQueueNs,
        long HardwareQueueNs,
        long ExecutionNs,
        long? ProcessId,
        string? ImageName,
        long? ThreadId);

    public static int Run(string etlPath, string? dumpDirectory)
    {
        if (!File.Exists(etlPath))
        {
            Console.Error.WriteLine($"找不到 ETL 檔案：{etlPath}");
            return 1;
        }

        Console.WriteLine("=== GPU 驗證：EtlFileReader.UseGpuData() vs TraceProcessor.UseDirectXData() ===");
        Console.WriteLine($"ETL: {etlPath}（{new FileInfo(etlPath).Length / 1024.0 / 1024.0:F2} MB）");

        List<DmaRow> traceProcessorRows = ReadTraceProcessor(etlPath, out TimeSpan traceProcessorElapsed, out MsDxgEvents msDxgEvents);
        Console.WriteLine($"[TraceProcessor] trace.Process() 耗時 {traceProcessorElapsed.TotalMilliseconds:F0} ms，DmaPackets {traceProcessorRows.Count} 筆");

        List<DmaRow> etlFileReaderRows = ReadEtlFileReader(etlPath, out IGpuDataSource gpu, out TimeSpan etlFileReaderElapsed);
        Console.WriteLine($"[EtlFileReader] ProcessFile() 耗時 {etlFileReaderElapsed.TotalMilliseconds:F0} ms，DmaPackets {etlFileReaderRows.Count} 筆");

        bool identical = CompareDmaPackets(etlFileReaderRows, traceProcessorRows);
        ReportNativeOrder(etlFileReaderRows, traceProcessorRows);
        identical &= CompareWithGenericEvents(gpu, msDxgEvents);
        ReportOtherGpuData(gpu);

        if (!string.IsNullOrEmpty(dumpDirectory))
        {
            DumpGpuData(gpu, dumpDirectory);
        }

        Console.WriteLine();
        Console.WriteLine(identical
            ? "結論：UseGpuData() 與 TraceProcessor 可比對的資料完全相同。"
            : "結論：UseGpuData() 與 TraceProcessor 有差異，請見上方明細。");
        return identical ? 0 : 2;
    }

    private static List<DmaRow> ReadTraceProcessor(string etlPath, out TimeSpan elapsed, out MsDxgEvents dxgEvents)
    {
        using ITraceProcessor trace = TraceProcessor.Create(etlPath, new TraceProcessorSettings { AllowLostEvents = true });
        var pendingDirectX = trace.UseDirectXData();
        var pendingGenericEvents = trace.UseGenericEvents(new GenericEventSettings { ProviderIds = [TraceSessionBuilder.DxgKrnlProviderGuid] });

        // SubmittingProcess / SubmittingThread 需要 Process 與 Thread 資料才會解析。
        trace.UseProcesses();
        trace.UseThreads();

        Stopwatch stopwatch = Stopwatch.StartNew();
        trace.Process();
        elapsed = stopwatch.Elapsed;
        dxgEvents = SnapshotDxgEvents(pendingGenericEvents.Result.Events);

        List<DmaRow> rows = [];
        foreach (IDmaPacket packet in pendingDirectX.Result.DmaPackets)
        {
            rows.Add(new DmaRow(
                packet.AdapterId,
                packet.NodeId,
                packet.NodeEngineId,
                ReadTraceProcessorType(packet),
                packet.SoftwareSubmitTime.DateTimeOffset.UtcTicks,
                packet.HardwareSubmitTime.DateTimeOffset.UtcTicks,
                packet.ExecutionStartTime.DateTimeOffset.UtcTicks,
                packet.ExecutionCompleteTime.DateTimeOffset.UtcTicks,
                packet.SoftwareQueueDuration.Nanoseconds,
                packet.HardwareQueueDuration.Nanoseconds,
                packet.ExecutionDuration.Nanoseconds,
                packet.SubmittingProcess?.Id,
                packet.SubmittingProcess?.ImageName,
                packet.SubmittingThread?.Id));
        }

        return rows;
    }

    // 未知種類的 DMA buffer：TraceProcessor 讀取 Type 時會丟出 NotSupportedException，以 -1 表示，對應 GpuDmaPacketType.Unknown。
    private static long ReadTraceProcessorType(IDmaPacket packet)
    {
        try
        {
            return (long)packet.Type;
        }
        catch (NotSupportedException)
        {
            return -1;
        }
    }

    private static List<DmaRow> ReadEtlFileReader(string etlPath, out IGpuDataSource gpu, out TimeSpan elapsed)
    {
        EtlFileReader reader = new();
        var pendingGpu = reader.UseGpuData();
        var pendingProcesses = reader.UseProcesses();

        Stopwatch stopwatch = Stopwatch.StartNew();
        reader.ProcessFile(etlPath);
        elapsed = stopwatch.Elapsed;

        gpu = pendingGpu.Result;
        Dictionary<uint, List<ProcessRecord>> processesById = [];
        foreach (ProcessRecord process in pendingProcesses.Result.Processes)
        {
            if (!processesById.TryGetValue(process.ProcessId, out List<ProcessRecord>? list))
            {
                processesById[process.ProcessId] = list = [];
            }

            list.Add(process);
        }

        List<DmaRow> rows = [];
        foreach (GpuDmaPacket packet in gpu.DmaPackets)
        {
            ProcessRecord? process = packet.SubmittingProcessId is { } submittingProcessId
                ? FindProcess(processesById, submittingProcessId, packet.SoftwareSubmitTime)
                : null;
            rows.Add(new DmaRow(
                packet.AdapterId,
                packet.NodeId,
                packet.NodeEngineId,
                (long)packet.Type,
                packet.SoftwareSubmitTime.ToUniversalTime().Ticks,
                packet.HardwareSubmitTime.ToUniversalTime().Ticks,
                packet.ExecutionStartTime.ToUniversalTime().Ticks,
                packet.ExecutionCompleteTime.ToUniversalTime().Ticks,
                packet.SoftwareQueueDuration.Ticks * 100,
                packet.HardwareQueueDuration.Ticks * 100,
                packet.ExecutionDuration.Ticks * 100,
                process is null ? null : packet.SubmittingProcessId,
                process?.ImageFileName,
                packet.SubmittingThreadId));
        }

        return rows;
    }

    private static ProcessRecord? FindProcess(Dictionary<uint, List<ProcessRecord>> processesById, uint processId, DateTime time)
    {
        if (!processesById.TryGetValue(processId, out List<ProcessRecord>? candidates))
        {
            return null;
        }

        ProcessRecord? match = null;
        foreach (ProcessRecord candidate in candidates)
        {
            bool started = candidate.StartTime is not { } start || start <= time;
            bool alive = candidate.EndTime is not { } end || time <= end;
            if (started && alive)
            {
                match = candidate;
            }
        }

        return match;
    }

    private static IOrderedEnumerable<DmaRow> Canonical(IEnumerable<DmaRow> rows) => rows
        .OrderBy(row => row.SoftwareSubmitTicks)
        .ThenBy(row => row.HardwareSubmitTicks)
        .ThenBy(row => row.NodeId)
        .ThenBy(row => row.NodeEngineId)
        .ThenBy(row => row.AdapterId)
        .ThenBy(row => row.ExecutionCompleteTicks)
        .ThenBy(row => row.ExecutionStartTicks)
        .ThenBy(row => row.Type)
        .ThenBy(row => row.ThreadId)
        .ThenBy(row => row.ProcessId)
        .ThenBy(row => row.ImageName, StringComparer.Ordinal);

    private static List<string> DifferentFields(DmaRow etl, DmaRow tp)
    {
        List<string> fields = [];
        AddIfDifferent(fields, nameof(DmaRow.AdapterId), etl.AdapterId, tp.AdapterId);
        AddIfDifferent(fields, nameof(DmaRow.NodeId), etl.NodeId, tp.NodeId);
        AddIfDifferent(fields, nameof(DmaRow.NodeEngineId), etl.NodeEngineId, tp.NodeEngineId);
        AddIfDifferent(fields, nameof(DmaRow.Type), etl.Type, tp.Type);
        AddIfDifferent(fields, nameof(DmaRow.SoftwareSubmitTicks), etl.SoftwareSubmitTicks, tp.SoftwareSubmitTicks);
        AddIfDifferent(fields, nameof(DmaRow.HardwareSubmitTicks), etl.HardwareSubmitTicks, tp.HardwareSubmitTicks);
        AddIfDifferent(fields, nameof(DmaRow.ExecutionStartTicks), etl.ExecutionStartTicks, tp.ExecutionStartTicks);
        AddIfDifferent(fields, nameof(DmaRow.ExecutionCompleteTicks), etl.ExecutionCompleteTicks, tp.ExecutionCompleteTicks);
        AddIfDifferent(fields, nameof(DmaRow.SoftwareQueueNs), etl.SoftwareQueueNs, tp.SoftwareQueueNs);
        AddIfDifferent(fields, nameof(DmaRow.HardwareQueueNs), etl.HardwareQueueNs, tp.HardwareQueueNs);
        AddIfDifferent(fields, nameof(DmaRow.ExecutionNs), etl.ExecutionNs, tp.ExecutionNs);
        AddIfDifferent(fields, nameof(DmaRow.ProcessId), etl.ProcessId, tp.ProcessId);
        AddIfDifferent(fields, nameof(DmaRow.ImageName), etl.ImageName, tp.ImageName);
        AddIfDifferent(fields, nameof(DmaRow.ThreadId), etl.ThreadId, tp.ThreadId);
        return fields;
    }

    private static void AddIfDifferent<T>(List<string> fields, string name, T etl, T tp)
    {
        if (!EqualityComparer<T>.Default.Equals(etl, tp))
        {
            fields.Add($"{name}: EtlFileReader={etl?.ToString() ?? "null"} TraceProcessor={tp?.ToString() ?? "null"}");
        }
    }

    private static bool CompareDmaPackets(List<DmaRow> etlFileReaderRows, List<DmaRow> traceProcessorRows)
    {
        Console.WriteLine();
        Console.WriteLine("--- DmaPackets 逐筆比對（兩邊都先依 SoftwareSubmit、HardwareSubmit、Node… 排序後對齊） ---");
        Console.WriteLine($"[DmaPackets] EtlFileReader={etlFileReaderRows.Count} 筆, TraceProcessor={traceProcessorRows.Count} 筆");

        // 與順序無關的精確比對：整列內容（record 值相等）的多重集合差集。
        Dictionary<DmaRow, int> balance = [];
        foreach (DmaRow row in etlFileReaderRows)
        {
            balance[row] = balance.GetValueOrDefault(row) + 1;
        }

        foreach (DmaRow row in traceProcessorRows)
        {
            balance[row] = balance.GetValueOrDefault(row) - 1;
        }

        int onlyEtlFileReader = balance.Values.Where(count => count > 0).Sum();
        int onlyTraceProcessor = -balance.Values.Where(count => count < 0).Sum();
        int identicalCount = etlFileReaderRows.Count - onlyEtlFileReader;
        Console.WriteLine($"[DmaPackets] 內容完全相同={identicalCount} 筆, 只在 EtlFileReader={onlyEtlFileReader} 筆, 只在 TraceProcessor={onlyTraceProcessor} 筆");

        if (onlyEtlFileReader == 0 && onlyTraceProcessor == 0)
        {
            return true;
        }

        DmaRow[] etlSorted = [.. Canonical(etlFileReaderRows)];
        DmaRow[] tpSorted = [.. Canonical(traceProcessorRows)];
        Dictionary<string, int> fieldCounts = [];
        int differentCount = 0;
        int printed = 0;
        int common = Math.Min(etlSorted.Length, tpSorted.Length);
        for (int i = 0; i < common; i++)
        {
            List<string> fields = DifferentFields(etlSorted[i], tpSorted[i]);
            if (fields.Count == 0)
            {
                continue;
            }

            differentCount++;
            foreach (string field in fields)
            {
                string name = field[..field.IndexOf(':')];
                fieldCounts[name] = fieldCounts.GetValueOrDefault(name) + 1;
            }

            if (printed++ < MaxDifferenceSamplesToPrint)
            {
                Console.WriteLine($"  #{i} 差異: {string.Join("; ", fields)}");
            }
        }

        Console.WriteLine($"[DmaPackets] 排序後對齊比對：內容不同={differentCount} 筆（兩邊筆數差 {Math.Abs(etlSorted.Length - tpSorted.Length)}）");
        foreach ((string name, int count) in fieldCounts.OrderByDescending(pair => pair.Value).Select(pair => (pair.Key, pair.Value)))
        {
            Console.WriteLine($"    欄位 {name}: {count} 筆不同");
        }

        return false;
    }

    /// <summary>
    /// 資訊：TraceProcessor 的 DmaPackets 以 SoftwareSubmitTime 排序，但同一個 SoftwareSubmitTime 的 packet（一個 Queue packet 拆成多個 DMA buffer）
    /// 之間的順序並不固定，所以上面的比對不依賴 TraceProcessor 的原生順序；這裡只統計兩者順序的差異程度。
    /// </summary>
    private static void ReportNativeOrder(List<DmaRow> etlFileReaderRows, List<DmaRow> traceProcessorRows)
    {
        Console.WriteLine();
        Console.WriteLine("--- 排序資訊（僅供參考，不影響上面的判定） ---");
        Console.WriteLine($"[排序] EtlFileReader 依 SoftwareSubmitTime 遞增: {(IsSortedBySoftwareSubmit(etlFileReaderRows) ? "是" : "否")}；" +
                          $"TraceProcessor 依 SoftwareSubmitTime 遞增: {(IsSortedBySoftwareSubmit(traceProcessorRows) ? "是" : "否")}");

        int groups = 0;
        int differentOrderGroups = 0;
        int rowsInDifferentOrderGroups = 0;
        for (int start = 0; start < traceProcessorRows.Count;)
        {
            int end = start + 1;
            while (end < traceProcessorRows.Count &&
                   traceProcessorRows[end].SoftwareSubmitTicks == traceProcessorRows[start].SoftwareSubmitTicks)
            {
                end++;
            }

            if (end - start > 1)
            {
                groups++;
                DmaRow[] native = [.. traceProcessorRows.Skip(start).Take(end - start)];
                DmaRow[] canonical = [.. Canonical(native)];
                if (!native.SequenceEqual(canonical))
                {
                    differentOrderGroups++;
                    rowsInDifferentOrderGroups += native.Length;
                }
            }

            start = end;
        }

        Console.WriteLine($"[排序] TraceProcessor 中 SoftwareSubmitTime 相同的群組 {groups} 組，其中原生順序與 EtlFileReader 的排序（HardwareSubmit 遞增）不同者 {differentOrderGroups} 組（{rowsInDifferentOrderGroups} 筆）");
    }

    private static bool IsSortedBySoftwareSubmit(List<DmaRow> rows)
    {
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i].SoftwareSubmitTicks < rows[i - 1].SoftwareSubmitTicks)
            {
                return false;
            }
        }

        return true;
    }

    private static void ReportOtherGpuData(IGpuDataSource gpu)
    {
        Console.WriteLine();
        Console.WriteLine("--- UseGpuData() 其他資料（TraceProcessor 沒有對應 API） ---");
        Console.WriteLine($"[Adapters] {gpu.Adapters.Count} 個，[Nodes] {gpu.Nodes.Count} 個，[Segments] {gpu.Segments.Count} 個，" +
                          $"[EngineExecutions] {gpu.EngineExecutions.Count} 筆，[SegmentResidency] {gpu.SegmentResidencySamples.Count} 筆，" +
                          $"[ProcessMemoryUsage] {gpu.ProcessMemoryUsageChanges.Count} 筆，[Performance] {gpu.PerformanceSamples.Count} 筆，" +
                          $"[未能對應 Context 的 DMA submit] {gpu.UnresolvedDmaSubmitCount} 筆");
        foreach (GpuAdapterRecord adapter in gpu.Adapters)
        {
            Console.WriteLine($"  Adapter {adapter.AdapterId}: LUID=0x{adapter.Luid:X} VEN_{adapter.VendorId:X4}&DEV_{adapter.DeviceId:X4} BusType={adapter.BusType} 名稱={adapter.Name ?? "(未對應)"}");
            foreach (GpuNodeRecord node in gpu.Nodes.Where(node => node.AdapterId == adapter.AdapterId))
            {
                Console.WriteLine($"    Node {node.NodeId}: {node.EngineType} \"{node.FriendlyName}\" => {node.DisplayName}");
            }

            foreach (GpuSegmentRecord segment in gpu.Segments.Where(segment => segment.AdapterId == adapter.AdapterId))
            {
                Console.WriteLine($"    Segment {segment.SegmentId}: {segment.Size / 1024.0 / 1024.0:F0} MiB 群組={segment.Group} Flags=0x{segment.RawFlags:X}");
            }
        }

        if (gpu.TraceStartTime is { } traceStart && gpu.TraceEndTime is { } traceEnd && traceEnd > traceStart)
        {
            Console.WriteLine($"[Utilization] 整段追蹤 {(traceEnd - traceStart).TotalSeconds:F1} 秒內各 Engine 使用率：");
            foreach (GpuEngineUtilization utilization in gpu.GetEngineUtilization(traceStart, traceEnd).Where(item => item.Utilization > 0))
            {
                Console.WriteLine($"  Adapter {utilization.AdapterId} Node {utilization.NodeId} ({utilization.DisplayName}): {utilization.Utilization:P2}");
            }
        }
    }

    private static void DumpGpuData(IGpuDataSource gpu, string directory)
    {
        Directory.CreateDirectory(directory);
        static string Ticks(DateTime time) => time.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        static string Hex(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

        WriteTsv(directory, "adapters.tsv", "AdapterId\tAddress\tLuid\tVendor\tDevice\tSubVendor\tSubSystem\tRevision\tBusType\tName\tDeviceInstanceId",
            gpu.Adapters.Select(a => string.Join('\t', a.AdapterId, Hex(a.AdapterAddress), a.Luid, a.VendorId, a.DeviceId, a.SubVendorId, a.SubSystemId, a.RevisionId, a.BusType, a.Name, a.DeviceInstanceId)));
        WriteTsv(directory, "nodes.tsv", "AdapterId\tNodeOrdinal\tNodeId\tEngineType\tFriendlyName\tDisplayName",
            gpu.Nodes.Select(n => string.Join('\t', n.AdapterId, n.NodeOrdinal, n.NodeId, (uint)n.EngineType, n.FriendlyName, n.DisplayName)));
        WriteTsv(directory, "segments.tsv", "AdapterId\tSegmentId\tSize\tCommitLimit\tFlags\tGroup\tBaseAddress\tCpuTranslatedAddress\tNumberOfBanks\tSystemMemoryEndAddress",
            gpu.Segments.Select(s => string.Join('\t', s.AdapterId, s.SegmentId, s.Size, s.CommitLimit, s.RawFlags, (byte)s.Group, s.BaseAddress, s.CpuTranslatedAddress, s.NumberOfBanks, s.SystemMemoryEndAddress)));
        WriteTsv(directory, "residency.tsv", "UtcTicks\tAdapterId\tSegmentId\tBytesResident",
            gpu.SegmentResidencySamples.Select(r => string.Join('\t', Ticks(r.Timestamp), r.AdapterId, r.SegmentId, r.BytesResident)));
        WriteTsv(directory, "usage.tsv", "UtcTicks\tAdapterId\tProcessId\tPhysicalAdapterIndex\tSegmentGroup\tNewUsage\tOldUsage",
            gpu.ProcessMemoryUsageChanges.Select(u => string.Join('\t', Ticks(u.Timestamp), u.AdapterId, u.ProcessId, u.PhysicalAdapterIndex, (byte)u.SegmentGroup, u.NewUsageBytes, u.OldUsageBytes)));
        WriteTsv(directory, "perf.tsv", "UtcTicks\tAdapterId\tLuid\tPhysicalAdapterIndex\tMemoryFrequency\tPower\tTemperature\tFanRpm\tEngineOrdinal\tEngineFrequency\tEngineVoltage\tEngineMaxTransitionLatency\tDdiQueryElapsedTimeMs",
            gpu.PerformanceSamples.Select(p => string.Join('\t', Ticks(p.Timestamp), p.AdapterId, p.AdapterLuid, p.PhysicalAdapterIndex, p.GpuMemoryFrequency, p.GpuPower, p.GpuTemperature, p.GpuFanRpm, p.EngineOrdinal, p.EngineFrequency, p.EngineVoltage, p.EngineMaxTransitionLatency, p.DdiQueryElapsedTimeMs)));
        WriteTsv(directory, "executions.tsv", "AdapterId\tNodeOrdinal\tEngineAffinity\tHContext\tProcessId\tPacketType\tSubmissionId\tSubmitUtcTicks\tCompleteUtcTicks\tInterruptType",
            gpu.EngineExecutions.Select(e => string.Join('\t', e.AdapterId, e.NodeOrdinal, e.EngineAffinity, e.ContextHandle, e.ProcessId, (uint)e.PacketType, e.SubmissionId, Ticks(e.SubmitTime), e.CompleteTime is { } c ? Ticks(c) : "", e.InterruptType is { } i ? (uint)i : "")));
        WriteTsv(directory, "dma_packets.tsv", "AdapterId\tNodeId\tNodeEngineId\tType\tSoftwareSubmit\tHardwareSubmit\tExecutionStart\tExecutionComplete\tProcessId\tThreadId\tHContext\tSubmissionId\tSubmitSequence\tQueuePacketType\tIsPresent\tInterruptType\tHardwarePacketType",
            gpu.DmaPackets.Select(p => string.Join('\t', p.AdapterId, p.NodeId, p.NodeEngineId, (int)p.Type, Ticks(p.SoftwareSubmitTime), Ticks(p.HardwareSubmitTime), Ticks(p.ExecutionStartTime), Ticks(p.ExecutionCompleteTime), p.SubmittingProcessId, p.SubmittingThreadId, p.ContextHandle, p.SubmissionId, p.SubmitSequence, p.QueuePacketType is { } queueType ? (uint)queueType : "", p.IsPresent ? 1 : 0, (uint)p.InterruptType, (uint)p.HardwarePacketType)));

        if (gpu.TraceStartTime is { } traceStart && gpu.TraceEndTime is { } traceEnd && traceEnd > traceStart)
        {
            WriteTsv(directory, "trace_window.tsv", "TraceStartUtcTicks\tTraceEndUtcTicks", [$"{Ticks(traceStart)}\t{Ticks(traceEnd)}"]);
            IEnumerable<string> rows = gpu.GetEngineUtilization(traceStart, traceEnd)
                .Select(u => string.Join('\t', u.AdapterId, u.NodeId, u.BusyTime.Ticks, u.WindowDuration.Ticks, u.Utilization.ToString("R", CultureInfo.InvariantCulture)));
            WriteTsv(directory, "utilization_total.tsv", "AdapterId\tNodeId\tBusyTicks\tWindowTicks\tUtilization", rows);
        }

        Console.WriteLine($"[傾印] 已將 UseGpuData() 的全部清單寫入 {directory}");
    }

    private static void WriteTsv(string directory, string fileName, string header, IEnumerable<string> rows) =>
        File.WriteAllLines(Path.Combine(directory, fileName), new[] { header }.Concat(rows), new UTF8Encoding(false));
}
