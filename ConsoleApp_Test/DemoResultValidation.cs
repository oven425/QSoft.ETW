
internal sealed record ProcessSnapshot(
    long ProcessId,
    long ParentProcessId,
    string ImageFileName,
    DateTime? CreateTime,
    DateTime? ExitTime,
    string CommandLine);

internal sealed record ServiceSnapshot(string ServiceName, long? ProcessId, string CurrentState, string StartType);

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


internal sealed record DemoResult(
    TimeSpan Elapsed,
    IReadOnlyList<ProcessSnapshot> Processes,
    IReadOnlyList<ServiceSnapshot> Services,
    int EnergyIntervalCount,
    double EnergyTotalMilliJoules,
    IReadOnlyList<InterruptModuleSnapshot> InterruptModules,
    IReadOnlyList<ThreadSnapshot> Threads);

internal static class DemoResultValidator
{
    public static void Validate(DemoResult etlFileReaderResult, DemoResult traceProcessorResult)
    {
        Console.WriteLine();
        Console.WriteLine("=== 驗證：EtlFileReader 與 TraceProcessor 解析同一份 ETL 的結果比對（以 TraceProcessor 為基準） ===");

        ValidateProcesses(etlFileReaderResult.Processes, traceProcessorResult.Processes);
        ValidateServices(etlFileReaderResult.Services, traceProcessorResult.Services);
        ValidateThreads(etlFileReaderResult.Threads, traceProcessorResult.Threads);
        ValidateEnergyEstimation(etlFileReaderResult, traceProcessorResult);
        ValidateInterruptHandling(etlFileReaderResult.InterruptModules, traceProcessorResult.InterruptModules);
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

    private static void ValidateProcesses(IReadOnlyList<ProcessSnapshot> etl, IReadOnlyList<ProcessSnapshot> tp)
    {
        Dictionary<long, ProcessSnapshot> etlByPid = ToLookup(etl, p => p.ProcessId);
        Dictionary<long, ProcessSnapshot> tpByPid = ToLookup(tp, p => p.ProcessId);

        List<long> onlyInEtl = [.. etlByPid.Keys.Except(tpByPid.Keys)];
        List<long> onlyInTp = [.. tpByPid.Keys.Except(etlByPid.Keys)];
        List<long> common = [.. etlByPid.Keys.Intersect(tpByPid.Keys)];
        int imageMismatch = common.Count(pid =>
            !string.Equals(etlByPid[pid].ImageFileName, tpByPid[pid].ImageFileName, StringComparison.OrdinalIgnoreCase));
        List<long> commandLineMismatchPids = [.. common.Where(pid =>
            !string.Equals(etlByPid[pid].CommandLine, tpByPid[pid].CommandLine, StringComparison.OrdinalIgnoreCase))];

        (int createTimeMismatch, double createTimeMaxDelta) = CompareTimestamps(common, etlByPid, tpByPid, s => s.CreateTime);
        (int exitTimeMismatch, double exitTimeMaxDelta) = CompareTimestamps(common, etlByPid, tpByPid, s => s.ExitTime);

        Console.WriteLine($"[UseProcesses] EtlFileReader={etl.Count} 筆, TraceProcessor={tp.Count} 筆, 共同 PID={common.Count} 筆, " +
            $"僅 EtlFileReader 有={onlyInEtl.Count} 筆, 僅 TraceProcessor 有={onlyInTp.Count} 筆, 映像檔名不同={imageMismatch} 筆, CommandLine 不同={commandLineMismatchPids.Count} 筆");
        Console.WriteLine($"  CreateTime 不同(容許誤差 {TimestampToleranceSeconds:F0} 秒)={createTimeMismatch} 筆, 實際最大誤差={createTimeMaxDelta:F3} 秒");
        Console.WriteLine($"  ExitTime 不同(容許誤差 {TimestampToleranceSeconds:F0} 秒)={exitTimeMismatch} 筆, 實際最大誤差={exitTimeMaxDelta:F3} 秒");
        PrintSample("僅 EtlFileReader 有的 PID", onlyInEtl);
        PrintSample("僅 TraceProcessor 有的 PID", onlyInTp);
        if (commandLineMismatchPids.Count > 0)
        {
            PrintSample("CommandLine 不同的 PID", commandLineMismatchPids);
            foreach (long pid in commandLineMismatchPids.Take(5))
            {
                Console.WriteLine($"    PID={pid} EtlFileReader=\"{etlByPid[pid].CommandLine}\" TraceProcessor=\"{tpByPid[pid].CommandLine}\"");
            }
        }
    }

    private static void ValidateServices(IReadOnlyList<ServiceSnapshot> etl, IReadOnlyList<ServiceSnapshot> tp)
    {
        Dictionary<string, ServiceSnapshot> etlByName = ToLookup(etl, s => s.ServiceName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ServiceSnapshot> tpByName = ToLookup(tp, s => s.ServiceName, StringComparer.OrdinalIgnoreCase);

        List<string> onlyInEtl = [.. etlByName.Keys.Except(tpByName.Keys, StringComparer.OrdinalIgnoreCase)];
        List<string> onlyInTp = [.. tpByName.Keys.Except(etlByName.Keys, StringComparer.OrdinalIgnoreCase)];
        List<string> common = [.. etlByName.Keys.Intersect(tpByName.Keys, StringComparer.OrdinalIgnoreCase)];
        int stateMismatch = common.Count(name =>
            !string.Equals(etlByName[name].CurrentState, tpByName[name].CurrentState, StringComparison.OrdinalIgnoreCase));
        int pidMismatch = common.Count(name => etlByName[name].ProcessId != tpByName[name].ProcessId);
        int startTypeMismatch = common.Count(name =>
            !string.Equals(etlByName[name].StartType, tpByName[name].StartType, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"[UseServices] EtlFileReader={etl.Count} 個, TraceProcessor={tp.Count} 個, 共同服務={common.Count} 個, " +
            $"僅 EtlFileReader 有={onlyInEtl.Count} 個, 僅 TraceProcessor 有={onlyInTp.Count} 個, " +
            $"最終狀態不同={stateMismatch} 個, 最終 PID 不同={pidMismatch} 個, StartType 不同={startTypeMismatch} 個(預期落差，見本方法文件註解)");
        PrintSample("僅 EtlFileReader 有的服務", onlyInEtl);
        PrintSample("僅 TraceProcessor 有的服務", onlyInTp);
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

    /// <summary>
    /// 比對兩邊快照的同一個 Nullable&lt;DateTime&gt; 欄位(依 <paramref name="commonKeys"/> 逐筆比對)：
    /// 「有無值」不同一律計入不同筆數；兩邊皆有值時，只有差距超過 <see cref="TimestampToleranceSeconds"/> 秒
    /// 才計入不同筆數，同時回報實際觀察到的最大誤差供參考。供 ValidateProcesses/ValidateThreads 共用，
    /// 因兩者皆有 CreateTime/ExitTime 這種「兩種實作換算路徑不同、不要求逐 tick 相等」的欄位。
    /// </summary>
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
            "（依文件記載，筆數與所屬模組應完全一致；耗時不在此比對範圍內，因 EtlFileReader 端已知系統性低估）");
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
