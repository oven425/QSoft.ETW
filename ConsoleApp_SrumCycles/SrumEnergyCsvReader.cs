using System.Globalization;
using System.Text;

namespace ConsoleApp_SrumCycles;

/// <summary>One row of "powercfg /srumutil /csv" (Energy Estimation provider). TimeStamp is the UTC interval end.</summary>
internal sealed record SrumEnergyRow(
    string AppId, string UserId, DateTime TimeStampUtc, long TimeInMs, bool OnBattery, bool ScreenOn, bool Foreground,
    long Cpu, long CpuAttributed, long CpuWorkOnBehalf, long Display, long Disk, long Network, long EnergyLoss, long Total)
{
    public DateTime IntervalStartUtc => TimeStampUtc - TimeSpan.FromMilliseconds(TimeInMs);
}

internal static class SrumEnergyCsvReader
{
    public static List<SrumEnergyRow> Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text = Decode(bytes);
        using StringReader reader = new(text);
        string? headerLine = reader.ReadLine() ?? throw new InvalidDataException("Empty SRUM CSV.");
        string[] header = headerLine.Split(',').Select(h => h.Trim()).ToArray();
        Dictionary<string, int> column = header.Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        int Col(string name) => column.TryGetValue(name, out int index) ? index
            : throw new InvalidDataException($"SRUM CSV has no '{name}' column; expected powercfg /srumutil /csv output.");
        int appId = Col("AppId"), userId = Col("UserId"), timeStamp = Col("TimeStamp"), timeInMs = Col("TimeInMSec");
        int onBattery = Col("OnBattery"), screenOn = Col("ScreenOn"), foreground = Col("Foreground");
        int cpu = Col("CPUEnergyConsumption"), cpuAttributed = Col("CPUEnergyConsumptionAttributed");
        int cpuWob = Col("CPUEnergyConsumptionWorkOnBehalf"), display = Col("DisplayEnergyConsumption");
        int disk = Col("DiskEnergyConsumption"), network = Col("NetworkEnergyConsumption"), loss = Col("EnergyLoss");
        int total = Col("TotalEnergyConsumption");

        List<SrumEnergyRow> rows = [];
        while (reader.ReadLine() is { } line)
        {
            string[] parts = line.Split(',');
            if (parts.Length < header.Length) continue;
            if (parts.Length > header.Length)
            {
                // AppId is the only free-text column; commas inside it shift the rest to the right.
                int extra = parts.Length - header.Length;
                parts = [string.Join(',', parts[..(extra + 1)]), .. parts[(extra + 1)..]];
            }
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            if (!DateTime.TryParse(parts[timeStamp], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime stamp)) continue;
            if (!long.TryParse(parts[timeInMs], CultureInfo.InvariantCulture, out long ms)) continue;
            try
            {
                rows.Add(new SrumEnergyRow(parts[appId], parts[userId], stamp, ms,
                    Bool(parts[onBattery]), Bool(parts[screenOn]), Bool(parts[foreground]),
                    Long(parts[cpu]), Long(parts[cpuAttributed]), Long(parts[cpuWob]), Long(parts[display]),
                    Long(parts[disk]), Long(parts[network]), Long(parts[loss]), Long(parts[total])));
            }
            catch (FormatException)
            {
            }
        }
        return rows;
    }

    private static bool Bool(string value) => value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

    private static long Long(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // powercfg writes the ANSI code page when paths contain non-ASCII characters.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)Native.GetACP()).GetString(bytes);
        }
    }
}
