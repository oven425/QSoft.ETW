using System.Globalization;
using System.Text;

namespace ConsoleApp_SystemImpact;

internal static class ReportWriter
{
    public static void WriteAppsCsv(TextWriter w, AppImpactReport r)
    {
        Dictionary<string, GpuProcessReport> gpu = r.Gpu.Processes.ToDictionary(p => p.Identity, StringComparer.Ordinal);
        w.WriteLine("Rank,App,HealthScore,ImpactScore,CpuImpact,WakeImpact,PollImpact,MachineCpuPercent,CpuSeconds,GpuEngineMs,PeakLocalGpuMemoryBytes,PeakNonLocalGpuMemoryBytes,PressuredCpuSeconds,WakesPerSecond,PollPeriodMs,PeriodicityConfirmed,Processes,StartedInTrace,ReadySeconds,StolenMs,WmiOperations,Issues");
        foreach (var a in r.Apps)
        {
            var members = a.Processes.Select(p => gpu.GetValueOrDefault(p.Identity)).Where(p => p is not null).ToArray();
            w.WriteLine(string.Join(",",
                I(a.Rank), AppCsv.Cell(a.Name), AppCsv.Number(a.HealthScore), AppCsv.Number(a.ImpactScore),
                AppCsv.Number(a.Parts.CpuImpact), AppCsv.Number(a.Parts.WakeImpact), AppCsv.Number(a.Parts.PollImpact),
                AppCsv.Number(a.MachineCpuPercent), AppCsv.Number(a.CpuSeconds),
                r.Gpu.Engines.Count > 0 ? AppCsv.Number(members.Sum(p => p!.ExecutionMs)) : "N/A",
                BytesCsv(members.Where(p => p!.PeakLocalMemoryBytes.HasValue).Select(p => p!.PeakLocalMemoryBytes!.Value).DefaultIfEmpty().Max(), members.Any(p => p!.PeakLocalMemoryBytes.HasValue)),
                BytesCsv(members.Where(p => p!.PeakNonLocalMemoryBytes.HasValue).Select(p => p!.PeakNonLocalMemoryBytes!.Value).DefaultIfEmpty().Max(), members.Any(p => p!.PeakNonLocalMemoryBytes.HasValue)),
                AppCsv.Number(a.PressuredCpuSeconds),
                AppCsv.Number(a.Scheduling.WakesPerSecond), AppCsv.Number(a.Periodicity?.PeriodMs),
                a.Periodicity?.Confirmed == true ? "true" : "false", I(a.ProcessCount), I(a.StartedInTrace),
                AppCsv.Number(a.Scheduling.ReadySeconds), AppCsv.Number(a.Interrupts.StolenMs), I(a.Wmi.Operations),
                AppCsv.Cell(string.Join("; ", a.Issues.Select(i => $"[{i.Severity}] {i.Title}")))));
        }
    }

    public static void WriteProcessesCsv(TextWriter w, AppImpactReport r)
    {
        Dictionary<string, GpuProcessReport> gpu = r.Gpu.Processes.ToDictionary(p => p.Identity, StringComparer.Ordinal);
        w.WriteLine("App,Identity,Pid,ParentPid,HealthScore,ImpactScore,CpuImpact,WakeImpact,PollImpact,CpuSeconds,GpuEngineMs,PeakLocalGpuMemoryBytes,PeakNonLocalGpuMemoryBytes,PressuredCpuSeconds,Wakes,WakesPerSecond,PollPeriodMs,PeriodicityConfirmed,StartedInTrace,ExitedInTrace,CommandLine");
        foreach (var a in r.Apps)
            foreach (var p in a.Processes)
            {
                gpu.TryGetValue(p.Identity, out GpuProcessReport? g);
                w.WriteLine(string.Join(",",
                    AppCsv.Cell(a.Name), AppCsv.Cell(p.Identity), I(p.Pid), I(p.ParentPid),
                    AppCsv.Number(p.HealthScore), AppCsv.Number(p.ImpactScore), AppCsv.Number(p.Parts.CpuImpact),
                    AppCsv.Number(p.Parts.WakeImpact), AppCsv.Number(p.Parts.PollImpact), AppCsv.Number(p.CpuSeconds),
                    r.Gpu.Engines.Count > 0 ? AppCsv.Number(g?.ExecutionMs ?? 0) : "N/A",
                    BytesCsv(g?.PeakLocalMemoryBytes), BytesCsv(g?.PeakNonLocalMemoryBytes),
                    AppCsv.Number(p.PressuredCpuSeconds), I(p.Wakes), AppCsv.Number(p.WakesPerSecond),
                    AppCsv.Number(p.Periodicity?.PeriodMs), p.Periodicity?.Confirmed == true ? "true" : "false",
                    p.StartedInTrace ? "true" : "false", p.ExitedInTrace ? "true" : "false", AppCsv.Cell(p.CommandLine)));
            }
    }

    public static string Markdown(AppImpactReport r)
    {
        StringBuilder s = new();
        s.AppendLine($"# 系統影響分析（{r.ScoreModel}）");
        s.AppendLine();
        s.AppendLine($"- **情境**：{Md(r.Scenario)}；**Camera client selector**：{Md(r.CameraClient ?? "auto")}");
        s.AppendLine($"擷取 {r.DurationSeconds:F1} 秒（{r.StartUtc:yyyy-MM-dd HH:mm:ss}–{r.EndUtc:HH:mm:ss} UTC），{r.ProcessorCount} 顆邏輯核心；CSwitch {r.SwitchCount:N0}、DPC/ISR {r.InterruptCount:N0}、WMI {r.WmiCount:N0}、核心映像 {r.KernelImageCount}。");
        s.AppendLine("分數：健康 = 100 − 影響；影響 = CPU 扣分 + 喚醒扣分（最多 10）+ 已確認輪詢扣分（最多 5）。DPC/ISR 只當證據，不扣 app 分數。");
        s.AppendLine();
        s.AppendLine("## 1. 全部 App（依影響排序）");
        s.AppendLine();
        s.AppendLine("| # | App | 健康 | 影響 | CPU% | 喚醒/s | 週期（頻域→時域） | 行程數 | 問題 |");
        s.AppendLine("|---:|---|---:|---:|---:|---:|---|---:|---|");
        foreach (var a in r.Apps)
            s.AppendLine($"| {a.Rank} | {Md(a.Name)} | {F(a.HealthScore)} | {F(a.ImpactScore)} | {a.MachineCpuPercent:F2} | {a.Scheduling.WakesPerSecond:F1} | {Period(a.Periodicity)} | {a.ProcessCount} | {IssueSummary(a.Issues)} |");
        s.AppendLine();

        s.AppendLine("## 2. 有問題的 App 詳細（頻域 → 時域 → 問題／解法）");
        foreach (var a in r.Apps.Where(a => a.Issues.Count > 0))
        {
            s.AppendLine();
            s.AppendLine($"### #{a.Rank} {Md(a.Name)} — 健康 {F(a.HealthScore)}／影響 {F(a.ImpactScore)}（CPU {a.Parts.CpuImpact:F2} + 喚醒 {a.Parts.WakeImpact:F2} + 輪詢 {a.Parts.PollImpact:F2}）");
            s.AppendLine();
            s.AppendLine($"- **頻域**：{Frequency(a)}");
            var sig = a.Couplings.Where(c => c.Significant).OrderByDescending(c => c.Coherence).Take(3).ToList();
            s.AppendLine(sig.Count == 0 ? "- **同節奏（coherence）**：無顯著耦合"
                : $"- **同節奏（coherence）**：{string.Join("；", sig.Select(Coupling))}");
            s.AppendLine($"- **CPU**：{a.CpuSeconds:F2} 秒（整機 {a.MachineCpuPercent:F2}%），飽和時段 {a.PressuredCpuSeconds:F2} 秒；{a.ProcessCount} 個行程（擷取中啟動 {a.StartedInTrace}、結束 {a.ExitedInTrace}）");
            var sc = a.Scheduling;
            s.AppendLine($"- **CSwitch**：切入 {sc.SwitchIns:N0} 次，喚醒 {sc.Wakes:N0}（{sc.WakesPerSecond:F1}/s），平均每次執行 {sc.AverageRunMs:F3} ms；被搶占 {sc.Preemptions:N0} 次，排隊 {sc.ReadySeconds:F3} 秒（平均 {sc.AverageReadyMs:F2} ms，最長 {sc.MaxReadyMs:F1} ms）");
            s.AppendLine($"- **等待原因**：{Join(sc.TopWaitReasons, x => $"{x.Name} {x.Value:N0}")}（核心鎖 {sc.LockWaits:N0}、分頁 {sc.PagingWaits:N0}）");
            s.AppendLine($"- **Thread**：觀察到 {sc.ThreadsSeen} 個，擷取中建立 {sc.ThreadStarts}／結束 {sc.ThreadExits}；最忙：{Join(sc.TopThreads, t => $"{AppAnalysis.ThreadLabel(t.Pid, t.Tid, t.Name)} {t.CpuSeconds:F2}s／{t.WakesPerSecond:F1} 喚醒/s")}");
            s.AppendLine($"- **DPC/ISR**：執行中被打斷 {a.Interrupts.StolenMs:F1} ms（{a.Interrupts.StolenPercentOfRun:F2}%），{Join(a.Interrupts.TopDriversMs, x => $"{x.Name} {x.Value:F1} ms")}");
            s.AppendLine($"- **WMI**：{a.Wmi.Operations} 次操作、{a.Wmi.Registrations} 個註冊；{Join(a.Wmi.TopOperations, x => $"{Md(x.Name)} ×{x.Value:F0}")}");
            if (a.Spawners.Count > 0)
                s.AppendLine($"- **啟動者**：{Join(a.Spawners, x => $"{Md(x.Name)} ×{x.Value:F0}")}");
            s.AppendLine();
            s.AppendLine("| 嚴重度 | 問題 | 證據 | 解決方法 |");
            s.AppendLine("|---|---|---|---|");
            foreach (var i in a.Issues)
                s.AppendLine($"| {i.Severity} | {Md(i.Title)} | {Md(i.Evidence)} | {Md(i.Fix)} |");
        }
        s.AppendLine();

        s.AppendLine("## 3. 行程樹（≥2 個行程）");
        s.AppendLine();
        s.AppendLine("| 根行程 | PID | 健康 | 影響 | 行程數 | CPU 秒 | 喚醒/s | 成員 |");
        s.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var t in r.Trees)
            s.AppendLine($"| {Md(t.Root)} | {t.RootPid} | {F(t.HealthScore)} | {F(t.ImpactScore)} | {t.ProcessCount} | {t.CpuSeconds:F2} | {t.WakesPerSecond:F1} | {Md(Join(t.Members.Take(6).ToList(), x => $"{x.Name}×{x.Value:F0}"))} |");
        s.AppendLine();

        s.AppendLine("## 4. GPU 與 webcam 工作負載歸因");
        s.AppendLine();
        s.AppendLine($"- Adapter：{(r.Gpu.Adapters.Count == 0 ? "無" : string.Join("；", r.Gpu.Adapters.Select(Md)))}；Engine executions {r.Gpu.EngineExecutionCount:N0}，未歸屬 {r.Gpu.UnattributedExecutionCount:N0}，無法解析 DMA submit {r.Gpu.UnresolvedDmaSubmitCount:N0}。");
        s.AppendLine("- GPU 工作時間以 DxgKrnl submit→complete 區間估算；同一 Engine 同時重疊的不同 Process 區間會平均分攤，避免行程加總超過 Engine 忙碌時間。多個 Engine 可同時忙碌，GPU Engine ms 不等同 GPU 整體使用率或因果關係。");
        s.AppendLine();
        s.AppendLine("| GPU Engine | 忙碌 ms | 擷取區間使用率 | 有工作的行程數 |");
        s.AppendLine("|---|---:|---:|---:|");
        foreach (var e in r.Gpu.Engines)
            s.AppendLine($"| {Md(e.Name)} | {e.BusyMs:F1} | {e.UtilizationPercent:F2}% | {e.ProcessCount} |");
        if (r.Gpu.Engines.Count == 0) s.AppendLine("| 無 GPU 資料（需 DxgKrnl GPU monitoring rundown） | — | — | — |");
        s.AppendLine();
        s.AppendLine("| PID | Process | GPU Engine ms | Engine 分佈 | Local GPU memory peak | Non-local GPU memory peak |");
        s.AppendLine("|---:|---|---:|---|---:|---:|");
        foreach (var p in r.Gpu.Processes)
            s.AppendLine($"| {p.Pid} | {Md(p.Name)} | {p.ExecutionMs:F1} | {Md(Join(p.Engines, x => $"{x.Name} {x.Value:F1} ms"))} | {Bytes(p.PeakLocalMemoryBytes)} | {Bytes(p.PeakNonLocalMemoryBytes)} |");
        if (r.Gpu.Processes.Count == 0) s.AppendLine("| — | 無 GPU 工作行程 | — | — | — | — |");
        s.AppendLine();
        s.AppendLine("### Webcam workload（自動分類為觀察線索，非因果證明）");
        s.AppendLine();
        s.AppendLine("- Camera Frame Server 常與其他服務共用 svchost.exe；此列 CPU 是整個該 svchost 行程的 CPU，不一定全是相機服務。");
        s.AppendLine("| 元件 | CPU 秒 | GPU Engine ms | USB driver DPC/ISR ms | Process / driver |");
        s.AppendLine("|---|---:|---:|---:|---|");
        foreach (var c in r.WebcamCosts)
            s.AppendLine($"| {Md(c.Component)} | {c.CpuSeconds:F3} | {c.GpuExecutionMs:F1} | {c.DpcIsrMs:F1} | {Md(Join(c.Processes, x => x))} |");
        s.AppendLine();

        var sys = r.System;
        s.AppendLine($"## 5. 系統分數：{F(sys.HealthScore)} / 100");
        s.AppendLine();
        s.AppendLine($"- CPU 飽和扣 {sys.CpuPressurePenalty:F2}、中斷扣 {sys.InterruptPenalty:F2}");
        s.AppendLine($"- CPU 忙碌 {sys.CpuBusyPercent:F2}%、DPC/ISR {sys.InterruptPercent:F2}%、全系統喚醒 {sys.WakesPerSecond:F0}/s");
        s.AppendLine($"- 排程覆蓋率 {sys.CoveragePercent:F2}%、未知 CPU {sys.UnknownCpuPercent:F2}%、壓力區段 {sys.PressureWindows.Count} 個");
        s.AppendLine();
        if (sys.Timeline.Count > 0)
        {
            s.AppendLine($"每秒系統分數：最低 {sys.Timeline.Min(x => x.HealthScore):F2}、中位數 {Median(sys.Timeline.Select(x => x.HealthScore)):F2}；最低 10 秒：");
            s.AppendLine();
            s.AppendLine("| 秒 | 分數 | CPU 飽和扣 | 中斷扣 | CPU 忙碌 % | 最忙核心 % | DPC % | ISR % | 該秒 CPU 前三（核心秒） | 系統事件 |");
            s.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---|---|");
            foreach (var x in sys.Timeline.OrderBy(x => x.HealthScore).ThenBy(x => x.OffsetSeconds).Take(10))
            {
                string ev = string.Join("；", sys.Events.Where(e => !e.Rundown && Math.Abs(e.OffsetSeconds - x.OffsetSeconds) <= 1)
                    .Take(2).Select(e => e.Text));
                s.AppendLine($"| {x.OffsetSeconds:F0} | {x.HealthScore:F2} | {x.CpuPressurePenalty:F2} | {x.InterruptPenalty:F2} | {x.BusyPercent:F1} | {x.PeakCoreBusyPercent:F0} | {x.DpcPercent:F2} | {x.IsrPercent:F2} | {Md(Join(x.TopProcesses, p => $"{p.Name} {p.Value:F2}"))} | {Md(ev)} |");
            }
            s.AppendLine();
        }
        var live = sys.Events.Where(e => !e.Rundown).ToList();
        s.AppendLine($"### 系統電源／散熱事件（{live.Count} 筆變更、{sys.Events.Count - live.Count} 筆狀態快照）");
        s.AppendLine();
        if (sys.Events.Count == 0)
            s.AppendLine("ETL 沒有 Kernel-Power／Processor-Power／UMPO／ACPI 事件，無法排除睡眠、AC/DC、電源模式或降頻的影響。");
        else
        {
            s.AppendLine("| 秒 | 類別 | 事件 ID | 說明 |");
            s.AppendLine("|---:|---|---:|---|");
            foreach (var e in live.Take(40).Concat(sys.Events.Where(e => e.Rundown).Take(20)))
                s.AppendLine($"| {e.OffsetSeconds:F1} | {e.Category}{(e.Rundown ? "（快照）" : "")} | {e.EventId} | {Md(e.Text)} |");
        }
        s.AppendLine();
        s.AppendLine("| 驅動程式 | DPC | ISR | 總時間 ms | 最長 µs | >100µs | >1ms | 週期 |");
        s.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var d in sys.Drivers.Take(12))
            s.AppendLine($"| {Md(d.Driver)} | {d.DpcCount:N0} | {d.IsrCount:N0} | {d.TotalMs:F1} | {d.MaxUs:F0} | {d.Over100Us:N0} | {d.Over1Ms:N0} | {Period(d.Periodicity)} |");
        s.AppendLine();
        if (sys.Issues.Count > 0)
        {
            s.AppendLine("| 嚴重度 | 系統問題 | 證據 | 解決方法 |");
            s.AppendLine("|---|---|---|---|");
            foreach (var i in sys.Issues)
                s.AppendLine($"| {i.Severity} | {Md(i.Title)} | {Md(i.Evidence)} | {Md(i.Fix)} |");
            s.AppendLine();
        }
        s.AppendLine("### 方法與限制");
        foreach (var w in sys.Warnings) s.AppendLine($"- {w}");
        return s.ToString();
    }

    private static string Frequency(AppScore a) => a.Periodicity is not { } p ? "無週期峰值（或喚醒 <120 次未分析）"
        : p.Prominence > 0
            ? $"{p.Band}峰值 {p.PeakHz:F2} Hz（突出 {p.Prominence:F1} 倍）→ 時域{(p.Confirmed ? "確認" : "未確認")}：{Md(p.Source)} 週期 {p.PeriodMs:F1} ms，符合 {p.Match:P0}（隨機 {p.Expected:P0}，excess {p.Excess:F2}）"
            : $"{p.Band}：{Md(p.Source)} 週期 {p.PeriodMs:F3} ms，符合 {p.Match:P0}（隨機 {p.Expected:P0}）";

    private static string Period(Periodicity? p) => p switch
    {
        null => "—",
        { Confirmed: true } => $"✔ {p.PeriodMs:F1} ms",
        _ => $"? {p.PeakHz:F2} Hz（未確認）"
    };

    private static string Coupling(Association c) =>
        $"{c.Source} {c.FrequencyHz:F2} Hz C={c.Coherence:F2}（門檻 {c.Threshold:F2}，相位 {c.PhaseDegrees:F0}°）";

    private static string IssueSummary(List<Issue> issues) => issues.Count == 0 ? "—"
        : Md(string.Join("、", issues.Take(3).Select(i => $"{i.Severity}:{i.Title}")) + (issues.Count > 3 ? $" 等{issues.Count}項" : ""));

    private static string Join<T>(List<T> items, Func<T, string> f) => items.Count == 0 ? "—" : string.Join("、", items.Select(f));
    private static string F(double? v) => v?.ToString("F2", CultureInfo.InvariantCulture) ?? "N/A";
    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Bytes(ulong? v) => v is ulong value ? $"{value / (1024d * 1024):F1} MiB" : "N/A";
    private static string BytesCsv(ulong? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "N/A";
    private static string BytesCsv(ulong v, bool available) => available ? v.ToString(CultureInfo.InvariantCulture) : "N/A";
    private static string Md(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? 0 : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
