using System.Diagnostics;
using System.Globalization;

// Deterministic CPU load: every thread follows the same duty-cycle pattern, one entry per phase.
// Example: CpuBurner --threads 2 --phase-seconds 60 --pattern 100,25,100,0
int threads = 1;
double phaseSeconds = 60;
int[] pattern = [100];
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--phase-seconds" && i + 1 < args.Length) phaseSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--pattern" && i + 1 < args.Length)
        pattern = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    else
    {
        Console.Error.WriteLine("CpuBurner [--threads N] [--phase-seconds S] [--pattern 100,25,0,...]");
        return 2;
    }
}
if (threads <= 0 || phaseSeconds <= 0 || pattern.Length == 0 || pattern.Any(p => p is < 0 or > 100))
{
    Console.Error.WriteLine("threads and phase-seconds must be positive; pattern values must be 0-100.");
    return 2;
}

double totalSeconds = phaseSeconds * pattern.Length;
Console.WriteLine($"CpuBurner PID={Environment.ProcessId} threads={threads} phase={phaseSeconds}s pattern={string.Join(',', pattern)} total={totalSeconds}s");
long start = Stopwatch.GetTimestamp();
const double SliceMs = 100;

Thread[] workers = new Thread[threads];
for (int t = 0; t < threads; t++)
{
    workers[t] = new Thread(() =>
    {
        double sink = 1;
        while (true)
        {
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (elapsed >= totalSeconds) break;
            int duty = pattern[Math.Min((int)(elapsed / phaseSeconds), pattern.Length - 1)];
            long sliceStart = Stopwatch.GetTimestamp();
            double busyMs = SliceMs * duty / 100.0;
            while (Stopwatch.GetElapsedTime(sliceStart).TotalMilliseconds < busyMs)
            {
                for (int k = 0; k < 2000; k++) sink = sink * 1.0000001 + 0.5;
            }
            double rest = SliceMs - Stopwatch.GetElapsedTime(sliceStart).TotalMilliseconds;
            if (rest >= 1) Thread.Sleep((int)rest);
        }
        GC.KeepAlive(sink);
    })
    { IsBackground = false, Name = $"burn-{t}" };
    workers[t].Start();
}
foreach (Thread worker in workers) worker.Join();
Console.WriteLine($"CpuBurner PID={Environment.ProcessId} done after {Stopwatch.GetElapsedTime(start).TotalSeconds:F1}s");
return 0;
