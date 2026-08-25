//using System.Runtime.CompilerServices;
//using System.Runtime.InteropServices;
//using System.Security.Principal;



using QSoft.ETW;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

int durationSeconds = 10;
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

return 0;
