using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Services;
using QSoft.ETW;
using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
var pppps = TdhManifestMapReader.QueryManifestEventInfo(Guid.Parse("{0F67E49F-FE51-4E9F-B490-6F2948CC6027}"));
var ees = TdhManifestMapReader.QueryEnergyEstimateEventInfo();
int durationSeconds = 30;
if (args.Length > 0 && int.TryParse(args[0], out int parsedSeconds) && parsedSeconds > 0)
{
    durationSeconds = parsedSeconds;
}

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
    // SystemMemoryProviderGuid 只是啟用用的控制平面 GUID，實際的 MemInfo 事件由 Microsoft-Windows-Kernel-Memory
    // 提供者(搭配 MEMINFO 關鍵字)發出，兩者須同時啟用，UseMemoryUtilizationData()/MemoryUtilization 才會有資料。
    .WithProvider(TraceSessionBuilder.KernelMemoryProviderGuid, TraceSessionBuilder.KernelMemoryMemInfoKeyword)
    .WithOutputPath(Path.Combine(AppContext.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.etl"))
    .WithEtwFileCompression()
    .Build();

if (!session.IsElevated())
{
    Console.Error.WriteLine("此程式需要以系統管理員身分執行才能啟動 ETW Kernel/User Trace。");
    return 1;
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
    return 1;
}
finally
{
    session.Stop();
}

// === ETL 蒐集完成，以下用兩種方式解析同一份檔案，藉由實際輸出對照兩者差異 ===
string capturedEtlPath = session.LogFilePath ?? string.Empty;
if (string.IsNullOrEmpty(capturedEtlPath) || !File.Exists(capturedEtlPath))
{
    Console.Error.WriteLine("找不到剛完成擷取的 ETL 檔案，略過解析示範。");
    return 1;
}

long etlFileSizeBytes = new FileInfo(capturedEtlPath).Length;
double etlFileSizeMb = etlFileSizeBytes / 1024.0 / 1024.0;
Console.WriteLine($"ETL 檔案大小: {etlFileSizeMb:F2} MB ({capturedEtlPath})");

TimeSpan etlFileReaderElapsed = RunEtlFileReaderDemo(capturedEtlPath);
TimeSpan traceProcessorElapsed = RunTraceProcessorDemo(capturedEtlPath);

Console.WriteLine();
Console.WriteLine("=== 效能比較總結（同一份 ETL，兩種解析方式）===");
Console.WriteLine($"ETL 檔案大小: {etlFileSizeMb:F2} MB");
Console.WriteLine($"EtlFileReader ProcessFile() 耗時: {etlFileReaderElapsed.TotalMilliseconds:F1} ms（{etlFileSizeMb / etlFileReaderElapsed.TotalSeconds:F2} MB/s）");
Console.WriteLine($"TraceProcessor trace.Process() 耗時: {traceProcessorElapsed.TotalMilliseconds:F1} ms（{etlFileSizeMb / traceProcessorElapsed.TotalSeconds:F2} MB/s）");
if (etlFileReaderElapsed < traceProcessorElapsed)
{
    Console.WriteLine($"EtlFileReader 較快，約為 TraceProcessor 的 {traceProcessorElapsed.TotalMilliseconds / etlFileReaderElapsed.TotalMilliseconds:F2} 倍速度" +
        "（但 TraceProcessor 同時多做了跨程序符號解析等 EtlFileReader 未實作的工作，非完全對等負載）。");
}
else
{
    Console.WriteLine($"TraceProcessor 較快，約為 EtlFileReader 的 {etlFileReaderElapsed.TotalMilliseconds / traceProcessorElapsed.TotalMilliseconds:F2} 倍速度。");
}

return 0;

/// <summary>
/// 方式一：本專案 QSoft.ETW 的 <see cref="EtlFileReader"/>。
/// 內部以 P/Invoke 直接呼叫 Advapi32 的 OpenTrace/ProcessTrace 逐筆讀取 EVENT_RECORD，
/// 再透過 Tdh* API 手動解析每個 Provider 的 schema，並以「一種事件對應一個強型別 event」的方式向外推送
/// (ProcessStart、ImageLoad、ThreadCSwitch、WmiActivity_17...)，需在呼叫 ProcessFile() 之前先訂閱好。
/// 優點：零外部相依、可 Native AOT/Trimming 發佈；缺點：每新增一種要支援的事件，
/// 都得手刻對應的 struct、delegate 與 TDH 解析程式碼，EtlFileReader.cs 因此累積超過三千行。
///
/// 補充：EtlFileReader 透過插件(<see cref="IEtlReaderPlugin"/>)提供即時關聯引擎，預設不會啟用──
/// 單純轉存 SQLite 等情境不需要這些額外的關聯/彙總計算。呼叫 <see cref="EtlFileReader.UseBuiltInAnalysis"/>
/// 掛上內建的 <see cref="EtlAnalysisPlugin"/> 後(於 ProcessFile() 執行過程中即時配對 CSwitch New/Old、
/// 並以 InstructionPointer/Routine 反查模組)，執行完成後即可透過 <see cref="EtlFileReader.Result"/>
/// 直接取得已關聯好的 <see cref="EtlReadResult"/>（程序清單、CPU 使用彙總、Profile/DPC/Interrupt 熱點），
/// 概念上對應下方 TraceProcessor 示範的 trace.Process() + IPendingResult&lt;T&gt;.Result，但完全不依賴
/// SQLite 或任何外部套件。
/// </summary>
static TimeSpan RunEtlFileReaderDemo(string etlFilePath)
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
    var pendingEnergy = reader.UseEnergyEstimationData();
    var pendingEnergyMeter = reader.UseEnergyMeterData();
    var pendingInterruptHandling = reader.UseInterruptHandlingData();
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
    Console.WriteLine($"[Result] 程序數: {pendingProcesses.Result.Processes.Count} 個（ProcessRecord 已含 PID、映像檔名、命令列、父程序、存續時間，免自行組裝）");
    Console.WriteLine($"[Result] 服務數: {pendingServices.Result.Services.Count} 個（ServiceRecord 來自 Microsoft-Windows-Services 的 EventId=105 StatusChange，含 PID/CurrentState/StartType，對應 TraceProcessor 的 IServiceDataSource）");
    foreach (ServiceRecord svc in pendingServices.Result.Services.Take(5))
    {
        Console.WriteLine($"  {svc.ServiceName} ({svc.ImageFileName}): PID={svc.ProcessId?.ToString() ?? "?"} 狀態={svc.CurrentState}");
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
        "（IInterruptServiceRoutineInterval/IDeferredProcedureCallInterval 對等，StartTime 由 StopTime 往前推 ElapsedTimeMSec 推算）");
    Console.WriteLine("  [已知限制] 實測比對 TraceProcessor 後確認：筆數與所屬模組完全一致、可放心採信；" +
        "但耗時是由 QPC→FILETIME 的線上回歸換算而來，統計上會系統性低估（詳見程式碼註解），" +
        "下方的耗時加總僅供參考，精確數值請以 TraceProcessor 的結果為準。");

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
        static double ToMb(long pageCount) => pageCount * MemoryUtilizationEventInfo.PageSizeBytes / 1024.0 / 1024.0;

        Console.WriteLine($"  最後一筆快照 @ {lastSnapshot.Timestamp:HH:mm:ss.fff}：");
        Console.WriteLine($"    ZeroListSize={ToMb(lastSnapshot.ZeroPageCount):F2} MB, FreeListSize={ToMb(lastSnapshot.FreePageCount):F2} MB");
        Console.WriteLine($"    ModifiedListSize={ToMb(lastSnapshot.ModifiedPageCount):F2} MB, ModifiedNoWriteListSize={ToMb(lastSnapshot.ModifiedNoWritePageCount):F2} MB, BadListSize={ToMb(lastSnapshot.BadPageCount):F2} MB");
        Console.WriteLine($"    StandbyListSize={ToMb(lastSnapshot.StandbyTotalPageCount):F2} MB");
        Console.WriteLine($"    RepurposedPageCount(累計計數器，非清單大小，詳見型別文件註解)={ToMb(lastSnapshot.RepurposedTotalPageCount):F2} MB");
        Console.WriteLine($"    ModifiedPageFileDataSize={ToMb(lastSnapshot.ModifiedPageFilePageCount):F2} MB, PagedPoolCommitSize={ToMb(lastSnapshot.PagedPoolPageCount):F2} MB, NonPagedPoolSize={ToMb(lastSnapshot.NonPagedPoolPageCount):F2} MB");
        Console.WriteLine($"    MemoryDescriptorListAllocationsSize={ToMb(lastSnapshot.MdlPageCount):F2} MB, CommitSize={ToMb(lastSnapshot.CommitPageCount):F2} MB");
        Console.WriteLine("    （TraceProcessor 另有 InUseListSize/NonSuperfetchStandbyListSize 兩個推算欄位，本專案未提供對應數值，原因見 MemoryUtilizationEventInfo 文件註解）");
    }

    return stopwatch.Elapsed;
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
static TimeSpan RunTraceProcessorDemo(string etlFilePath)
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
    Microsoft.Windows.EventTracing.IPendingResult<Microsoft.Windows.EventTracing.Processes.IProcessDataSource> pendingProcesses = trace.UseProcesses();
    Microsoft.Windows.EventTracing.IPendingResult<Microsoft.Windows.EventTracing.Services.IServiceDataSource> pendingServices = trace.UseServices();
    Microsoft.Windows.EventTracing.IPendingResult<ICpuSampleDataSource> pendingCpuSamples = trace.UseCpuSamplingData();
    Microsoft.Windows.EventTracing.IPendingResult<IGenericEventDataSource> pendingGenericEvents = trace.UseGenericEvents();
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
    Console.WriteLine($"服務數: {services.Count} 個（IService 已依 ServiceName 分組，StateChanges 內含每次啟停與所在 PID）");
    foreach (IService svc in services.Take(5))
    {
        IServiceStateChange? lastState = svc.StateChanges.Count > 0 ? svc.StateChanges[^1] : null;
        Console.WriteLine($"  {svc.Name}: PID={(lastState is not null ? lastState.ProcessId.ToString() : "?")} 狀態={(lastState is not null ? lastState.State.ToString() : "?")}");
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
        Console.WriteLine("    （InUseListSize/NonSuperfetchStandbyListSize 是 TraceProcessor 內部推算欄位，MemInfo 事件本身沒有對應的原始欄位可解析，EtlFileReader 端故意不提供這兩項，避免以未經證實的猜測值冒充真實資料）");
        Console.WriteLine("    （RepurposedListSize 經比對後發現不宜與 EtlFileReader 的 RepurposedTotalPageCount 直接對照：後者是原始事件裡單調遞增的累計計數器，" +
            "TraceProcessor 疑似對其做了「相鄰快照差值」之類的內部換算才得到這種數十 MB、會隨時間起伏的數值，但確切公式未公開，還原後仍有約 5% 誤差，故不採用）");
    }

    return stopwatch.Elapsed;
}
