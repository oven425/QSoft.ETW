using QSoft.ETW;
using WpfApp1.Models;

namespace WpfApp1.Services;

internal interface IEtlExporter
{
    void Export(EtlFileReader reader, string etlPath);
}

internal abstract class EtlExporter : IEtlExporter
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
    }

    protected virtual void FailExport()
    {
    }

    protected virtual void OnThreadCSwitch(in CSwitchEventInfo data)
    {
    }

    protected virtual void OnDpc(in DpcEventInfo data)
    {
    }

    protected virtual void OnIsr(in InterruptEventInfo data)
    {
    }

    protected virtual void OnThreadStart(in ThreadStartStopEventInfo data)
    {
    }

    protected virtual void OnThreadStop(in ThreadStartStopEventInfo data)
    {
    }

    protected virtual void OnImageLoad(in ImageLoadEventInfo data)
    {
    }

    protected virtual void OnImageUnload(in ImageLoadEventInfo data)
    {
    }

    protected virtual void OnProfile(ProfileEventInfo data)
    {
    }

    private void Attach(EtlFileReader reader)
    {
        //reader.ThreadCSwitch += OnThreadCSwitch;
        //reader.PerfInfoThreadedDPC += OnDpc;
        //reader.PerfInfoDPC += OnDpc;
        //reader.PerfInfoTimerDPC += OnDpc;
        //reader.PerfInfoISR += OnIsr;
        //reader.ThreadStart += OnThreadStart;
        //reader.ThreadStop += OnThreadStop;
        //reader.ThreadDCStart += OnThreadStart;
        //reader.ThreadDCStop += OnThreadStop;
        reader.ImageLoad += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
        //reader.ImageDCStart += OnImageLoad;
        //reader.ImageDCStop += OnImageUnload;
        //reader.PerfInfoProfile += OnProfile;
    }

    private void Detach(EtlFileReader reader)
    {
        //reader.ThreadCSwitch -= OnThreadCSwitch;
        //reader.PerfInfoThreadedDPC -= OnDpc;
        //reader.PerfInfoDPC -= OnDpc;
        //reader.PerfInfoTimerDPC -= OnDpc;
        //reader.PerfInfoISR -= OnIsr;
        //reader.ThreadStart -= OnThreadStart;
        //reader.ThreadStop -= OnThreadStop;
        //reader.ThreadDCStart -= OnThreadStart;
        //reader.ThreadDCStop -= OnThreadStop;
        reader.ImageLoad -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
        //reader.ImageDCStart -= OnImageLoad;
        //reader.ImageDCStop -= OnImageUnload;
        //reader.PerfInfoProfile -= OnProfile;
    }
}
