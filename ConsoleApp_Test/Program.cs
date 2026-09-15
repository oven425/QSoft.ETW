using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Services;
using QSoft.ETW;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

Console.OutputEncoding = Encoding.UTF8;
var pppps = TdhManifestMapReader.QueryManifestEventInfo(Guid.Parse("{65cd4c8a-0848-4583-92a0-31c0fbaf00c0}"));
//var ees = TdhManifestMapReader.QueryEnergyEstimateEventInfo();
int durationSeconds = 30;
if (args.Length > 0 && int.TryParse(args[0], out int parsedSeconds) && parsedSeconds > 0)
{
    durationSeconds = parsedSeconds;
}

//string? capturedEtlPath = await RunTraceSessionCaptureDemo(durationSeconds);
//if (capturedEtlPath is null)
//{
//    return 1;
//}
var capturedEtlPath = args.Length > 0 && File.Exists(args[0])
    ? args[0]
    : "capture_20260909_092700.etl";

long etlFileSizeBytes = new FileInfo(capturedEtlPath).Length;
double etlFileSizeMb = etlFileSizeBytes / 1024.0 / 1024.0;
Console.WriteLine($"ETL 檔案大小: {etlFileSizeMb:F2} MB ({capturedEtlPath})");

DemoResult_Etl etlFileReaderResult = RunEtlFileReaderDemo(capturedEtlPath);
DemoResult_Tp traceProcessorResult = RunTraceProcessorDemo(capturedEtlPath);

Console.WriteLine();
Console.WriteLine("=== 效能比較總結（同一份 ETL，兩種解析方式）===");
Console.WriteLine($"ETL 檔案大小: {etlFileSizeMb:F2} MB");
Console.WriteLine($"EtlFileReader ProcessFile() 耗時: {etlFileReaderResult.Elapsed.TotalMilliseconds:F1} ms（{etlFileSizeMb / etlFileReaderResult.Elapsed.TotalSeconds:F2} MB/s）");
Console.WriteLine($"TraceProcessor trace.Process() 耗時: {traceProcessorResult.Elapsed.TotalMilliseconds:F1} ms（{etlFileSizeMb / traceProcessorResult.Elapsed.TotalSeconds:F2} MB/s）");
if (etlFileReaderResult.Elapsed < traceProcessorResult.Elapsed)
{
    Console.WriteLine($"EtlFileReader 較快，約為 TraceProcessor 的 {traceProcessorResult.Elapsed.TotalMilliseconds / etlFileReaderResult.Elapsed.TotalMilliseconds:F2} 倍速度" +
        "（但 TraceProcessor 同時多做了跨程序符號解析等 EtlFileReader 未實作的工作，非完全對等負載）。");
}
else
{
    Console.WriteLine($"TraceProcessor 較快，約為 EtlFileReader 的 {etlFileReaderResult.Elapsed.TotalMilliseconds / traceProcessorResult.Elapsed.TotalMilliseconds:F2} 倍速度。");
}

DemoResultValidator.Validate(etlFileReaderResult, traceProcessorResult);

return 0;

/// <summary>
/// 將 EtlFileReader 內部以 ulong/uint 儲存的原始位址/遮罩值轉成 long/int，僅重新詮釋位元樣式(unchecked)、
/// 不做數值判斷，才能與 TraceProcessor 對應屬性(long/int)用 <see cref="ThreadSnapshot"/> 直接比較。
/// 詳見 <see cref="ThreadSnapshot"/> 型別文件註解。
/// </summary>
static long? ToSignedAddress(ulong? value) => value.HasValue ? unchecked((long)value.Value) : null;
static int? ToSignedId(uint? value) => value.HasValue ? unchecked((int)value.Value) : null;

/// <summary>
/// Console 示範預設不載入 PDB、不連線。設定 QSOFT_ETW_SYMBOL_PATH 可使用自訂（通常為本機）symbol path；
/// 或設定 QSOFT_ETW_MICROSOFT_SYMBOL_CACHE 明確允許由 Microsoft Symbol Server 下載到指定快取目錄。
/// QSOFT_ETW_IMAGE_PATH 可用分號指定額外的映像搜尋目錄。
/// </summary>
static DbgHelpSymbolResolutionOptions? CreateDbgHelpOptionsFromEnvironment()
{
    string? symbolPath = Environment.GetEnvironmentVariable("QSOFT_ETW_SYMBOL_PATH");
    string? microsoftSymbolCache = Environment.GetEnvironmentVariable("QSOFT_ETW_MICROSOFT_SYMBOL_CACHE");
    if (!string.IsNullOrWhiteSpace(symbolPath) && !string.IsNullOrWhiteSpace(microsoftSymbolCache))
    {
        throw new InvalidOperationException(
            "QSOFT_ETW_SYMBOL_PATH 與 QSOFT_ETW_MICROSOFT_SYMBOL_CACHE 不可同時設定。");
    }

    string? imagePath = Environment.GetEnvironmentVariable("QSOFT_ETW_IMAGE_PATH");
    string[] imageSearchDirectories = string.IsNullOrWhiteSpace(imagePath)
        ? []
        : imagePath
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    if (!string.IsNullOrWhiteSpace(microsoftSymbolCache))
    {
        return DbgHelpSymbolResolutionOptions.CreateWithMicrosoftSymbolServer(
            microsoftSymbolCache,
            imageSearchDirectories);
    }

    return string.IsNullOrWhiteSpace(symbolPath)
        ? null
        : DbgHelpSymbolResolutionOptions.CreateLocalOnly(symbolPath, imageSearchDirectories);
}

/// <summary>
/// 前置作業：本專案 QSoft.ETW 的 <see cref="TraceSession"/>（由 <see cref="TraceSessionBuilder"/> 組裝設定）。
/// 內部以 P/Invoke 直接呼叫 Advapi32 的 StartTraceW/EnableTraceEx2/ControlTraceW 等「ETW Controller API」，
/// 建立並啟停一個 Kernel+User 混合 Session，將事件即時寫入磁碟成同一份 ETL 檔——這一步只負責「錄製」，
/// 不涉及事後解析；錄好的 ETL 才會分別交給下面的 <see cref="RunEtlFileReaderDemo"/> 與
/// <see cref="RunTraceProcessorDemo"/> 解析比較。獨立成方法是為了和另外兩個 RunXxxDemo() 並列，
/// 讓「錄製」與「解析」兩種職責在結構上一目了然（純粹重構自原本寫在頂層陳述式的程式碼，行為不變）。
/// </summary>
static async Task<string?> RunTraceSessionCaptureDemo(int durationSeconds)
{
    Console.WriteLine();
    Console.WriteLine("=== 前置作業：TraceSession（本專案 P/Invoke 直接呼叫 StartTraceW/ControlTraceW 控制 ETW Session） ===");

    using TraceSession session = new TraceSessionBuilder()
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS)
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS_COUNTERS)
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_IMAGE_LOAD)
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_CSWITCH)
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_THREAD)
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_INTERRUPT)
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROFILE)
        .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DPC)
        .WithProvider(TraceSessionBuilder.WmiActivityProviderGuid)
        .WithProvider(TraceSessionBuilder.ServicesProviderGuid)
        .WithProvider(TraceSessionBuilder.ServicesSvchostProviderGuid)
        .WithProvider(TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
        .WithProvider(TraceSessionBuilder.KernelAcpiProviderGuid)
        .WithProvider(TraceSessionBuilder.KernelPowerProviderGuid)
        .WithSystemProvider(
            TraceSessionBuilder.SystemMemoryProviderGuid,
            TraceSessionBuilder.SystemMemoryMemoryInfoKeyword |
            TraceSessionBuilder.SystemMemoryWorkingSetKeyword |
            TraceSessionBuilder.SystemMemoryVirtualAllocKeyword)
        .WithProvider(TraceSessionBuilder.PowerMeterPollingProviderGuid, TraceSessionBuilder.PowerMeterPollingFiveSecondKeyword)
        .WithProvider(TraceSessionBuilder.DxgKrnlProviderGuid)
        .WithProvider(TraceSessionBuilder.KernelMemoryProviderGuid, TraceSessionBuilder.KernelMemoryMemInfoKeyword)
        .WithOutputPath(Path.Combine(AppContext.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.etl"))
        .WithEtwFileCompression()
        .Build();

    if (!session.IsElevated())
    {
        Console.Error.WriteLine("此程式需要以系統管理員身分執行才能啟動 ETW Kernel/User Trace。");
        return null;
    }

    Console.CancelKeyPress += (_, e) =>
    {
        // 攔截 Ctrl+C，避免行程直接終止導致 finally 未執行、Kernel Logger 未停止而變成孤兒 session。
        e.Cancel = true;
        session.Stop();
        Environment.Exit(1);
    };

    try
    {
        session.Start();
        Console.WriteLine($"追蹤中，將持續 {durationSeconds} 秒...");
        await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"執行失敗: {ex.Message}");
        return null;
    }
    finally
    {
        session.Stop();
    }

    // === ETL 蒐集完成，交還路徑給呼叫端分別以 EtlFileReader / TraceProcessor 解析同一份檔案 ===
    string capturedEtlPath = session.LogFilePath ?? string.Empty;
    if (string.IsNullOrEmpty(capturedEtlPath) || !File.Exists(capturedEtlPath))
    {
        Console.Error.WriteLine("找不到剛完成擷取的 ETL 檔案，略過解析示範。");
        return null;
    }

    return capturedEtlPath;
}

static DemoResult_Etl RunEtlFileReaderDemo(string etlFilePath)
{
    Console.WriteLine();
    Console.WriteLine("=== 方式一：EtlFileReader（本專案手刻 P/Invoke + TDH 解析） ===");

    int processStartCount = 0;
    int imageLoadCount = 0;
    int cswitchCount = 0;
    int profileSampleCount = 0;
    int wmiActivityCount = 0;

    EtlFileReader reader = new();
    reader.UseBuiltInAnalysis();
    var pendingProcesses = reader.UseProcesses();
    var pendingServices = reader.UseServices();
    var pendingThreads = reader.UseThreads();
    var pendingEnergy = reader.UseEnergyEstimationData();
    var pendingEnergyMeter = reader.UseEnergyMeterData();
    DbgHelpSymbolResolutionOptions? symbolOptions = CreateDbgHelpOptionsFromEnvironment();
    var pendingInterruptHandling = symbolOptions is null
        ? reader.UseInterruptHandlingData()
        : reader.UseInterruptHandlingData(symbolOptions);
    var pendingMemoryUtilization = reader.UseMemoryUtilizationData();
    reader.ProcessStart += (in ProcessInfo info) => processStartCount++;
    reader.ImageLoad += (in ImageLoadEventInfo info) => imageLoadCount++;
    reader.ThreadCSwitch += (in CSwitchEventInfo info) => cswitchCount++;
    reader.PerfInfoProfile += info => profileSampleCount++;
    reader.WmiActivity_17 += (in WmiActivityEventInfo_17 info) => wmiActivityCount++;

    long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
    Stopwatch stopwatch = Stopwatch.StartNew();
    reader.ProcessFile(etlFilePath);
    stopwatch.Stop();
    long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

    Console.WriteLine($"[效能] ProcessFile() 耗時: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    Console.WriteLine($"[效能] 原始事件總數: {reader.TotalEventCount:N0} 筆（{reader.TotalEventCount / stopwatch.Elapsed.TotalSeconds:N0} events/sec）");
    Console.WriteLine($"[效能] 解析後受管記憶體增量: {(memoryAfter - memoryBefore) / 1024.0 / 1024.0:F2} MB");

    Console.WriteLine($"程序啟動(ProcessStart): {processStartCount} 筆");
    Console.WriteLine($"模組載入(ImageLoad): {imageLoadCount} 筆");
    Console.WriteLine($"CPU Context Switch: {cswitchCount} 筆（須自行依 Processor/Timestamp 配對前後兩筆事件，才能算出每個程序的 CPU 時間）");
    Console.WriteLine($"CPU 取樣(Profile): {profileSampleCount} 筆（須自行以 InstructionPointer 反查模組，才能得到熱點模組名稱）");
    Console.WriteLine($"WMI-Activity(EventId=17): {wmiActivityCount} 筆（其餘十餘種 WmiActivity 事件 ID 都各自需要專屬的 struct + event，此處僅示範其一）");

    // === 以下改用內建關聯引擎產出的 Result，直接對照 RunTraceProcessorDemo 的輸出 ===
    EtlReadResult result = reader.Result;
    EtlAnalysisResult analysis = result.Analysis!;

    Console.WriteLine();
    IReadOnlyList<ProcessRecord> processes = pendingProcesses.Result.Processes;
    Console.WriteLine($"[Result] 程序數: {processes.Count} 個（ProcessRecord 已含 PID、映像檔名、命令列、父程序、存續時間，免自行組裝）");
    Console.WriteLine($"[Result] 動態服務數: {pendingServices.Result.Services.Count} 個，設定快照數: {pendingServices.Result.Snapshots.Count} 個" +
        "（完整對應 IServiceDataSource 的 Services 與 Snapshots）");
    foreach (ServiceRecord svc in pendingServices.Result.Services.Take(5))
    {
        Console.WriteLine($"  {svc.ServiceName} ({svc.ImageFileName}): PID={svc.ProcessId?.ToString() ?? "?"} 狀態={svc.CurrentState}");
    }

    IReadOnlyList<ThreadRecord> threads = pendingThreads.Result.Threads;
    int namedThreadCount = threads.Count(t => !string.IsNullOrEmpty(t.Name));
    Console.WriteLine($"[Result] 執行緒數: {threads.Count} 個（ThreadRecord 對應 TraceProcessor 的 IThread，含建立/結束時間、堆疊範圍、優先權、可執行處理器、啟動位址與名稱），其中具名 {namedThreadCount} 個");
    foreach (ThreadRecord t in threads.Where(t => !string.IsNullOrEmpty(t.Name)).Take(5))
    {
        Console.WriteLine($"  TID={t.ThreadId} PID={t.ProcessId} Name=\"{t.Name}\" BasePriority={t.BasePriority} PagePriority={t.PagePriority} IoPriority={t.IoPriority} StartAddress模組={t.StartAddressModuleName}+0x{t.StartAddressModuleOffset:X}");
    }

    IReadOnlyList<EnergyEstimationInterval> energyIntervals = pendingEnergy.Result.Intervals;
    Console.WriteLine($"[Result] Energy Estimation 週期數: {energyIntervals.Count} 筆（EnergyEstimationInterval 每筆對應一次 E3 估算週期，含 CPU/Disk/Display/Network/SoC 等分項能耗）");
    var topEnergyConsumers = energyIntervals
        .GroupBy(interval => interval.ProcessId)
        .Select(group => new
        {
            ProcessId = group.Key,
            ImageName = group.First().ImageName,
            TotalMilliJoules = group.Sum(interval => (double)interval.TotalEnergy),
        })
        .OrderByDescending(item => item.TotalMilliJoules)
        .Take(5);

    Console.WriteLine("[Result] 估計耗能前 5 名（依 ProcessId 加總 TotalEnergy）：");
    foreach (var item in topEnergyConsumers)
    {
        Console.WriteLine($"  PID={item.ProcessId} {item.ImageName}: {item.TotalMilliJoules:F1} mJ");
    }

    IReadOnlyList<EnergyMeasurementInterval> energyMeterIntervals = pendingEnergyMeter.Result.Intervals;
    Console.WriteLine($"[Result] Energy Meter(EMI)區間數: {energyMeterIntervals.Count} 筆（EnergyMeasurementInterval 每筆對應硬體計量通道相鄰兩次讀數之間的耗能，僅在裝置支援 EMI 時才會有資料）");
    var topEnergyMeterChannels = energyMeterIntervals
        .GroupBy(interval => interval.ChannelName)
        .Select(group => new
        {
            ChannelName = group.Key,
            TotalMilliJoules = group.Sum(interval => interval.TotalEnergy),
        })
        .OrderByDescending(item => item.TotalMilliJoules)
        .Take(5);

    Console.WriteLine("[Result] 硬體電錶耗能前 5 個通道（依 ChannelName 加總 TotalEnergy）：");
    foreach (var item in topEnergyMeterChannels)
    {
        Console.WriteLine($"  {item.ChannelName}: {item.TotalMilliJoules:F1} mJ");
    }

    Console.WriteLine("[Result] 估計 CPU 執行時間前 5 名（由內建引擎配對 CSwitch New/Old 累加而得）：");
    foreach (EtlProcessCpuSummary cpu in analysis.ProcessCpuSummaries.Take(5))
    {
        Console.WriteLine($"  PID={cpu.ProcessId} {cpu.ImageFileName}: {cpu.EstimatedExecutionTime.TotalMilliseconds:F3} ms");
    }

    Console.WriteLine("[Result] CPU 取樣熱點前 5 名（由內建引擎以 InstructionPointer 反查所屬模組/RVA）：");
    foreach (AddressSampleSummary hotspot in analysis.ProfileHotspots.Take(5))
    {
        Console.WriteLine($"  0x{hotspot.Address:X} {hotspot.ModuleName}+0x{hotspot.ModuleRelativeAddress?.ToString("X") ?? "?"}: {hotspot.SampleCount} 次");
    }

    if (analysis.DataQualityWarnings.Count > 0)
    {
        Console.WriteLine("[Result] 資料品質警告：");
        foreach (string warning in analysis.DataQualityWarnings)
        {
            Console.WriteLine($"  - {warning}");
        }
    }

    // === UseInterruptHandlingData()：對應 TraceProcessor 的 trace.UseInterruptHandlingData() ===
    IReadOnlyList<InterruptServiceRoutineInterval> isrIntervals = pendingInterruptHandling.Result.InterruptServiceRoutines;
    IReadOnlyList<DeferredProcedureCallInterval> dpcIntervals = pendingInterruptHandling.Result.DeferredProcedureCalls;
    Console.WriteLine();
    Console.WriteLine($"[Result] ISR 區間數: {isrIntervals.Count} 筆，DPC 區間數: {dpcIntervals.Count} 筆" +
        "（IInterruptServiceRoutineInterval/IDeferredProcedureCallInterval 對等，StartTime 由 payload InitialTime 依 ETL clock type 直接換算）");
    IEnumerable<HandlerStackFrameInfo> handlerFrames = isrIntervals
        .Select(interval => interval.HandlerStackFrame)
        .Concat(dpcIntervals.Select(interval => interval.HandlerStackFrame))
        .OfType<HandlerStackFrameInfo>();
    Console.WriteLine("[Result] HandlerStackFrame 狀態：" +
        string.Join(", ", handlerFrames
            .GroupBy(frame => frame.SymbolResolutionStatus)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}={group.Count()}")));
    foreach (HandlerStackFrameInfo frame in handlerFrames
        .Where(frame => frame.SymbolResolutionStatus == HandlerSymbolResolutionStatus.Resolved)
        .DistinctBy(frame => (frame.ModuleName, frame.Address))
        .Take(5))
    {
        Console.WriteLine(
            $"  {Path.GetFileName(frame.ModuleName)}!{frame.SymbolName}+0x{frame.SymbolDisplacement:X} " +
            $"(RVA=0x{frame.RelativeVirtualAddress:X}, VA=0x{frame.Address:X})");
    }

    var isrByModule = isrIntervals
        .GroupBy(isr => isr.HandlerModuleName)
        .Select(group => new
        {
            Module = group.Key,
            Count = group.Count(),
            TotalMilliseconds = group.Sum(item => item.DurationMSec),
        })
        .OrderByDescending(item => item.TotalMilliseconds)
        .Take(5);

    Console.WriteLine("[Result] ISR 執行時間前 5 名模組（依 HandlerModuleName 分組）：");
    foreach (var item in isrByModule)
    {
        Console.WriteLine($"  {item.Module}: {item.Count} 次，共 {item.TotalMilliseconds:F3} ms");
    }

    var dpcByModule = dpcIntervals
        .GroupBy(dpc => dpc.HandlerModuleName)
        .Select(group => new
        {
            Module = group.Key,
            Count = group.Count(),
            TotalMilliseconds = group.Sum(item => item.DurationMSec),
        })
        .OrderByDescending(item => item.TotalMilliseconds)
        .Take(5);

    Console.WriteLine("[Result] DPC 執行時間前 5 名模組（依 HandlerModuleName 分組）：");
    foreach (var item in dpcByModule)
    {
        Console.WriteLine($"  {item.Module}: {item.Count} 次，共 {item.TotalMilliseconds:F3} ms");
    }

    var dpcByType = dpcIntervals
        .GroupBy(dpc => dpc.CallType == EtlDeferredProcedureCallType.ThreadedDpc ? EtlDeferredProcedureCallType.Normal : dpc.CallType)
        .Select(group => new { CallType = group.Key, Count = group.Count() });

    Console.WriteLine("[Result] DPC 依 CallType 統計（ThreadedDpc 已併入 Normal，才能與 TraceProcessor 對等比較）：");
    foreach (var item in dpcByType)
    {
        Console.WriteLine($"  {item.CallType}: {item.Count} 次");
    }

    // === UseMemoryUtilizationData()：對應 TraceProcessor 的 trace.UseMemoryUtilizationData() ===
    IReadOnlyList<MemoryUtilizationEventInfo> memorySnapshots = pendingMemoryUtilization.Result.Snapshots;
    Console.WriteLine($"[Result] 系統記憶體用量快照數: {memorySnapshots.Count} 筆" +
        "（對應 IMemoryUtilizationSnapshot，來源為 Microsoft-Windows-Kernel-Memory 的 MemInfo 事件）");
    if (memorySnapshots.Count > 0)
    {
        MemoryUtilizationEventInfo lastSnapshot = memorySnapshots[^1];
        static double ToMb(long pageCount) =>
            pageCount * MemoryUtilizationEventInfo.PageSizeBytes / 1_000_000.0;

        Console.WriteLine($"  最後一筆快照 @ {lastSnapshot.Timestamp:HH:mm:ss.fff}：");
        Console.WriteLine($"    ZeroListSize={ToMb(lastSnapshot.ZeroPageCount):F2} MB, FreeListSize={ToMb(lastSnapshot.FreePageCount):F2} MB, InUseListSize={ToMb(lastSnapshot.InUsePageCount):F2} MB");
        Console.WriteLine($"    ModifiedListSize={ToMb(lastSnapshot.ModifiedPageCount):F2} MB, ModifiedNoWriteListSize={ToMb(lastSnapshot.ModifiedNoWritePageCount):F2} MB, BadListSize={ToMb(lastSnapshot.BadPageCount):F2} MB");
        Console.WriteLine($"    StandbyListSize={ToMb(lastSnapshot.StandbyTotalPageCount):F2} MB, NonSuperfetchStandbyListSize={ToMb(lastSnapshot.NonSuperfetchStandbyPageCount):F2} MB, RepurposedListSize={ToMb(lastSnapshot.RepurposedTotalPageCount):F2} MB");
        Console.WriteLine($"    ModifiedPageFileDataSize={ToMb(lastSnapshot.ModifiedPageFilePageCount):F2} MB, PagedPoolCommitSize={ToMb(lastSnapshot.PagedPoolPageCount):F2} MB, NonPagedPoolSize={ToMb(lastSnapshot.NonPagedPoolPageCount):F2} MB");
        Console.WriteLine($"    MemoryDescriptorListAllocationsSize={ToMb(lastSnapshot.MdlPageCount):F2} MB, CommitSize={ToMb(lastSnapshot.CommitPageCount):F2} MB");
    }

    // === 為了讓 RunTraceProcessorDemo 解析同一份 ETL 後可互相驗證，整理出跨兩種實作共用的比對快照 ===
    ServiceDataSnapshot serviceDataSnapshot =
        DemoResultValidator.ToServiceDataSnapshot(pendingServices.Result);
    var threadSnapshots = threads
        .Select(t => new ThreadSnapshot(
            t.ThreadId,
            t.ProcessId,
            t.Name ?? string.Empty,
            t.CreateTime,
            t.ExitTime,
            t.BasePriority ?? 0,
            (int?)t.PagePriority,
            (int?)t.IoPriority,
            ToSignedAddress(t.ProcessorAffinity) ?? 0,
            ToSignedAddress(t.StackBase),
            ToSignedAddress(t.StackLimit),
            ToSignedAddress(t.UserStackBase),
            ToSignedAddress(t.UserStackLimit),
            ToSignedAddress(t.StartAddress),
            ToSignedAddress(t.TebBase),
            ToSignedId(t.ServiceId),
            t.StartAddressModuleName))
        .ToList();
    var interruptModuleSnapshots = DemoResultValidator.BuildInterruptModuleSnapshots(
        isrIntervals.Select(isr => isr.HandlerModuleName),
        dpcIntervals.Select(dpc => dpc.HandlerModuleName));
    return new DemoResult_Etl(
        stopwatch.Elapsed,
        result.TraceStartTime,
        result.TraceEndTime,
        processes,
        serviceDataSnapshot,
        memorySnapshots,
        isrIntervals,
        dpcIntervals);
    //return new DemoResult(
    //    stopwatch.Elapsed,
    //    processSnapshots,
    //    serviceSnapshots,
    //    energyIntervals.Count,
    //    energyIntervals.Sum(i => (double)i.TotalEnergy),
    //    interruptModuleSnapshots,
    //    threadSnapshots);
}

/// <summary>
/// 方式二：官方 NuGet 套件 Microsoft.Windows.EventTracing.Processing.All 提供的 TraceProcessor，
/// 是 Windows Performance Analyzer(WPA)背後所使用的同一套解析引擎。
/// 只要呼叫幾個 trace.UseXxx()，於 trace.Process() 執行後就能取得已解析、已關聯好的強型別物件模型
/// (IProcess/ICpuSample/IGenericEvent...)，不必自行處理 TDH schema、緩衝區或個別 Provider 的事件型別。
/// 代價：內部透過傳統 COM 載入原生解析引擎，與 PublishAot=true 預設關閉的 BuiltInComInteropSupport
/// 互斥（已在 csproj 明確改回 true 才能執行本示範），也代表它終究無法真正支援 Native AOT 發佈；
/// 且只能讀取「已寫入磁碟」的完整 ETL，不像 EtlFileReader 可以掛在即時 Session 上逐筆處理事件。
/// </summary>
static DemoResult_Tp RunTraceProcessorDemo(string etlFilePath)
{
    Console.WriteLine();
    Console.WriteLine("=== 方式二：TraceProcessor（Microsoft.Windows.EventTracing.Processing.All） ===");

    // 另一個差異：TraceProcessor 預設對「事件遺失」採取嚴格態度，只要 Session 統計到 EventsLost > 0
    // 就會直接丟出 TraceLostEventsException 中止處理；EtlFileReader 則不會檢查這件事，
    // 遺失多少事件就只解析拿得到的部份，不會拋例外（本範例刻意用短時間高頻旗標觸發遺失，
    // 用 AllowLostEvents=true 讓 TraceProcessor 也能繼續示範，正式環境應優先加大 Buffer 設定）。
    var settings = new TraceProcessorSettings { AllowLostEvents = true };
    using ITraceProcessor trace = TraceProcessor.Create(etlFilePath, settings);
    
    // 想要哪些資料，就掛上對應的 UseXxx()；實際解析與關聯在 trace.Process() 執行時才會發生。
    var pendingProcesses = trace.UseProcesses();
    var pendingServices = trace.UseServices();
    var pendingThreads = trace.UseThreads();
    var pendingCpuSamples = trace.UseCpuSamplingData();
    var pendingGenericEvents = trace.UseGenericEvents();
    var energys = trace.UseEnergyEstimationData();
    var energyMeters = trace.UseEnergyMeterData();
    var interruptHandling = trace.UseInterruptHandlingData();
    var memoryUtilization = trace.UseMemoryUtilizationData();
    long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
    Stopwatch stopwatch = Stopwatch.StartNew();
    trace.Process();
    stopwatch.Stop();
    long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
    
    Console.WriteLine($"[效能] trace.Process() 耗時: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    Console.WriteLine($"[效能] 解析後受管記憶體增量: {(memoryAfter - memoryBefore) / 1024.0 / 1024.0:F2} MB（不含其內部 COM 原生解析引擎配置的非受管記憶體）");

    IReadOnlyList<IProcess> processes = pendingProcesses.Result.Processes;
    Console.WriteLine($"程序數: {processes.Count} 個（單一 IProcess 已含 PID、映像檔名、命令列、父程序、存續時間等，免自行組裝）");

    IReadOnlyList<IService> services = pendingServices.Result.Services;
    Console.WriteLine($"動態服務數: {services.Count} 個，設定快照數: {pendingServices.Result.Snapshots.Count} 個" +
        "（IService 已依 ServiceName 分組，並含 Group/StateChanges/Transitions）");
    foreach (IService svc in services.Take(5))
    {
        IServiceStateChange? lastState = svc.StateChanges.Count > 0 ? svc.StateChanges[^1] : null;
        Console.WriteLine($"  {svc.Name}: PID={(lastState is not null ? lastState.ProcessId.ToString() : "?")} 狀態={(lastState is not null ? lastState.State.ToString() : "?")}");
    }

    IReadOnlyList<Microsoft.Windows.EventTracing.Processes.IThread> threadsTp = pendingThreads.Result.Threads;
    int namedThreadCountTp = threadsTp.Count(t => !string.IsNullOrEmpty(t.Name));
    Console.WriteLine($"執行緒數: {threadsTp.Count} 個（IThread 已含建立/結束時間、堆疊範圍、優先權、可執行處理器、啟動位址、啟動函式所在模組與名稱），其中具名 {namedThreadCountTp} 個");
    foreach (var t in threadsTp.Where(t => !string.IsNullOrEmpty(t.Name)).Take(5))
    {
        string moduleName = t.StartFrame.Image?.FileName ?? "<未映射>";
        Console.WriteLine($"  TID={t.Id} PID={t.ProcessId} Name=\"{t.Name}\" BasePriority={t.BasePriority} PagePriority={t.PagePriority} IoPriority={t.IoPriority} StartAddress模組={moduleName}+0x{t.StartFrame.RelativeVirtualAddress.Value:X}");
    }

    var cpuTop5 = pendingCpuSamples.Result.Samples
        .Where(sample => sample.Process is not null)
        .GroupBy(sample => sample.Process!.Id)
        .Select(group => new
        {
            ProcessId = group.Key,
            ImageName = group.First().Process!.ImageName,
            BusyMilliseconds = group.Sum(sample => sample.Weight.TotalMilliseconds),
        })
        .OrderByDescending(item => item.BusyMilliseconds)
        .Take(5);

    Console.WriteLine("CPU 取樣時間前 5 名（Weight 已由 TraceProcessor 換算好，直接依程序彙總即可）：");
    foreach (var item in cpuTop5)
    {
        Console.WriteLine($"  PID={item.ProcessId} {item.ImageName}: {item.BusyMilliseconds:F3} ms");
    }

    IReadOnlyList<IGenericEvent> genericEvents = pendingGenericEvents.Result.Events;

    // 實測發現：即使 Provider（如 WmiActivity）已用 wevtutil 註冊在本機，TraceProcessor 針對這類
    // 「純 ETW 追蹤用」的核心層 Provider，仍可能解析不出 ProviderName（回傳空字串），
    // 需自行以 ProviderId 當備援 key，否則會誤把多個不同 Provider 全部歸成同一組。
    var eventsByProvider = genericEvents
        .GroupBy(evt => string.IsNullOrEmpty(evt.ProviderName) ? evt.ProviderId.ToString() : evt.ProviderName)
        .Select(group => new { Provider = group.Key, Count = group.Count() })
        .OrderByDescending(item => item.Count)
        .Take(5);

    Console.WriteLine($"已 Manifest 化事件共 {genericEvents.Count} 筆，依 Provider 統計前 5 名" +
        "（WmiActivity/EnergyEstimationEngine/KernelAcpi 等全部共用同一組 API，不必個別撰寫 Parse*Payload）：");
    foreach (var item in eventsByProvider)
    {
        Console.WriteLine($"  {item.Provider}: {item.Count} 筆");
    }

    // 欄位層級也是通用解析：任何 Provider 的欄位理論上都能透過 IGenericEvent.Fields 取得名稱與值，
    // 對照 EtlFileReader 需要為每個 EventId/Version 各自定義一個 record struct(如 WmiActivityEventInfo_17)。
    // 但實測同樣發現：上面 ProviderName 解析不到的 Provider，其 Fields 也會是 null，屬於同一個限制，
    // 必須額外判斷並提供備援訊息 —— 這點正好凸顯 EtlFileReader 手動 offset 解析「來源穩定、不看執行環境臉色」
    // 的優勢，只是換來前面提到的高維護成本（每種事件都要手刻程式碼）。
    IGenericEvent? sampleWmiEvent = genericEvents.FirstOrDefault(evt => evt.ProviderId == TraceSessionBuilder.WmiActivityProviderGuid);
    if (sampleWmiEvent is not null)
    {
        Console.WriteLine($"WMI-Activity 事件欄位示範(EventId={sampleWmiEvent.Id})：");
        if (sampleWmiEvent.Fields is { Count: > 0 } fields)
        {
            foreach (IGenericEventField field in fields)
            {
                Console.WriteLine($"  {field.Name} = {field.AsString}");
            }
        }
        else
        {
            Console.WriteLine("  （此環境下 TraceProcessor 無法解析出此事件的欄位資料，Fields 為 null/空集合）");
        }
    }

    var energyIntervals = energys.Result.Intervals;
    Console.WriteLine($"Energy Estimation Engine 週期數: {energyIntervals.Count} 筆（IEnergyEstimationInterval 每筆代表一次估算週期，需自行依 ConsumerId 加總才能得到單一程序總能耗）");

    var topEnergyConsumers = energyIntervals
        .GroupBy(interval => interval.ConsumerId)
        .Select(group => new
        {
            ConsumerId = group.Key,
            ImageName = group.First().ImageName,
            TotalMilliJoules = group.Sum(interval => interval.TotalEnergy.TotalMillijoules),
        })
        .OrderByDescending(item => item.TotalMilliJoules)
        .Take(5);

    Console.WriteLine("估計耗能前 5 名（依 ConsumerId 加總 TotalEnergy）：");
    foreach (var item in topEnergyConsumers)
    {
        Console.WriteLine($"  {item.ImageName} ({item.ConsumerId}): {item.TotalMilliJoules:F1} mJ");
    }

    var energyMeterIntervals = energyMeters.Result.Intervals;
    Console.WriteLine($"Energy Meter(EMI)區間數: {energyMeterIntervals.Count} 筆（IEnergyMeasurementInterval 每筆代表硬體計量通道相鄰兩次讀數之間的耗能，僅在裝置支援 EMI 時才會有資料）");
    var topEnergyMeterChannels = energyMeterIntervals
        .GroupBy(interval => interval.ChannelName)
        .Select(group => new
        {
            ChannelName = group.Key,
            TotalMilliJoules = group.Sum(interval => interval.TotalEnergy.TotalMillijoules),
        })
        .OrderByDescending(item => item.TotalMilliJoules)
        .Take(5);

    Console.WriteLine("硬體電錶耗能前 5 個通道（依 ChannelName 加總 TotalEnergy）：");
    foreach (var item in topEnergyMeterChannels)
    {
        Console.WriteLine($"  {item.ChannelName}: {item.TotalMilliJoules:F1} mJ");
    }

    // === trace.UseInterruptHandlingData()：一次到位取得已配對好 StartTime/StopTime/HandlerImage 的 ISR/DPC 區間 ===
    var isrIntervals = interruptHandling.Result.InterruptServiceRoutines;
    var dpcIntervals = interruptHandling.Result.DeferredProcedureCalls;
    Console.WriteLine();
    Console.WriteLine($"ISR(中斷服務常式)區間數: {isrIntervals.Count} 筆，DPC(延遲程序呼叫)區間數: {dpcIntervals.Count} 筆" +
        "（IInterruptServiceRoutineInterval/IDeferredProcedureCallInterval 已含 StartTime/StopTime/HandlerImage，免自行配對或反查模組）");

    var isrByModule = isrIntervals
        .GroupBy(isr => isr.HandlerImage?.FileName ?? "<未知模組>")
        .Select(group => new
        {
            Module = group.Key,
            Count = group.Count(),
            TotalMilliseconds = group.Sum(item => (double)(item.StopTime - item.StartTime).TotalMilliseconds),
        })
        .OrderByDescending(item => item.TotalMilliseconds)
        .Take(5);

    Console.WriteLine("ISR 執行時間前 5 名模組（依 HandlerImage 分組）：");
    foreach (var item in isrByModule)
    {
        Console.WriteLine($"  {item.Module}: {item.Count} 次，共 {item.TotalMilliseconds:F3} ms");
    }

    var dpcByModule = dpcIntervals
        .GroupBy(dpc => dpc.HandlerImage?.FileName ?? "<未知模組>")
        .Select(group => new
        {
            Module = group.Key,
            Count = group.Count(),
            TotalMilliseconds = group.Sum(item => (double)(item.StopTime - item.StartTime).TotalMilliseconds),
        })
        .OrderByDescending(item => item.TotalMilliseconds)
        .Take(5);
    
    Console.WriteLine("DPC 執行時間前 5 名模組（依 HandlerImage 分組）：");
    foreach (var item in dpcByModule)
    {
        Console.WriteLine($"  {item.Module}: {item.Count} 次，共 {item.TotalMilliseconds:F3} ms");
    }

    var dpcByType = dpcIntervals
        .GroupBy(dpc => dpc.CallType)
        .Select(group => new { group.Key, Count = group.Count() });

    Console.WriteLine("DPC 依 CallType 統計（Normal/Timer；TraceProcessor 不像 EtlFileReader 額外區分 ThreadedDpc）：");
    foreach (var item in dpcByType)
    {
        Console.WriteLine($"  {item.Key}: {item.Count} 次");
    }

    // === trace.UseMemoryUtilizationData()：一次到位取得已換算成 DataSize 的系統記憶體用量快照 ===
    var memorySnapshots = memoryUtilization.Result.Snapshots;
    Console.WriteLine($"系統記憶體用量快照數: {memorySnapshots.Count} 筆（IMemoryUtilizationSnapshot 每筆對應一次 Kernel-Memory MemInfo 事件）");
    if (memorySnapshots.Count > 0)
    {
        var lastSnapshot = memorySnapshots[^1];
        Console.WriteLine($"  最後一筆快照 @ {lastSnapshot.Timestamp.DateTimeOffset:HH:mm:ss.fff}：");
        Console.WriteLine($"    ZeroListSize={lastSnapshot.ZeroListSize.TotalMegabytes:F2} MB, FreeListSize={lastSnapshot.FreeListSize.TotalMegabytes:F2} MB, InUseListSize={lastSnapshot.InUseListSize.TotalMegabytes:F2} MB");
        Console.WriteLine($"    ModifiedListSize={lastSnapshot.ModifiedListSize.TotalMegabytes:F2} MB, ModifiedNoWriteListSize={lastSnapshot.ModifiedNoWriteListSize.TotalMegabytes:F2} MB, BadListSize={lastSnapshot.BadListSize.TotalMegabytes:F2} MB");
        Console.WriteLine($"    StandbyListSize={lastSnapshot.StandbyListSize.TotalMegabytes:F2} MB, NonSuperfetchStandbyListSize={lastSnapshot.NonSuperfetchStandbyListSize.TotalMegabytes:F2} MB, RepurposedListSize={lastSnapshot.RepurposedListSize.TotalMegabytes:F2} MB");
        Console.WriteLine($"    ModifiedPageFileDataSize={lastSnapshot.ModifiedPageFileDataSize.TotalMegabytes:F2} MB, PagedPoolCommitSize={lastSnapshot.PagedPoolCommitSize.TotalMegabytes:F2} MB, NonPagedPoolSize={lastSnapshot.NonPagedPoolSize.TotalMegabytes:F2} MB");
        Console.WriteLine($"    MemoryDescriptorListAllocationsSize={lastSnapshot.MemoryDescriptorListAllocationsSize.TotalMegabytes:F2} MB, CommitSize={lastSnapshot.CommitSize.TotalMegabytes:F2} MB");
    }

    // === 為了與 RunEtlFileReaderDemo 解析同一份 ETL 的結果互相驗證，整理出跨兩種實作共用的比對快照 ===
    List<ProcessSnapshot> processSnapshots = [.. processes.Select(DemoResultValidator.ToProcessSnapshot)];
    ServiceDataSnapshot serviceDataSnapshot =
        DemoResultValidator.ToServiceDataSnapshot(pendingServices.Result);
    var threadSnapshots = threadsTp
        .Select(t => new ThreadSnapshot(
            t.Id,
            t.ProcessId,
            t.Name ?? string.Empty,
            t.CreateTime?.DateTimeOffset.UtcDateTime,
            t.ExitTime?.DateTimeOffset.UtcDateTime,
            t.BasePriority,
            (int)t.PagePriority,
            (int)t.IoPriority,
            t.ProcessorAffinity,
            t.KernelStackRange.BaseAddress.Value,
            t.KernelStackRange.LimitAddress.Value,
            t.UserStackRange?.BaseAddress.Value,
            t.UserStackRange?.LimitAddress.Value,
            t.StartAddress.Value,
            t.ThreadEnviromentBlockAddress.Value,
            t.ServiceId,
            t.StartFrame.Image?.FileName ?? "<未映射>"))
        .ToList();
    //var interruptModuleSnapshots = DemoResultValidator.BuildInterruptModuleSnapshots(
    //    isrIntervals.Select(isr => isr.HandlerImage?.FileName ?? "<未知模組>"),
    //    dpcIntervals.Select(dpc => dpc.HandlerImage?.FileName ?? "<未知模組>"));

    // TraceProcessor 的 ISR/DPC 物件是依賴 ITraceProcessor 原生資料的 flyweight；
    // 必須在 trace.Dispose() 前複製成純受管快照，不能把介面物件回傳到 using 範圍外。
    List<MemoryUtilizationSnapshot> memoryUtilizationSnapshots =
        [.. memorySnapshots.Select(DemoResultValidator.ToMemoryUtilizationSnapshot)];
    List<IsrSnapshot> isrSnapshots = [.. isrIntervals.Select(DemoResultValidator.ToIsrSnapshot)];
    List<DpcSnapshot> dpcSnapshots = [.. dpcIntervals.Select(DemoResultValidator.ToDpcSnapshot)];

    return new DemoResult_Tp(
        stopwatch.Elapsed,
        processSnapshots,
        serviceDataSnapshot,
        memoryUtilizationSnapshots,
        isrSnapshots,
        dpcSnapshots
        );
    //return new DemoResult(
    //    stopwatch.Elapsed,
    //    processSnapshots,
    //    serviceSnapshots,
    //    energyIntervals.Count,
    //    energyIntervals.Sum(i => (double)i.TotalEnergy.TotalMillijoules),
    //    interruptModuleSnapshots,
    //    threadSnapshots);
}
