using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Processes;
using QSoft.ETW;
using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

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
/// 補充：EtlFileReader 內建了一套即時關聯引擎（於建構函式訂閱自身事件，在 ProcessFile() 執行過程中
/// 即時配對 CSwitch New/Old、並以 InstructionPointer/Routine 反查模組），執行完成後可透過
/// <see cref="EtlFileReader.Result"/> 直接取得已關聯好的 <see cref="EtlReadResult"/>
/// （程序清單、CPU 使用彙總、Profile/DPC/Interrupt 熱點），概念上對應下方 TraceProcessor 示範的
/// trace.Process() + IPendingResult&lt;T&gt;.Result，但完全不依賴 SQLite 或任何外部套件。
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
    Console.WriteLine($"[Result] 程序數: {result.Processes.Count} 個（ProcessRecord 已含 PID、映像檔名、命令列、父程序、存續時間，免自行組裝）");

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
    IPendingResult<IProcessDataSource> pendingProcesses = trace.UseProcesses();
    IPendingResult<ICpuSampleDataSource> pendingCpuSamples = trace.UseCpuSamplingData();
    IPendingResult<IGenericEventDataSource> pendingGenericEvents = trace.UseGenericEvents();

    long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
    Stopwatch stopwatch = Stopwatch.StartNew();
    trace.Process();
    stopwatch.Stop();
    long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

    Console.WriteLine($"[效能] trace.Process() 耗時: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    Console.WriteLine($"[效能] 解析後受管記憶體增量: {(memoryAfter - memoryBefore) / 1024.0 / 1024.0:F2} MB（不含其內部 COM 原生解析引擎配置的非受管記憶體）");

    IReadOnlyList<IProcess> processes = pendingProcesses.Result.Processes;
    Console.WriteLine($"程序數: {processes.Count} 個（單一 IProcess 已含 PID、映像檔名、命令列、父程序、存續時間等，免自行組裝）");

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

    return stopwatch.Elapsed;
}
