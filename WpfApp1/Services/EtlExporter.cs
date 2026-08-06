using QSoft.ETW;
using WpfApp1.Models;

namespace WpfApp1.Services;



internal class SQLiteExport(DataBase_SQLite db)
{
    public int UnmatchedCpuIntervalCount { get; private set; }

    public int IncompleteCpuIntervalCount { get; private set; }

    public void Export(EtlFileReader reader, string etlPath)
    {
        BeginExport(etlPath);
        Attach(reader);

        try
        {
            reader.ProcessFile(etlPath);
            CompleteExport();
        }
        catch
        {
            FailExport();
            throw;
        }
        finally
        {
            Detach(reader);
        }
    }

    protected virtual void BeginExport(string etlPath)
    {
        m_ThreadCSwitchs.Clear();
        m_ThreadStartedAts.Clear();
        m_ThreadProcessIds.Clear();
        m_ProcessThreadCpuSummaries.Clear();
        UnmatchedCpuIntervalCount = 0;
        IncompleteCpuIntervalCount = 0;
    }

    protected virtual void CompleteExport()
    {
        db.Complete();
    }

    protected virtual void FailExport()
    {
        db.Fail();
    }

    private readonly Dictionary<uint, List<CSwitchEventInfo>> m_ThreadCSwitchs = [];
    private readonly Dictionary<uint, DateTime> m_ThreadStartedAts = [];
    private readonly Dictionary<uint, uint> m_ThreadProcessIds = [];
    private readonly Dictionary<uint, List<ThreadCpuSummary>> m_ProcessThreadCpuSummaries = [];

    protected virtual void OnThreadCSwitch(in CSwitchEventInfo data)
    {
        //db.WriteContextSwitchEvent(in data);
        if (m_ThreadCSwitchs.TryGetValue(data.OldThreadId, out List<CSwitchEventInfo>? threadCSwitchs))
        {
            threadCSwitchs.Add(data);
        }

        if (m_ThreadCSwitchs.TryGetValue(data.NewThreadId, out List<CSwitchEventInfo>? threadCSwitchs1))
        {
            threadCSwitchs1.Add(data);
        }
    }

    protected virtual void OnDpc(in DpcEventInfo data)
    {
    }

    protected virtual void OnIsr(in InterruptEventInfo data)
    {
    }

    protected virtual void OnThreadStart(in ThreadStartStopEventInfo data)
    {
        db.WriteThreadEvent(in data);
        m_ThreadCSwitchs[data.ThreadId] = [];
        m_ThreadStartedAts[data.ThreadId] = data.Timestamp;
        m_ThreadProcessIds[data.ThreadId] = data.ProcessId;
    }

    protected virtual void OnThreadStop(in ThreadStartStopEventInfo data)
    {
        m_ThreadCSwitchs.Remove(data.ThreadId, out List<CSwitchEventInfo>? threadCSwitchs);
        bool hasThreadStartedAt = m_ThreadStartedAts.Remove(data.ThreadId, out DateTime threadStartedAt);
        m_ThreadProcessIds.Remove(data.ThreadId);
        ThreadCpuSummary? cpuSummary = threadCSwitchs is null
            ? null
            : CreateCpuSummary(
                data.ThreadId,
                hasThreadStartedAt ? threadStartedAt : null,
                data.Timestamp,
                threadCSwitchs);

        if (cpuSummary is ThreadCpuSummary summary)
        {
            AddProcessThreadCpuSummary(data.ProcessId, summary);
        }

        db.WriteThreadEvent(
            in data,
            cpuSummary?.StartedAt,
            cpuSummary?.EndedAt,
            cpuSummary?.DurationTicks);
    }

    protected virtual void OnProcessStart(ProcessInfo process)
    {
        db.WriteProcessStart(process);
    }

    protected virtual void OnProcessStop(ProcessInfo process)
    {
        DateTime processStoppedAt = process.EndTime ?? throw new InvalidOperationException("程序結束事件未提供結束時間。");
        List<uint> activeThreadIds = [];
        foreach ((uint threadId, uint processId) in m_ThreadProcessIds)
        {
            if (processId == process.ProcessId)
            {
                activeThreadIds.Add(threadId);
            }
        }

        foreach (uint threadId in activeThreadIds)
        {
            m_ThreadCSwitchs.Remove(threadId, out List<CSwitchEventInfo>? threadCSwitchs);
            bool hasThreadStartedAt = m_ThreadStartedAts.Remove(threadId, out DateTime threadStartedAt);
            m_ThreadProcessIds.Remove(threadId);

            if (threadCSwitchs is not null &&
                CreateCpuSummary(
                    threadId,
                    hasThreadStartedAt ? threadStartedAt : null,
                    processStoppedAt,
                    threadCSwitchs) is ThreadCpuSummary summary)
            {
                AddProcessThreadCpuSummary(process.ProcessId, summary);
            }
        }

        m_ProcessThreadCpuSummaries.Remove(process.ProcessId, out List<ThreadCpuSummary>? threadCpuSummaries);
        ProcessCpuSummary? cpuSummary = CreateProcessCpuSummary(process, threadCpuSummaries);
        db.WriteProcessStop(process, cpuSummary?.DurationTicks, cpuSummary?.CpuUsagePercent);
    }

    protected virtual void OnImageLoad(in ImageLoadEventInfo data)
    {
        db.WriteImageLoad(data);
    }

    protected virtual void OnImageUnload(in ImageLoadEventInfo data)
    {
        db.WriteImageUnLoad(data);
    }

    protected virtual void OnWmiActivity(in WmiActivityEventInfo data)
    {
        db.WriteWmiActivity(in data);
    }


    protected virtual void OnKernelAcpi(KernelAcpiEventInfo data)
    {
        db.WriteKernelAcpi(data);
    }

    protected virtual void OnProfile(ProfileEventInfo data)
    {
    }

    private void Attach(EtlFileReader reader)
    {
        reader.ThreadCSwitch += OnThreadCSwitch;
        ////reader.PerfInfoThreadedDPC += OnDpc;
        ////reader.PerfInfoDPC += OnDpc;
        ////reader.PerfInfoTimerDPC += OnDpc;
        ////reader.PerfInfoISR += OnIsr;
        reader.ThreadStart += OnThreadStart;
        reader.ThreadStop += OnThreadStop;
        reader.ThreadDCStart += OnThreadStart;
        reader.ThreadDCStop += OnThreadStop;
        reader.ProcessStart += OnProcessStart;
        reader.ProcessStop += OnProcessStop;
        reader.ImageLoad += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
        reader.WmiActivity += OnWmiActivity;
        reader.EnergyEstimationEngine_37 += OnEnergyEstimationEngine_37;
        reader.KernelAcpi += OnKernelAcpi;
        ////reader.ImageDCStart += OnImageLoad;
        ////reader.ImageDCStop += OnImageUnload;
        ////reader.PerfInfoProfile += OnProfile;
    }

    private void OnEnergyEstimationEngine_37(in EnergyEstimationEngineEventInfo_37 data)
    {
        db.WriteEnergyEstimationEngine(in data);
    }

    private void Detach(EtlFileReader reader)
    {
        reader.ThreadCSwitch -= OnThreadCSwitch;
        //reader.PerfInfoThreadedDPC -= OnDpc;
        //reader.PerfInfoDPC -= OnDpc;
        //reader.PerfInfoTimerDPC -= OnDpc;
        //reader.PerfInfoISR -= OnIsr;
        reader.ThreadStart -= OnThreadStart;
        reader.ThreadStop -= OnThreadStop;
        reader.ThreadDCStart -= OnThreadStart;
        reader.ThreadDCStop -= OnThreadStop;
        reader.ProcessStart -= OnProcessStart;
        reader.ProcessStop -= OnProcessStop;
        reader.ImageLoad -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
        reader.WmiActivity -= OnWmiActivity;
        reader.EnergyEstimationEngine_37 -= OnEnergyEstimationEngine_37;
        reader.KernelAcpi -= OnKernelAcpi;
        //reader.ImageDCStart -= OnImageLoad;
        //reader.ImageDCStop -= OnImageUnload;
        //reader.PerfInfoProfile -= OnProfile;
    }


    private ThreadCpuSummary? CreateCpuSummary(
        uint threadId,
        DateTime? threadStartedAt,
        DateTime threadStoppedAt,
        List<CSwitchEventInfo> threadCSwitchs)
    {
        Dictionary<byte, DateTime> startedAtByProcessor = [];
        DateTime? cpuStartedAt = null;
        DateTime? cpuEndedAt = null;
        long durationTicks = 0;

        foreach (CSwitchEventInfo cSwitch in threadCSwitchs)
        {
            if (cSwitch.NewThreadId == threadId)
            {
                if (!startedAtByProcessor.TryAdd(cSwitch.ProcessorNumber, cSwitch.Timestamp))
                {
                    IncompleteCpuIntervalCount++;
                    startedAtByProcessor[cSwitch.ProcessorNumber] = cSwitch.Timestamp;
                }
            }

            if (cSwitch.OldThreadId == threadId)
            {
                if (startedAtByProcessor.Remove(cSwitch.ProcessorNumber, out DateTime startedAt) &&
                    cSwitch.Timestamp >= startedAt)
                {
                    AddCpuInterval(startedAt, cSwitch.Timestamp, ref cpuStartedAt, ref cpuEndedAt, ref durationTicks);
                }
                else
                {
                    UnmatchedCpuIntervalCount++;
                }
            }
        }

        foreach (DateTime startedAt in startedAtByProcessor.Values)
        {
            if (threadStoppedAt >= startedAt)
            {
                AddCpuInterval(startedAt, threadStoppedAt, ref cpuStartedAt, ref cpuEndedAt, ref durationTicks);
            }
            else
            {
                UnmatchedCpuIntervalCount++;
            }
        }

        if (cpuStartedAt is null)
        {
            return null;
        }

        long lifetimeTicks = threadStartedAt is null ? 0 : (threadStoppedAt - threadStartedAt.Value).Ticks;
        double cpuUsagePercent = lifetimeTicks > 0
            ? durationTicks * 100.0 / lifetimeTicks
            : 0;

        return new ThreadCpuSummary(
            cpuStartedAt.Value,
            cpuEndedAt!.Value,
            durationTicks,
            cpuUsagePercent);
    }

    private static void AddCpuInterval(
        DateTime startedAt,
        DateTime endedAt,
        ref DateTime? cpuStartedAt,
        ref DateTime? cpuEndedAt,
        ref long durationTicks)
    {
        cpuStartedAt = cpuStartedAt is null || startedAt < cpuStartedAt ? startedAt : cpuStartedAt;
        cpuEndedAt = cpuEndedAt is null || endedAt > cpuEndedAt ? endedAt : cpuEndedAt;
        durationTicks = checked(durationTicks + (endedAt - startedAt).Ticks);
    }

    private void AddProcessThreadCpuSummary(uint processId, in ThreadCpuSummary summary)
    {
        if (!m_ProcessThreadCpuSummaries.TryGetValue(processId, out List<ThreadCpuSummary>? summaries))
        {
            summaries = [];
            m_ProcessThreadCpuSummaries.Add(processId, summaries);
        }

        summaries.Add(summary);
    }

    private static ProcessCpuSummary? CreateProcessCpuSummary(
        ProcessInfo process,
        List<ThreadCpuSummary>? threadCpuSummaries)
    {
        if (threadCpuSummaries is null || threadCpuSummaries.Count == 0 || process.EndTime is not DateTime endedAt)
        {
            return null;
        }

        long durationTicks = 0;
        foreach (ThreadCpuSummary threadCpuSummary in threadCpuSummaries)
        {
            durationTicks = checked(durationTicks + threadCpuSummary.DurationTicks);
        }

        long lifetimeTicks = (endedAt - process.StartTime).Ticks;
        double cpuUsagePercent = lifetimeTicks > 0
            ? durationTicks * 100.0 / lifetimeTicks
            : 0;

        return new ProcessCpuSummary(durationTicks, cpuUsagePercent);
    }

    private readonly record struct ThreadCpuSummary(
        DateTime StartedAt,
        DateTime EndedAt,
        long DurationTicks,
        double CpuUsagePercent);

    private readonly record struct ProcessCpuSummary(long DurationTicks, double CpuUsagePercent);
}
