# ConsoleApp_SrumCycles

Prototype of "plan 3": collect per-process CPU cycles and use them to split SRUM energy rows
(`powercfg /srumutil`) over the individual processes behind one AppId. It is self-contained
(pure P/Invoke, NativeAOT compatible) and does not depend on the QSoft.ETW library.
[ConsoleApp_CpuBurner](../ConsoleApp_CpuBurner/Program.cs) is the load generator for the experiment.

## Commands

All commands except `--self-test` require Administrator.

| Command | Purpose |
|---|---|
| `collect [--output DIR] [--interval SEC=60] [--seconds N]` | Real-time ETW + periodic sampling into `DIR\cycles-yyyyMMdd.jsonl` until Ctrl+C. |
| `allocate --cycles DIR [--srum FILE.csv] [--app SUBSTRING] [--max-delay SEC=360] [--output DIR]` | Splits SRUM CPU energy by cycles. Without `--srum` it runs `powercfg /srumutil /csv`. Writes `allocations.jsonl` and `summary.json`. |
| `e2e [--output DIR] [--interval SEC=10] [--phase-seconds 60] [--settle SEC=150]` | About 9 minutes: collector, three CpuBurner instances with known duty schedules, SRUM export, allocation and checks. Writes `e2e-report.json`. |
| `--self-test` | Offline checks: allocation math, ledger, curves, matching, CSV parsing, `SYSTEM_PROCESS_INFORMATION` layout. |

## Collector

- Real-time session `QSoft-SrumCycleCollector` on Microsoft-Windows-Kernel-Process
  (`{22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716}`, keyword `0x10`), events 1 (ProcessStart) and
  2 (ProcessStop), decoded by property name through TDH.
- Sampling is aligned to the wall clock. Every interval, one `NtQuerySystemInformation(SystemProcessInformation)`
  call returns every process's `CycleTime` (x64 offset `0x18`), `CreateTime` (`0x20`) and I/O transfer counts.
  The offsets are verified by the self-test against `QueryProcessCycleTime`/`GetProcessTimes`.
- On first sight, identity is resolved with `PROCESS_QUERY_LIMITED_INFORMATION` (with `SeDebugPrivilege`
  enabled): NT image path (`PROCESS_NAME_NATIVE`, the SRUM AppId format), package full name, token user
  SID, hosted services and, for svchost, the `-k` group read from the command line.
- The process key is `(Pid, CreateFt)`. The ETW `CreateTime` equals the SPI `CreateTime` exactly, so start,
  stop and sample records join without tolerance. `ProcessSequenceNumber` is kept as `Seq`.

JSONL record types (`T`): `session`/`end` (collector lifetime, ETW loss counters), `tick` (one sampling
pass, defines coverage), `start`/`stop` (ETW), `proc` (identity), and `sample` (cumulative cycles, written
only when changed, with `HeldUntilFt` marking the last pass at which the previous value was still seen).
FILETIME fields are raw UTC FILETIME values.

## Allocation

- `C_i(t)` is 0 before create, interpolated linearly between samples, and fixed to the ProcessStop
  `CPUCycleCount` after exit. Outside collector coverage (gaps longer than 2.5 intervals) it is unknown.
- SRUM rows are grouped by `(AppId, UserId, TimeStamp)`. `TimeStamp` is the UTC interval end. The window starts
  at `TimeStamp - Σ TimeInMSec`, because SRUM splits a minute into consecutive sub-intervals stamped with the
  same time when the state changes (observed: screen off produced rows of 3,856 ms and 50,585 ms both stamped
  at the same minute).
- Candidates: an AppId starting with `\` is matched to the image path; ` [Group] [Service]` tags select the
  svchost hosting that service (per-user instances such as `CDPUserSvc_18ea0b` match the template name
  `CDPUserSvc`). A group-only tag (`svchost.exe [netsvcs]`) selects hosts with that `-k` group,
  except hosts whose service has its own `[Group] [Service]` row. Other AppIds match the package full name
  or the pseudo-processes `System`, `Registry` and `MemCompression`. Tracks without an image path fall back to
  the file name. The user SID must match when both sides have one.
- Ledger per `(AppId, UserId, process)`: each row takes the cycles from the previous row's end (at most
  `--max-delay` back) to `TimeStamp`. A process's cycles are consumed only once, so a row that SRUM reports
  late, after the process has exited, still reaches it.
- `CPU` and `CPUAttributed` are split with the largest-remainder method, so the parts sum exactly to the row.
  `CPUWorkOnBehalf`, Display, Disk, Network, `EnergyLoss` and `Total` are not split.
- Shared bucket (`Method: "shared"`): `no-matching-process`, `missing-collector-data` (window outside
  coverage, or a process that already existed before its first sample) and `zero-cycles`.
- Reconciliation: Pearson correlation of each interval's CPU vs Σcycles, an offset scan (−120 to +120 s) and,
  per app, CPU per gigacycle with its coefficient of variation.

## Findings (Windows 11, Intel hybrid CPU)

- Three burners shared one AppId (A: 1 thread, B: 2 threads, C: 2 threads for 20 s). Every per-row split
  matched the duty schedules within 2.5 percentage points, for example A 52% / B 48% vs 50/50 expected, and
  A 9% / B 68% / C 23% vs 9/68/23. The short-lived C received its row and nothing after its cycles had been consumed.
- The ProcessStop `CPUCycleCount` is 0.005-0.016% lower than `QueryProcessCycleTime` after exit, because the
  event is logged before teardown finishes.
- Offset scan over all apps (~1,300 intervals per offset, two runs): r = 0.91-0.93 at 0 s, 0.89-0.91 at ±30 s,
  0.85-0.86 at ±60 s. `TimeStamp` is the UTC interval end. The 5 burner intervals alone are too few to rank offsets.
- CPU energy per gigacycle varies from about 800 to 1,800 between minutes (single-thread minutes on P-cores at
  high frequency cost more per cycle). So whole-run energy shares differ from whole-run cycle shares by up to
  7 points (A: 32.9% of cycles, 39.7% of energy) even though every row is split correctly.
- Without `SeDebugPrivilege`, dwm.exe, csrss.exe and most service processes have no readable image path or SID.
  With it, plus `-k` groups and per-user service templates, 1,523 of 1,757 rows (86% of CPU energy) were
  allocated. The remaining shared rows: `missing-collector-data` (188, mostly minutes before the collector
  started or before a process's first sample), and `no-matching-process` (45), only for AppIds without a
  process: `EMI_RAPL_Package0_*` (energy meters), `System Interrupts` and `Unknown`.

## Limitations

- Only data recorded after the collector started can be allocated, and the collector is a console app,
  not yet a Windows service. Gaps while it is not running go to the shared bucket.
- The SRUM Application Resource Usage table (`{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}`, foreground/background
  cycle time) is not exported by `powercfg /srumutil`. Reading it requires parsing `SRUDB.dat` (ESE), so
  reconciliation currently relies on the energy-vs-cycles correlation.
- Cycles approximate energy: on hybrid CPUs and with frequency changes the energy per cycle differs.
- Display (foreground window time) and Network (Kernel-Network bytes) are not allocated.
- x64 only (`SYSTEM_PROCESS_INFORMATION` offsets).
