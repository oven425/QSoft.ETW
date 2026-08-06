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

    private sealed class CSwitch
    {
        public required byte ProcessorNumber { get; init; }
        public required DateTime StartedAt { get; init; }
        public DateTime? EndedAt { get; set; }
    }

    private readonly Dictionary<uint, List<CSwitch>> m_ThreadCSwitchs = [];

    protected virtual void OnThreadCSwitch(in CSwitchEventInfo data)
    {
        //db.WriteContextSwitchEvent(in data);
        byte processorNumber = data.ProcessorNumber;

        if (m_ThreadCSwitchs.TryGetValue(data.OldThreadId, out List<CSwitch>? oldThreadCSwitchs))
        {
            CSwitch? runningCSwitch = oldThreadCSwitchs.LastOrDefault(cSwitch =>
                cSwitch.ProcessorNumber == processorNumber && cSwitch.EndedAt is null);

            if (runningCSwitch is not null)
            {
                runningCSwitch.EndedAt = data.Timestamp;
            }
            else
            {
                IncompleteCpuIntervalCount++;
            }
        }
        else
        {
            UnmatchedCpuIntervalCount++;
        }

        if (m_ThreadCSwitchs.TryGetValue(data.NewThreadId, out List<CSwitch>? newThreadCSwitchs))
        {
            newThreadCSwitchs.Add(new CSwitch
            {
                ProcessorNumber = processorNumber,
                StartedAt = data.Timestamp,
            });
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

        //_activeThreads[data.ThreadId] = new ActiveThread(data.ProcessId, data.Timestamp);
        //_cpuExecutionSummaries.Remove(data.ThreadId);
    }

    protected virtual void OnThreadStop(in ThreadStartStopEventInfo data)
    {
        if (m_ThreadCSwitchs.TryGetValue(data.ThreadId, out List<CSwitch>? threadCSwitchs))
        {
            foreach (CSwitch runningCSwitch in threadCSwitchs.Where(cSwitch => cSwitch.EndedAt is null))
            {
                runningCSwitch.EndedAt = data.Timestamp;
            }
        }

        m_ThreadCSwitchs.Remove(data.ThreadId, out var vv);
        //long threadStopEventId = db.WriteThreadEvent(in data);

    }

    protected virtual void OnProcessStart(ProcessInfo process)
    {
        db.WriteProcessStart(process);
    }

    protected virtual void OnProcessStop(ProcessInfo process)
    {
        db.WriteProcessStop(process);
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
        //reader.ImageLoad += OnImageLoad;
        //reader.ImageUnload += OnImageUnload;
        //reader.WmiActivity += OnWmiActivity;
        reader.EnergyEstimationEngine_37 += OnEnergyEstimationEngine_37;
        //reader.KernelAcpi += OnKernelAcpi;
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



}
