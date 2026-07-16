using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 簡化版 ETL 分析服務：讀取 ETL 檔案中的 CSwitch/DiskIO/Profile/DPC/Interrupt/Energy 相關事件，
/// 彙總成 <see cref="AnalysisResult"/>。此實作以「輕量、可運作」為原則，資料不足或無法精確解析的
/// 部分會記錄於 <see cref="AnalysisResult.DataQualityWarnings"/>，而非中斷整個分析流程。
/// </summary>
public static class EtlAnalyzer
{
    private const double QpcToMillisecondsFallback = 1.0;

    public static Task<AnalysisResult> AnalyzeAsync(string etlPath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Analyze(etlPath, cancellationToken), cancellationToken);
    }

    private static AnalysisResult Analyze(string etlPath, CancellationToken cancellationToken)
    {
        var result = new AnalysisResult();

        if (!File.Exists(etlPath))
        {
            throw new FileNotFoundException($"找不到 ETL 檔案：{etlPath}", etlPath);
        }

        var processNames = new Dictionary<int, string>();

        var cpuAccumulator = new Dictionary<int, ProcessCpuAccumulator>();
        var lastSwitch = new Dictionary<int, (DateTime Time, int ProcessId)>();

        var ioAccumulator = new Dictionary<int, ProcessIoAccumulator>();

        var profileHotspots = new Dictionary<ulong, int>();
        var dpcHotspots = new Dictionary<ulong, List<DateTime>>();
        var interruptHotspots = new Dictionary<ulong, int>();

        var modules = new List<ModuleRange>();

        var energyEvents = new List<double>();

        int cSwitchCount = 0;
        int diskIoCount = 0;
        int profileCount = 0;
        int dpcCount = 0;
        int interruptCount = 0;

        try
        {
            using var source = new ETWTraceEventSource(etlPath);

            source.Kernel.ProcessStart += data =>
            {
                processNames[data.ProcessID] = SafeImageName(data.ImageFileName, data.ProcessID);
            };
            source.Kernel.ProcessStartGroup += data =>
            {
                processNames[data.ProcessID] = SafeImageName(data.ImageFileName, data.ProcessID);
            };

            source.Kernel.ImageLoad += data =>
            {
                modules.Add(new ModuleRange((ulong)data.ImageBase, (ulong)data.ImageSize, SafeModuleName(data.FileName)));
            };
            source.Kernel.ImageLoadGroup += data =>
            {
                modules.Add(new ModuleRange((ulong)data.ImageBase, (ulong)data.ImageSize, SafeModuleName(data.FileName)));
            };

            source.Kernel.ThreadCSwitch += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                cSwitchCount++;

                int processorNumber = data.ProcessorNumber;
                int newProcessId = data.ProcessID;
                DateTime timestamp = data.TimeStamp;

                if (lastSwitch.TryGetValue(processorNumber, out var previous))
                {
                    double durationMs = (timestamp - previous.Time).TotalMilliseconds;
                    if (durationMs > 0)
                    {
                        var acc = GetOrAdd(cpuAccumulator, previous.ProcessId);
                        acc.TotalCpuTimeMs += durationMs;
                        acc.ContextSwitchCount++;
                        acc.Samples.Add(new TimedSample(previous.Time, durationMs));
                    }
                }

                lastSwitch[processorNumber] = (timestamp, newProcessId);
            };

            source.Kernel.DiskIORead += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                diskIoCount++;
                var acc = GetOrAdd(ioAccumulator, data.ProcessID);
                acc.TotalReadBytes += data.TransferSize;
                acc.IoCount++;
                acc.Samples.Add(new TimedIoSample(data.TimeStamp, data.TransferSize, IsWrite: false));
            };
            source.Kernel.DiskIOWrite += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                diskIoCount++;
                var acc = GetOrAdd(ioAccumulator, data.ProcessID);
                acc.TotalWriteBytes += data.TransferSize;
                acc.IoCount++;
                acc.Samples.Add(new TimedIoSample(data.TimeStamp, data.TransferSize, IsWrite: true));
            };

            source.Kernel.PerfInfoSample += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                profileCount++;
                ulong address = (ulong)data.InstructionPointer;
                profileHotspots[address] = profileHotspots.GetValueOrDefault(address) + 1;
            };

            source.Kernel.PerfInfoDPC += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dpcCount++;
                ulong routine = (ulong)data.Routine;
                if (!dpcHotspots.TryGetValue(routine, out List<DateTime>? list))
                {
                    list = [];
                    dpcHotspots[routine] = list;
                }
                list.Add(data.TimeStamp);
            };
            source.Kernel.PerfInfoThreadedDPC += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dpcCount++;
                ulong routine = (ulong)data.Routine;
                if (!dpcHotspots.TryGetValue(routine, out List<DateTime>? list))
                {
                    list = [];
                    dpcHotspots[routine] = list;
                }
                list.Add(data.TimeStamp);
            };

            source.Kernel.PerfInfoISR += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                interruptCount++;
                ulong routine = (ulong)data.Routine;
                interruptHotspots[routine] = interruptHotspots.GetValueOrDefault(routine) + 1;
            };

            source.Dynamic.All += data =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (data.ProviderGuid == EnergyEstimationEngineProviderGuid)
                {
                    try
                    {
                        for (int i = 0; i < data.PayloadNames.Length; i++)
                        {
                            if (data.PayloadValue(i) is double d)
                            {
                                energyEvents.Add(d);
                            }
                            else if (data.PayloadValue(i) is int intValue)
                            {
                                energyEvents.Add(intValue);
                            }
                        }
                    }
                    catch
                    {
                        // 忽略無法解析的 payload，僅供粗略統計使用。
                    }
                }
            };

            source.Process();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"解析 ETL 檔案失敗：{ex.Message}", ex);
        }

        modules.Sort((a, b) => a.Base.CompareTo(b.Base));

        foreach ((int processId, ProcessCpuAccumulator acc) in cpuAccumulator)
        {
            double totalMs = acc.Samples.Count == 0 ? 0 : acc.Samples[^1].Timestamp.Subtract(acc.Samples[0].Timestamp).TotalMilliseconds;
            double averageCpuPercent = totalMs > 0 ? Math.Min(100, acc.TotalCpuTimeMs / totalMs * 100.0) : 0;

            result.ProcessCpuSummaries.Add(new ProcessCpuSummary
            {
                ProcessId = unchecked((uint)processId),
                ImageFileName = processNames.GetValueOrDefault(processId, $"PID {processId}"),
                AverageCpuPercent = averageCpuPercent,
                ContextSwitchCount = acc.ContextSwitchCount,
                TotalCpuTimeMs = acc.TotalCpuTimeMs,
                Samples = acc.Samples,
            });
        }
        result.ProcessCpuSummaries.Sort((a, b) => b.TotalCpuTimeMs.CompareTo(a.TotalCpuTimeMs));

        foreach ((int processId, ProcessIoAccumulator acc) in ioAccumulator)
        {
            result.ProcessIoSummaries.Add(new ProcessIoSummary
            {
                ProcessId = unchecked((uint)processId),
                ImageFileName = processNames.GetValueOrDefault(processId, $"PID {processId}"),
                TotalReadBytes = acc.TotalReadBytes,
                TotalWriteBytes = acc.TotalWriteBytes,
                IoCount = acc.IoCount,
                Samples = acc.Samples,
            });
        }
        result.ProcessIoSummaries.Sort((a, b) => (b.TotalReadBytes + b.TotalWriteBytes).CompareTo(a.TotalReadBytes + a.TotalWriteBytes));

        if (energyEvents.Count > 0)
        {
            result.ProcessEnergySummaries.Add(new ProcessEnergySummary
            {
                ProcessId = null,
                ImageFileName = "<系統整體 - Energy Estimation Engine>",
                EventCount = energyEvents.Count,
                AverageValue = energyEvents.Average(),
                MinValue = energyEvents.Min(),
                MaxValue = energyEvents.Max(),
            });
        }
        else
        {
            result.DataQualityWarnings.Add("未擷取到 Energy Estimation Engine 相關數值事件，能源與電錶頁籤可能為空。");
        }

        foreach ((ulong address, int count) in profileHotspots.OrderByDescending(kv => kv.Value).Take(50))
        {
            result.ProfileHotspots.Add(new ProfileHotspot
            {
                Address = address,
                ModuleName = ResolveModule(modules, address),
                SampleCount = count,
            });
        }

        foreach ((ulong routine, List<DateTime> timestamps) in dpcHotspots.OrderByDescending(kv => kv.Value.Count).Take(50))
        {
            timestamps.Sort();
            var samples = new List<TimedSample>(timestamps.Count);
            for (int i = 0; i < timestamps.Count; i++)
            {
                samples.Add(new TimedSample(timestamps[i], i + 1));
            }

            result.DpcHotspots.Add(new DpcHotspot
            {
                Routine = routine,
                ModuleName = ResolveModule(modules, routine),
                EventCount = timestamps.Count,
                Samples = samples,
            });
        }

        foreach ((ulong routine, int count) in interruptHotspots.OrderByDescending(kv => kv.Value).Take(50))
        {
            result.InterruptHotspots.Add(new InterruptHotspot
            {
                Routine = routine,
                ModuleName = ResolveModule(modules, routine),
                EventCount = count,
            });
        }

        if (cSwitchCount == 0)
        {
            result.DataQualityWarnings.Add("未擷取到任何 Context Switch (CSwitch) 事件，CPU 分析可能不準確或為空，請確認擷取時已啟用 EVENT_TRACE_FLAG_CSWITCH。");
        }
        if (diskIoCount == 0)
        {
            result.DataQualityWarnings.Add("未擷取到任何 Disk I/O 事件，Disk I/O 分析可能為空，請確認擷取時已啟用 EVENT_TRACE_FLAG_DISK_IO。");
        }
        if (profileCount == 0)
        {
            result.DataQualityWarnings.Add("未擷取到任何 Profile 取樣事件，Profile 熱點可能為空。");
        }
        if (dpcCount == 0)
        {
            result.DataQualityWarnings.Add("未擷取到任何 DPC 事件，DPC 熱點可能為空。");
        }
        if (interruptCount == 0)
        {
            result.DataQualityWarnings.Add("未擷取到任何 Interrupt 事件，Interrupt 熱點可能為空。");
        }
        if (modules.Count == 0)
        {
            result.DataQualityWarnings.Add("未擷取到任何模組載入 (ImageLoad) 事件，Profile/DPC/Interrupt 熱點的 ModuleName 將無法映射，請確認擷取時已啟用 EVENT_TRACE_FLAG_IMAGE_LOAD。");
        }
        result.DataQualityWarnings.Add("本分析為簡化版：Profile/DPC/Interrupt 熱點以位址/常式指標分組，並依擷取到的 ImageLoad 事件範圍嘗試映射模組名稱，若事件不完整或位址未落在已載入範圍內則仍會顯示 <未映射>；Disk I/O 事件之行程歸屬亦可能因核心回報方式而不完全精確。");

        return result;
    }

    private static string SafeImageName(string? imageFileName, int processId)
        => string.IsNullOrWhiteSpace(imageFileName) ? $"PID {processId}" : imageFileName;

    private static string SafeModuleName(string? fileName)
        => string.IsNullOrWhiteSpace(fileName) ? "<未知模組>" : fileName;

    private static string ResolveModule(List<ModuleRange> modules, ulong address)
    {
        foreach (ModuleRange module in modules)
        {
            if (module.Size > 0 && address >= module.Base && address < module.Base + module.Size)
            {
                return module.Name;
            }
        }
        return "<未映射>";
    }

    private static readonly Guid EnergyEstimationEngineProviderGuid = new("ddcc3826-a68a-4e0d-bcfd-9c06c27c6948");

    private static T GetOrAdd<T>(Dictionary<int, T> dict, int key) where T : new()
    {
        if (!dict.TryGetValue(key, out T? value))
        {
            value = new T();
            dict[key] = value;
        }
        return value;
    }

    private sealed class ProcessCpuAccumulator
    {
        public double TotalCpuTimeMs { get; set; }
        public int ContextSwitchCount { get; set; }
        public List<TimedSample> Samples { get; } = [];
    }

    private sealed class ProcessIoAccumulator
    {
        public long TotalReadBytes { get; set; }
        public long TotalWriteBytes { get; set; }
        public int IoCount { get; set; }
        public List<TimedIoSample> Samples { get; } = [];
    }

    private readonly record struct ModuleRange(ulong Base, ulong Size, string Name);
}
