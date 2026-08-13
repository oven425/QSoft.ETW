using System.Diagnostics;
using System.Globalization;
using System.Management;

// 這支程式的用途：用 System.Management 定期查詢 WMI (Win32_Battery)，
// 藉此製造真實的 WMI 呼叫，同時把「本行程的 PID / 啟動時間 / 查詢時間點」印出來，
// 方便跟 ETW 擷取到的 WmiActivityEvents_24.ClientProcessId、TimestampUtc 對照，
// 驗證 ClientProcessId 是否真的對應到「發起呼叫的應用程式」而非 WMI 服務端行程。

int intervalSeconds = 5;
int durationSeconds = 60;





DateTime endAtUtc = DateTime.UtcNow.AddSeconds(durationSeconds);
int delaysec = 1;
for(int i=0; i<1000; i++)
{
    DateTime queryStartedAtUtc = DateTime.UtcNow;

    try
    {
        using ManagementObjectSearcher searcher = new(@"root\CIMV2", "SELECT * FROM Win32_Battery");
        using ManagementObjectCollection results = searcher.Get();

        bool foundBattery = false;
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                foundBattery = true;
                object? chargeRemaining = item["EstimatedChargeRemaining"];
                object? batteryStatus = item["BatteryStatus"];
                Console.WriteLine(
                    $"EstimatedChargeRemaining={chargeRemaining} BatteryStatus={batteryStatus}");
            }
        }


    }
    catch (ManagementException ex)
    {
        Console.WriteLine($"[{queryStartedAtUtc:O}] WMI 查詢失敗: {ex.Message}");
    }

    Thread.Sleep(TimeSpan.FromSeconds(delaysec));
    delaysec++;
}

Console.WriteLine();
Console.WriteLine("測試結束。");
return 0;
