using System.Collections.ObjectModel;
using QSoft.ETW;

namespace WpfApp1.Models;

/// <summary>
/// 單一服務的存續期間彙總資訊，由 SQLite 內 ServiceStatusChanges 資料表依 ServiceName 重建而成，
/// 對應 TraceProcessor 的 IService（Name/StateChanges），供 UI 或其他程式碼使用。
/// </summary>
public sealed class ServiceInfo
{
    public ServiceInfo(string serviceName)
    {
        ServiceName = serviceName;
    }

    public string ServiceName { get; }

    public ObservableCollection<ServiceStateChangeInfo> StateChanges { get; } = [];

    private ServiceStateChangeInfo? Latest => StateChanges.Count == 0 ? null : StateChanges[^1];

    public string ImageName => Latest?.ImageName ?? string.Empty;

    public long? ProcessId => Latest?.ProcessId;

    public ServiceState? CurrentState => Latest?.CurrentState;

    public ServiceStartType? StartType
    {
        get
        {
            ServiceStartType? rawStartType = Latest?.StartType;
            if (rawStartType is not (ServiceStartType.Auto or ServiceStartType.DelayedAuto))
            {
                return rawStartType;
            }

            foreach (ServiceStateChangeInfo stateChange in StateChanges)
            {
                if (stateChange.CurrentState == ServiceState.StartPending)
                {
                    return stateChange.ExecutionPhase == 2
                        ? ServiceStartType.DelayedAuto
                        : ServiceStartType.Auto;
                }
            }

            return null;
        }
    }

    public string StateSummary => Latest is null
        ? "沒有狀態變化紀錄"
        : $"目前狀態：{Latest.CurrentState}（PID={Latest.ProcessId}，StartType={StartType?.ToString() ?? "無法判定"}，共 {StateChanges.Count} 次狀態變化）";
}

/// <summary>
/// 單一次服務狀態變化紀錄，對應 TraceProcessor 的 IServiceStateChange（ProcessId/State/ImageName/Timestamp）。
/// ProcessRecordId/ProcessImageFileName/ProcessCommandLine 由 ProcessRecordId 外鍵 JOIN 回 Processes 資料表取得，
/// 對應 IServiceStateChange.Process 可取得完整程序資訊這一點；找不到對應程序紀錄時皆為 null。
/// </summary>
public sealed record ServiceStateChangeInfo(
    string TimestampUtc,
    string ImageName,
    long ProcessId,
    uint ExecutionPhase,
    ServiceState CurrentState,
    ServiceStartType StartType,
    long? ProcessRecordId,
    string? ProcessImageFileName,
    string? ProcessCommandLine);
