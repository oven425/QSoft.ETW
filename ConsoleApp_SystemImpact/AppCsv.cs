using System.Globalization;

namespace ConsoleApp_SystemImpact;

internal static class AppCsv
{
    public static void Write(TextWriter writer, IReadOnlyList<AppReport> apps)
    {
        writer.WriteLine("Identity,Pid,Name,HealthScore,ImpactScore,MachineCpuPercent,CpuSeconds,WmiOperations,WmiRegistrations,StrongestAssociation,Coherence,FrequencyHz");
        foreach (var app in apps)
        {
            var strongest = app.Associations.Where(a => a.Coherence is not null).MaxBy(a => a.Coherence);
            writer.WriteLine(string.Join(",", new[]
            {
                Cell(app.Identity), app.Pid.ToString(CultureInfo.InvariantCulture), Cell(app.Name),
                Number(app.HealthScore), Number(app.ImpactScore), Number(app.MachineCpuPercent),
                Number(app.CpuSeconds), app.WmiOperations.ToString(CultureInfo.InvariantCulture),
                app.WmiRegistrations.ToString(CultureInfo.InvariantCulture),
                Cell(strongest?.Source ?? ""), Number(strongest?.Coherence), Number(strongest?.FrequencyHz)
            }));
        }
    }

    internal static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "N/A";

    internal static string Cell(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
