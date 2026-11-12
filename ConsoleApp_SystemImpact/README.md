# System impact experiment

This standalone .NET 10 Windows console captures **180 seconds** using `TraceSession`,
then reads the closed ETL using `EtlFileReader` callbacks and `UseGpuData()` for DxgKrnl GPU
engine, process-memory and adapter rundown data. Other callbacks copy compact records into
`List<T>`; no SQLite, TraceProcessor, or external FFT packages are used.
All event Lists remain in memory during analysis; busy systems can require substantial RAM.

## Run

From the repository root:

```powershell
dotnet build .\ConsoleApp_SystemImpact\ConsoleApp_SystemImpact.csproj -c Release
dotnet run --project .\ConsoleApp_SystemImpact -c Release --no-build -- --self-test
# Run in an Administrator terminal; captures the whole machine, including app/WMI metadata.
dotnet run --project .\ConsoleApp_SystemImpact -c Release --no-build -- --output C:\Traces\ImpactRun
# Short, labeled comparison capture; select the viewer process by executable name if auto-detection misses it.
dotnet run --project .\ConsoleApp_SystemImpact -c Release --no-build -- --seconds 30 --scenario webcam-viewer --camera-client WindowsCamera.exe --output C:\Traces\Camera
# Re-analyze without elevation or another capture:
dotnet run --project .\ConsoleApp_SystemImpact -c Release --no-build -- --analyze C:\Traces\ImpactRun\capture.etl --output C:\Traces\Reanalysis
```

Default output: `impact-results\<timestamp>_<pid>` under the current directory.
The directory contains the ETL plus the v3 outputs:

- `report.md` – Chinese report: all apps (sorted by impact), detail cards for apps with issues
  (frequency → time domain → DPC/ISR/WMI/thread/CSwitch evidence → issue/fix), process trees,
  per-process GPU engine time / memory, webcam viewer / Frame Server / DWM / USB-driver group
  summaries, and the system score with per-driver DPC/ISR table.
- `report.json` – the full `AppImpactReport` (apps with their processes, trees, system section).
- `apps.csv` / `processes.csv` – UTF-8 BOM, culture-independent numbers, unknown scores `N/A`.

Every observed process instance is scored, then grouped into apps by executable name.
Reused PIDs remain separate process instances.
Existing ETLs are never
overwritten by capture. Ctrl+C cancels the delay and stops only the owned session;
partial captures are not automatically scored. ETL files contain sensitive machine activity
(including command lines and thread names): keep them local and control access.
No automatic uploads or privilege escalation.

## v3 app scoring (`cpu-wake-poll-v3`)

Capture flags: PROCESS, THREAD, CSWITCH, DPC, INTERRUPT, IMAGE_LOAD (kernel image rundown
resolves DPC/ISR routines to driver names), `WithGpuMonitoring()` (DxgKrnl GPU/adapter rundown),
and the WMI-Activity provider.

GPU work is attributed to the process owning its DxgKrnl context. To avoid process totals
exceeding an engine's busy interval, overlapping submit-to-complete intervals from different
processes on the same engine are split evenly; this is an estimate, not hardware scheduler
time. Per-process GPU memory shows the maximum reported local/non-local usage. Webcam groups are heuristics: viewer matching uses
`--camera-client` or executable names containing `camera` / `ConsoleApp_UsbCameraTest`,
Frame Server uses executable/command-line names, DWM uses `dwm.exe`, and USB-driver cost sums
USB-named DPC/ISR routines. These are evidence groups, not proof that the viewer caused the
driver or compositor activity. GPU engine-ms may overlap across engines and is not additive
with CPU seconds.

1. **Scheduling walk (time domain).** CSwitch `OldThreadState`/`OldThreadWaitReason` classify
   each switch-in: previous switch-out *Waiting* → **wake**; *Ready/Standby/DeferredReady* →
   **preemption resume**, whose gap is exact ready-queue delay. Wait reasons are aggregated
   (kernel locks = WrKeyedEvent/WrResource/WrPushLock/WrMutex/WrFastMutex/WrGuardedMutex;
   WrAlertByThreadId is user-mode SRW/condition variable/WaitOnAddress and is not counted as lock contention).
2. **Periodicity (frequency first, then time domain).** Per process with ≥120 wakes, wake counts
   are binned (1 ms bins for ≥1 Hz, 100 ms bins for <1 Hz) and a Welch PSD is searched for up to
   8 peaks whose power is ≥4× (≥6× low band) the local median. Each peak f is then tested per
   thread for periods m/f (m = 1..4): the fraction of wakes followed by another wake one period
   later (±10%) is compared with Poisson chance; `excess = (match − chance)/(1 − chance)`.
   A period is **confirmed** if excess ≥ 0.4, chance < 0.9, and ≥10 cycles fit the lifetime; the
   period is refined to the median matched interval and the shortest near-equal period wins
   (the fundamental, not a harmonic). Unconfirmed peaks are shown with `?` and never scored.
3. **Scores** (0–100, health = 100 − impact, withheld if v2 data quality fails):
   - CPU = `100·(0.6·CPU s + 0.4·CPU s in saturated-core bins)/core-seconds` (v2 formula)
   - wake = `10·clamp((wakes/s − 10)/990)`
   - poll = `5·clamp(log10(confirmed Hz)/3)` (only confirmed, ≥1 Hz)
   App = all processes with the same exe name: CPU and wakes are summed, poll is the worst process.
   Process trees walk parent PIDs and stop at shell/service hosts (explorer, svchost, services, …).
4. **DPC/ISR are evidence, not app deductions.** Each interrupt is charged to the thread it
   interrupted ("stolen" time per app and driver). Driver totals, >100 µs / >1 ms counts and
   periodicity appear in the system section.
5. **Couplings.** App CPU vs other CPU, DPC, ISR and own WMI (100 ms bins) report coherence,
   frequency, phase (positive = second signal lags) and a Bonferroni-corrected null threshold
   (`1 − (0.05/M)^(1/(K−1))`, K from Hann 50%-overlap dof). Only significant in-phase (|φ| ≤ 60°)
   DPC/ISR couplings become low-severity issues; anti-phase is the interrupt-subtraction artifact.
6. **Issues** are threshold rules (CPU ≥5% machine or ≥0.5 core, wakes ≥100/s, confirmed polling,
   thread churn ≥1/s, ≥10 process starts, ≥30 WMI operations, ready delay ≥1 s with avg ≥1 ms,
   kernel-lock waits, paging waits, interrupt theft ≥5%) with generic fixes. All weights and
   thresholds are heuristics; use stacks (PROFILE/DISPATCHER flags) to find root causes.

## v2 model (still used for CPU attribution and system health)

## Signals and attribution

* Process/thread lifecycle callbacks distinguish reused PIDs/TIDs; each process instance has
  an identity `PID@startTicks`. Rundown is not interpreted as an exit.
* Consecutive matching CSwitch events reconstruct scheduled intervals on each logical core.
  Unmatched intervals and unobserved capture edges do not become invented CPU usage.
* DPC/ISR interval unions are subtracted from app scheduled residency. Remaining non-idle
  intervals are attributed through the thread and process lifetimes. DPC/ISR are counted
  as system load, including during idle-thread residency, never as a correlated app's cost.
  Separately reported DPC/ISR signals may overlap due to nesting; the busy signal uses their union.
* WMI event 11 counts local client operation starts. Event 24 counts notification registrations
  separately; registrations are not polling requests. Missing WMI events are not evidence
  that WMI has no cost. Provider execution is not reassigned to the client without causal evidence.
* Signals use exact overlap splitting into 100 ms bins, including the final partial bin.
  Byte-valued core IDs cannot fully represent processor-group systems above 256 logical CPUs;
  those captures are rejected.

## Time/frequency analysis

Each active app is compared against other CPU activity (own CPU removed), system DPC,
system ISR, and its own WMI starts. WMI starts are also compared with DPC/ISR.
Pearson and strongest absolute lag correlation search +/-2 seconds. Positive lag means
the second signal follows the first; this is not causal direction.

Welch uses radix-2 FFT, per-segment mean removal, Hann windows, 50% overlap, up to 256 samples,
and at least seven segments. Short traces use smaller segments down to 32. DC and Nyquist
are excluded; candidate frequency bins must have >=5% of each signal's peak spectral power.
Each candidate must also have energy support `(sum(segmentPower))^2 / sum(segmentPower^2)`
of at least seven in **each** signal. This rejects isolated shared events/bursts that otherwise
produce near-perfect coherence. This support measure is not independent degrees of freedom
(windows overlap), a confidence level, or a statistical significance test.
The report includes the maximum eligible magnitude-squared coherence, its frequency and
segment count. Constant/short/unsupported signals yield JSON `null`, never an invented zero.
With a 180-second capture the usual segment length is 25.6 seconds (0.0390625 Hz bin spacing).
100 ms aggregation does not resolve >=5 Hz or guarantee absence of aliasing.

Time-domain top five ranks direct CPU impact. Frequency top five ranks the strongest
estimable coherence across the listed comparisons: these are **association candidates,
not the five proven causes of slowness**. Coherence does not increase app responsibility scores.
No statistical significance or multiple-testing guarantee is claimed; shared timers,
trends and the observation process can cause apparent associations.

Up to five non-overlapping 25.6-second pressure windows are ranked by
`0.7 * saturatedCoreTimeFraction + 0.3 * min(1, interruptCoreTimeFraction / 0.1)`.
Saturation here means >=85% busy in a 100 ms core bin. Each window includes its top CPU
contributors and local time/frequency association to other CPU. These are pressure candidates,
not proven bottlenecks; short final windows may not support Welch.

## Scores (transparent heuristics, not calibrated performance measurements)

All scores are 0..100. System health and app health are higher-is-better;
the original app impact remains available as higher-is-more-load.
Let `C = captureSeconds * logicalCores`, `u` be each core-bin busy fraction, and
`p = clamp((u - 0.70) / 0.30, 0, 1)`.

* System health = `100 * (1 - 0.7 * sum(p * binSeconds) / C
  - 0.3 * sum(clamp(interruptFraction / 0.10, 0, 1) * binSeconds) / C)`.
* App impact = `100 * (0.6 * appCpuSeconds + 0.4 * pressureWeightedAppSeconds) / C`.
  Pressure weighting uses the core on which each app interval actually executed.
  This is absolute machine-capacity burden, not a percentile or a normalized leaderboard.
  A single-core-heavy app on a many-core machine can have a low machine-wide score.
* App health = `round(100 - appImpact, 2)`. This is the complement of the existing
  absolute burden score, not a rescaling of this run's worst app to 0.
  A score of 100 means no measured burden at the displayed precision, not proof of
  app responsiveness or efficiency. Both app scores are `null`/`N/A` when quality fails.

Scores are withheld (`null`/`N/A`) if scheduling coverage is below 95%, unknown non-idle
CPU exceeds 5%, or events/buffers were lost, switches mismatched, or interrupt times were
invalid. Missing required CPU signals produce an explicit analysis error. Reports include
quality counts and warnings. Scores do **not** cover GPU, disk, network, memory pressure,
ready queues, frame latency, or user experience. They cannot establish overall system/app
performance or causal bottlenecks without those additional measurements.
