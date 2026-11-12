using Microsoft.Windows.EventTracing.Events;
using QSoft.ETW;

/// <summary>
/// TraceProcessor 沒有提供 Adapter / Node / Segment / 記憶體用量 / 硬體執行區間的 API，
/// 所以改用它的 GenericEvents 解出 DxgKrnl 事件的原始欄位（由 TraceProcessor 依 manifest 解碼），
/// 再依相同規則重算一份清單，與 <c>UseGpuData()</c> 的結果比對，驗證 EtlFileReader 的事件解碼與彙整邏輯。
/// GenericEvents 需要 ETL 內嵌 provider manifest（WPR 擷取的檔案已內嵌；TraceSession 擷取的檔案請先執行
/// <c>xperf -merge 輸入.etl 輸出.etl</c>）。
/// </summary>
internal static partial class GpuValidation
{
    private static readonly HashSet<int> GenericEventIds = [17, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 78, 110, 116, 175, 177, 250, 259, 274, 367, 433, 471, 491, 509];

    /// <summary>TraceProcessor 解出的一筆 DxgKrnl 事件：整數欄位（含指標）與字串欄位。</summary>
    private sealed record MsDxgEvent(int Id, long Ticks, IReadOnlyDictionary<string, ulong> Numbers, string? Text)
    {
        public ulong Get(string name) => Numbers.TryGetValue(name, out ulong value)
            ? value
            : throw new InvalidOperationException($"TraceProcessor 解出的 DxgKrnl 事件 {Id} 沒有欄位 {name}");

        public ulong GetOrDefault(string name) => Numbers.GetValueOrDefault(name);
    }

    private sealed record MsDxgEvents(List<MsDxgEvent> Events, int UndecodedEventCount);

    private static MsDxgEvents SnapshotDxgEvents(IReadOnlyList<IGenericEvent> genericEvents)
    {
        List<MsDxgEvent> decoded = [];
        int undecoded = 0;
        foreach (IGenericEvent genericEvent in genericEvents)
        {
            if (genericEvent.ProviderId != TraceSessionBuilder.DxgKrnlProviderGuid || !GenericEventIds.Contains(genericEvent.Id))
            {
                continue;
            }

            if (genericEvent.Fields is null)
            {
                undecoded++;
                continue;
            }

            Dictionary<string, ulong> numbers = [];
            string? text = null;
            foreach (IGenericEventField field in genericEvent.Fields)
            {
                if (TryGetNumber(field, out ulong value))
                {
                    numbers[field.Name] = value;
                }
                else if (field.Type == GenericEventFieldType.String)
                {
                    text = field.AsString;
                }
            }

            decoded.Add(new MsDxgEvent(genericEvent.Id, genericEvent.Timestamp.DateTimeOffset.UtcTicks, numbers, text));
        }

        return new MsDxgEvents(decoded, undecoded);
    }

    private static bool TryGetNumber(IGenericEventField field, out ulong value)
    {
        switch (field.Type)
        {
            case GenericEventFieldType.Address:
                value = unchecked((ulong)field.AsAddress.Value);
                return true;
            case GenericEventFieldType.UInt64:
                value = field.AsUInt64;
                return true;
            case GenericEventFieldType.UInt32:
                value = field.AsUInt32;
                return true;
            case GenericEventFieldType.UInt16:
                value = field.AsUInt16;
                return true;
            case GenericEventFieldType.Byte:
                value = field.AsByte;
                return true;
            case GenericEventFieldType.Int64:
                value = unchecked((ulong)field.AsInt64);
                return true;
            case GenericEventFieldType.Int32:
                value = unchecked((uint)field.AsInt32);
                return true;
            case GenericEventFieldType.Boolean:
                value = field.AsBoolean ? 1UL : 0UL;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool CompareWithGenericEvents(IGpuDataSource gpu, MsDxgEvents ms)
    {
        Console.WriteLine();
        Console.WriteLine("--- 其他 GPU 資料：與 TraceProcessor GenericEvents 解出的 DxgKrnl 原始欄位交叉驗證 ---");
        if (ms.Events.Count == 0)
        {
            Console.WriteLine($"[略過] TraceProcessor 沒有解出任何 DxgKrnl 欄位（{ms.UndecodedEventCount} 筆事件缺少 provider manifest）。" +
                              "請用 WPR 擷取，或先執行 xperf -merge <輸入.etl> <輸出.etl> 內嵌 manifest 後再驗證。");
            return true;
        }

        List<MsDxgEvent> events = ms.Events;
        Console.WriteLine($"[GenericEvents] 解出 {events.Count} 筆相關的 DxgKrnl 事件（未能解碼 {ms.UndecodedEventCount} 筆）");

        // AdapterId：與 TraceProcessor 的 IDmaPacket.AdapterId 相同的編號規則（以合成事件黑盒驗證，見 AssignAdapterIds）。
        Dictionary<ulong, int> adapterIds = AssignAdapterIds(events, out Dictionary<MsDxgEvent, int> deviceAdapterIds);
        int AdapterIdOf(ulong address) => adapterIds.GetValueOrDefault(address);
        Dictionary<ulong, MsDxgEvent> adapterReports = events.Where(e => e.Id == 110)
            .GroupBy(e => e.Get("pDxgAdapter"))
            .ToDictionary(group => group.Key, group => group.First());

        bool allSame = true;

        allSame &= CompareSignatures(
            "Adapters",
            gpu.Adapters.Select(a => $"{a.AdapterId}|0x{a.AdapterAddress:X}|{unchecked((ulong)a.Luid)}|{a.VendorId}|{a.DeviceId}|{a.SubVendorId}|{a.SubSystemId}|{a.RevisionId}|{a.BusType}"),
            adapterIds.OrderBy(pair => pair.Value).Select(pair => adapterReports.TryGetValue(pair.Key, out MsDxgEvent? e)
                ? $"{pair.Value}|0x{pair.Key:X}|{e.Get("AdapterLuid")}|{e.Get("VendorID")}|{e.Get("DeviceID")}|{e.Get("SubVendorID")}|{e.Get("SubSystemID")}|{e.Get("RevisionID")}|{e.Get("BusType")}"
                : $"{pair.Value}|0x{pair.Key:X}|0|0|0|0|0|0|0"));

        allSame &= CompareSignatures(
            "Nodes",
            gpu.Nodes.Select(n => $"{n.AdapterId}|{n.NodeOrdinal}|{(uint)n.EngineType}|{n.FriendlyName}"),
            events.Where(e => e.Id == 250)
                .DistinctBy(e => (e.Get("pDxgAdapter"), e.Get("NodeOrdinal")))
                .Select(e => $"{AdapterIdOf(e.Get("pDxgAdapter"))}|{e.Get("NodeOrdinal")}|{e.Get("EngineType")}|{e.Text}"));

        allSame &= CompareSignatures(
            "Segments",
            gpu.Segments.Select(s => $"{s.AdapterId}|{s.SegmentId}|{s.Size}|{s.CommitLimit}|{s.RawFlags}|{(byte)s.Group}|{s.BaseAddress}|{s.CpuTranslatedAddress}|{s.NumberOfBanks}|{s.SystemMemoryEndAddress}"),
            events.Where(e => e.Id == 78)
                .DistinctBy(e => (e.Get("pDxgAdapter"), e.Get("ulSegmentId")))
                .Select(e => $"{AdapterIdOf(e.Get("pDxgAdapter"))}|{e.Get("ulSegmentId")}|{e.Get("Size")}|{e.Get("CommitLimit")}|{e.Get("Flags")}|{e.Get("MemorySegmentGroup")}|{e.Get("BaseAddress")}|{e.Get("CpuTranslatedAddress")}|{e.Get("NbOfBanks")}|{e.Get("SystemMemoryEndAddress")}"));

        allSame &= CompareSignatures(
            "SegmentResidency",
            gpu.SegmentResidencySamples.Select(r => $"{r.Timestamp.ToUniversalTime().Ticks}|{r.AdapterId}|{r.SegmentId}|{r.BytesResident}"),
            events.Where(e => e.Id == 274)
                .Select(e => $"{e.Ticks}|{AdapterIdOf(e.Get("DxgAdapter"))}|{e.Get("SegmentId")}|{e.Get("TotalBytesResident")}"));

        allSame &= CompareSignatures(
            "ProcessMemoryUsage",
            gpu.ProcessMemoryUsageChanges.Select(u => $"{u.Timestamp.ToUniversalTime().Ticks}|{u.AdapterId}|{u.ProcessId}|{u.PhysicalAdapterIndex}|{(byte)u.SegmentGroup}|{u.NewUsageBytes}|{u.OldUsageBytes}"),
            events.Where(e => e.Id == 367)
                .Select(e => $"{e.Ticks}|{AdapterIdOf(e.Get("pDxgAdapter"))}|{e.Get("ProcessId")}|{e.Get("PhysicalAdapterIndex")}|{e.Get("MemorySegmentGroup")}|{e.Get("NewUsage")}|{e.Get("OldUsage")}"));

        allSame &= CompareSignatures(
            "Performance",
            gpu.PerformanceSamples.Select(p => $"{p.Timestamp.ToUniversalTime().Ticks}|{unchecked((ulong)p.AdapterLuid)}|{p.PhysicalAdapterIndex}|{p.GpuMemoryFrequency}|{p.GpuPower}|{p.GpuTemperature}|{p.GpuFanRpm}|{p.EngineOrdinal}|{p.EngineFrequency}|{p.EngineVoltage}|{p.EngineMaxTransitionLatency}|{p.DdiQueryElapsedTimeMs}"),
            events.Where(e => e.Id == 509)
                .Select(e => $"{e.Ticks}|{e.GetOrDefault("AdapterLuid")}|{e.GetOrDefault("PhysicalAdapterIndex")}|{e.GetOrDefault("GpuMemoryFrequency")}|{e.GetOrDefault("GpuPower")}|{e.GetOrDefault("GpuTemperature")}|{e.GetOrDefault("GpuFanRPM")}|{e.GetOrDefault("EngineOrdinal")}|{e.GetOrDefault("EngineFrequency")}|{e.GetOrDefault("EngineVoltage")}|{e.GetOrDefault("EngineMaxTransitionLatency")}|{e.GetOrDefault("DdiQueryElapsedTimeInMs")}"));

        (List<string> executionSignatures, List<(int Adapter, ulong Node, long Start, long? End)> intervals, int unresolved) =
            BuildGenericExecutions(events, deviceAdapterIds);

        allSame &= CompareSignatures(
            "EngineExecutions",
            gpu.EngineExecutions.Select(e => string.Join('|',
                e.ContextHandle, e.SubmissionId, (uint)e.PacketType, e.SubmitTime.ToUniversalTime().Ticks,
                e.CompleteTime is { } complete ? complete.ToUniversalTime().Ticks : "",
                e.InterruptType is { } interrupt ? (uint)interrupt : "",
                e.AdapterId, e.NodeOrdinal, e.EngineAffinity, e.ProcessId?.ToString() ?? "")),
            executionSignatures);

        bool unresolvedSame = unresolved == gpu.UnresolvedDmaSubmitCount;
        Console.WriteLine($"[UnresolvedDmaSubmit] EtlFileReader={gpu.UnresolvedDmaSubmitCount} 筆, TraceProcessor(GenericEvents)={unresolved} 筆{(unresolvedSame ? "" : "（不同）")}");
        allSame &= unresolvedSame;

        allSame &= CompareUtilization(gpu, intervals);
        return allSame;
    }

    private static bool CompareSignatures(string category, IEnumerable<string> etlFileReader, IEnumerable<string> traceProcessor)
    {
        Dictionary<string, int> balance = [];
        int etlCount = 0;
        int msCount = 0;
        foreach (string row in etlFileReader)
        {
            etlCount++;
            balance[row] = balance.GetValueOrDefault(row) + 1;
        }

        foreach (string row in traceProcessor)
        {
            msCount++;
            balance[row] = balance.GetValueOrDefault(row) - 1;
        }

        int onlyEtlFileReader = balance.Values.Where(count => count > 0).Sum();
        int onlyTraceProcessor = -balance.Values.Where(count => count < 0).Sum();
        Console.WriteLine($"[{category}] EtlFileReader={etlCount} 筆, TraceProcessor(GenericEvents)={msCount} 筆, " +
                          $"內容完全相同={etlCount - onlyEtlFileReader} 筆, 只在 EtlFileReader={onlyEtlFileReader} 筆, 只在 TraceProcessor={onlyTraceProcessor} 筆");

        if (onlyEtlFileReader == 0 && onlyTraceProcessor == 0)
        {
            return true;
        }

        foreach ((string row, int count) in balance.Where(pair => pair.Value != 0).Take(MaxDifferenceSamplesToPrint).Select(pair => (pair.Key, pair.Value)))
        {
            Console.WriteLine($"    {(count > 0 ? "只在 EtlFileReader" : "只在 TraceProcessor")} x{Math.Abs(count)}: {row}");
        }

        return false;
    }

    /// <summary>
    /// TraceProcessor 的 AdapterId 編號規則：從 1 開始，依「Adapter 指標第一次出現在特定事件」的時間順序指派
    /// （17、23、24、25、26、27、28、110、116、259；29 只有在 Device handle 是新的時候才算）。
    /// 25（DestroyAdapter）會刪除該指標目前的 Adapter 物件，所以之後再次註冊同一個指標會得到新的編號；
    /// Device（27 / 29）會固定使用建立當下的 Adapter 物件（<paramref name="deviceAdapterIds"/>），傳回值則是每個指標最新一個物件的編號。
    /// 只在 250 / 78 事件出現的 Adapter 沒有編號，這裡比照 EtlFileReader 接在後面補號，讓其他資料仍可比對。
    /// </summary>
    private static Dictionary<ulong, int> AssignAdapterIds(List<MsDxgEvent> events, out Dictionary<MsDxgEvent, int> deviceAdapterIds)
    {
        Dictionary<ulong, int> latestIds = [];
        Dictionary<ulong, int> liveAdapters = [];
        Dictionary<ulong, int> deviceAdapters = [];
        Dictionary<MsDxgEvent, int> deviceEventAdapters = new(ReferenceEqualityComparer.Instance);
        int counter = 0;

        int Register(ulong address)
        {
            if (address == 0)
            {
                return 0;
            }

            if (!liveAdapters.TryGetValue(address, out int id))
            {
                id = ++counter;
                liveAdapters.Add(address, id);
                latestIds[address] = id;
            }

            return id;
        }

        foreach (MsDxgEvent e in events.OrderBy(e => e.Ticks))
        {
            switch (e.Id)
            {
                case 17 or 23 or 24 or 26 or 110 or 116 or 259:
                    Register(e.Get("pDxgAdapter"));
                    break;

                case 25:
                    ulong destroyed = e.Get("pDxgAdapter");
                    Register(destroyed);
                    liveAdapters.Remove(destroyed);
                    break;

                case 27:
                    int created = Register(e.Get("pDxgAdapter"));
                    deviceAdapters[e.Get("hDevice")] = created;
                    deviceEventAdapters[e] = created;
                    break;

                case 28:
                    deviceAdapters.Remove(e.Get("hDevice"));
                    Register(e.Get("pDxgAdapter"));
                    break;

                case 29:
                    ulong hDevice = e.Get("hDevice");
                    if (deviceAdapters.TryGetValue(hDevice, out int adapterOfDevice))
                    {
                        adapterOfDevice = liveAdapters.GetValueOrDefault(e.Get("pDxgAdapter"), adapterOfDevice);
                    }
                    else
                    {
                        adapterOfDevice = Register(e.Get("pDxgAdapter"));
                    }

                    deviceAdapters[hDevice] = adapterOfDevice;
                    deviceEventAdapters[e] = adapterOfDevice;
                    break;
            }
        }

        foreach (MsDxgEvent e in events.Where(e => e.Id is 250 or 78))
        {
            ulong address = e.Get("pDxgAdapter");
            if (address != 0 && !latestIds.ContainsKey(address))
            {
                latestIds.Add(address, ++counter);
            }
        }

        deviceAdapterIds = deviceEventAdapters;
        return latestIds;
    }

    private readonly record struct MsContext(ulong AdapterAddress, int AdapterId, ulong Node, ulong Engine, ulong ProcessId, bool HasProcess);

    private readonly record struct MsDevice(ulong AdapterAddress, int AdapterId, ulong ProcessId, bool HasProcess);

    /// <summary>
    /// 以 TraceProcessor 解出的欄位重播 Process / Device / Context 的建立與刪除，再把每個 DMA submit（175）配上完成中斷（177），
    /// 產生與 <see cref="IGpuDataSource.EngineExecutions"/> 同格式的簽章，以及各 Node 的忙碌區間。
    /// </summary>
    private static (List<string> Signatures, List<(int Adapter, ulong Node, long Start, long? End)> Intervals, int Unresolved) BuildGenericExecutions(
        List<MsDxgEvent> events,
        Dictionary<MsDxgEvent, int> deviceAdapterIds)
    {
        static int Order(int id) => id switch
        {
            471 or 491 => 0,
            27 or 29 => 1,
            28 => 2,
            30 or 32 => 3,
            31 => 4,
            433 => 5,
            175 => 6,
            _ => 7,
        };

        MsDxgEvent[] timeline = [.. events
            .Select((e, index) => (Event: e, Index: index))
            .Where(item => Order(item.Event.Id) < 7)
            .OrderBy(item => item.Event.Ticks)
            .ThenBy(item => Order(item.Event.Id))
            .ThenBy(item => item.Index)
            .Select(item => item.Event)];

        // 同一個 (Context, SubmissionId) 可能有多筆完成中斷：每筆 DMA submit 取「它之後的第一筆」。
        Dictionary<(ulong HContext, ulong Id), List<MsDxgEvent>> isrByKey = [];
        foreach (MsDxgEvent isr in events.Where(e => e.Id == 177).OrderBy(e => e.Ticks))
        {
            (ulong, ulong) key = (isr.Get("hContext"), isr.Get("uliCompletionId"));
            if (!isrByKey.TryGetValue(key, out List<MsDxgEvent>? sameKey))
            {
                isrByKey.Add(key, sameKey = []);
            }

            sameKey.Add(isr);
        }

        Dictionary<ulong, ulong> dxgProcesses = [];
        Dictionary<ulong, MsDevice> devices = [];
        Dictionary<ulong, MsContext> live = [];
        Dictionary<ulong, MsContext> everSeen = [];
        List<(MsDxgEvent Submit, MsContext? Context)> submits = [];

        void Bind(ulong handle, MsContext context)
        {
            if (handle == 0)
            {
                return;
            }

            live[handle] = context;
            everSeen.TryAdd(handle, context);
        }

        foreach (MsDxgEvent e in timeline)
        {
            switch (e.Id)
            {
                case 471 or 491:
                    dxgProcesses[e.Get("DxgProcess")] = e.Get("ProcessId");
                    break;

                case 27 or 29:
                    ulong hProcessId = e.Get("hProcessId");
                    ulong deviceAdapter = e.Get("pDxgAdapter");
                    int deviceAdapterId = deviceAdapterIds.GetValueOrDefault(e);
                    devices[e.Get("hDevice")] = dxgProcesses.TryGetValue(e.Get("DxgProcess"), out ulong dxgProcessId)
                        ? new MsDevice(deviceAdapter, deviceAdapterId, dxgProcessId, true)
                        : new MsDevice(deviceAdapter, deviceAdapterId, hProcessId, hProcessId != 0);
                    break;

                case 28:
                    devices.Remove(e.Get("hDevice"));
                    break;

                case 30 or 32:
                    devices.TryGetValue(e.Get("hDevice"), out MsDevice device);
                    MsContext created = new(
                        device.AdapterAddress, device.AdapterId, e.Get("NodeOrdinal"), e.Get("EngineAffinity"), device.ProcessId, device.HasProcess);
                    Bind(e.Get("hContext"), created);
                    Bind(e.Get("ContextHandle"), created);
                    break;

                case 31:
                    live.Remove(e.Get("hContext"));
                    live.Remove(e.Get("ContextHandle"));
                    break;

                case 433:
                    if (live.TryGetValue(e.Get("pDxgObject"), out MsContext associated))
                    {
                        Bind(e.Get("pSchObject"), associated);
                        Bind(e.Get("hKmdHandle"), associated);
                        Bind(e.Get("hOsHandle"), associated);
                    }

                    break;

                case 175:
                    submits.Add((e, live.TryGetValue(e.Get("hContext"), out MsContext resolved) ? resolved : null));
                    break;
            }
        }

        List<string> signatures = [];
        List<(int Adapter, ulong Node, long Start, long? End)> intervals = [];
        int unresolved = 0;
        foreach ((MsDxgEvent submit, MsContext? contextAtSubmit) in submits)
        {
            MsContext? context = contextAtSubmit;
            if (context is null && everSeen.TryGetValue(submit.Get("hContext"), out MsContext fallback))
            {
                context = fallback;
            }

            // Context 所屬的 Device 也要找得到（AdapterAddress 不為 0），否則 TraceProcessor 不會產生 packet。
            if (context is not { AdapterAddress: not 0 } resolvedContext)
            {
                unresolved++;
                continue;
            }

            ulong hContext = submit.Get("hContext");
            ulong submissionId = submit.Get("uliSubmissionId");
            MsDxgEvent? complete = isrByKey.TryGetValue((hContext, submissionId), out List<MsDxgEvent>? sameKeyIsrs)
                ? sameKeyIsrs.FirstOrDefault(isr => isr.Ticks >= submit.Ticks)
                : null;
            int adapterId = resolvedContext.AdapterId;

            signatures.Add(string.Join('|',
                hContext, submissionId, submit.Get("PacketType"), submit.Ticks,
                complete is null ? "" : complete.Ticks,
                complete is null ? "" : complete.Get("InterruptType"),
                adapterId, resolvedContext.Node, resolvedContext.Engine,
                resolvedContext.HasProcess ? resolvedContext.ProcessId : ""));
            intervals.Add((adapterId, resolvedContext.Node, submit.Ticks, complete?.Ticks));
        }

        return (signatures, intervals, unresolved);
    }

    /// <summary>以 TraceProcessor 解出的 DMA submit / 完成中斷區間重算各 Node 的忙碌時間，比對 <see cref="IGpuDataSource.GetEngineUtilization"/>。</summary>
    private static bool CompareUtilization(IGpuDataSource gpu, List<(int Adapter, ulong Node, long Start, long? End)> intervals)
    {
        if (gpu.TraceStartTime is not { } traceStart || gpu.TraceEndTime is not { } traceEnd || traceEnd <= traceStart)
        {
            Console.WriteLine("[Utilization] 缺少追蹤起訖時間，略過。");
            return true;
        }

        long windowStart = traceStart.ToUniversalTime().Ticks;
        long windowEnd = traceEnd.ToUniversalTime().Ticks;
        Dictionary<(int Adapter, ulong Node), long> expectedBusy = [];
        foreach (var group in intervals.GroupBy(item => (item.Adapter, item.Node)))
        {
            List<(long Start, long End)> merged = [];
            foreach (var item in group.OrderBy(item => item.Start))
            {
                long end = Math.Max(item.End ?? windowEnd, item.Start);
                if (merged.Count > 0 && item.Start <= merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
                }
                else
                {
                    merged.Add((item.Start, end));
                }
            }

            expectedBusy[group.Key] = merged.Sum(interval =>
                Math.Max(0, Math.Min(interval.End, windowEnd) - Math.Max(interval.Start, windowStart)));
        }

        int compared = 0;
        int different = 0;
        foreach (GpuEngineUtilization utilization in gpu.GetEngineUtilization(traceStart, traceEnd))
        {
            compared++;
            long expected = expectedBusy.GetValueOrDefault((utilization.AdapterId, (ulong)utilization.NodeOrdinal));
            if (utilization.BusyTime.Ticks != expected)
            {
                different++;
                if (different <= MaxDifferenceSamplesToPrint)
                {
                    Console.WriteLine($"    Utilization 不同 Adapter {utilization.AdapterId} Node {utilization.NodeId}: EtlFileReader={utilization.BusyTime.Ticks} ticks, TraceProcessor(GenericEvents)={expected} ticks");
                }
            }
        }

        Console.WriteLine($"[Utilization] 比對 {compared} 個 Node 的忙碌時間（整段追蹤），內容不同={different} 個");
        return different == 0;
    }
}
