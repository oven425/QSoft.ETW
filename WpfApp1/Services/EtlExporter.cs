using QSoft.ETW;
using WpfApp1.Models;

namespace WpfApp1.Services;



internal class SQLiteExport(DataBase_SQLite db)
{
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
    }

    protected virtual void CompleteExport()
    {
        db.Complete();
    }

    protected virtual void FailExport()
    {
        db.Fail();
    }

    protected virtual void OnThreadCSwitch(in CSwitchEventInfo data)
    {
            db.WriteContextSwitchEvent(in data);
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
    }

    protected virtual void OnThreadStop(in ThreadStartStopEventInfo data)
    {
            db.WriteThreadEvent(in data);
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

    protected virtual void OnEnergyEstimationEngine(in EnergyEstimationEngineEventInfo data)
    {
        db.WriteEnergyEstimationEngine(in data);
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
        //reader.PerfInfoThreadedDPC += OnDpc;
        //reader.PerfInfoDPC += OnDpc;
        //reader.PerfInfoTimerDPC += OnDpc;
        //reader.PerfInfoISR += OnIsr;
        reader.ThreadStart += OnThreadStart;
        reader.ThreadStop += OnThreadStop;
        reader.ThreadDCStart += OnThreadStart;
        reader.ThreadDCStop += OnThreadStop;
        reader.ProcessStart += OnProcessStart;
        reader.ProcessStop += OnProcessStop;
        reader.ImageLoad += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
        reader.WmiActivity += OnWmiActivity;
        reader.EnergyEstimationEngine += OnEnergyEstimationEngine;
        reader.KernelAcpi += OnKernelAcpi;
        //reader.ImageDCStart += OnImageLoad;
        //reader.ImageDCStop += OnImageUnload;
        //reader.PerfInfoProfile += OnProfile;
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
        reader.EnergyEstimationEngine -= OnEnergyEstimationEngine;
        reader.KernelAcpi -= OnKernelAcpi;
        //reader.ImageDCStart -= OnImageLoad;
        //reader.ImageDCStop -= OnImageUnload;
        //reader.PerfInfoProfile -= OnProfile;
    }
}
