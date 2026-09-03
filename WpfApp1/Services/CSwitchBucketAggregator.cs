using QSoft.ETW;

namespace WpfApp1.Services;

/// <summary>
/// 單一執行緒在某個時間桶(bucket)內的 CSwitch 聚合統計,用來取代「每執行緒保留完整 CSwitchEventInfo 清單」的做法。
/// </summary>
internal readonly record struct CSwitchThreadBucket(
    uint ProcessId,
    uint ThreadId,
    DateTime BucketStartUtc,
    DateTime BucketEndUtc,
    int SwitchInCount,
    int SwitchOutCount,
    long RunDurationTicks,
    int? MinPriority,
    int? MaxPriority,
    int IdealProcessorMismatchCount,
    IReadOnlyDictionary<int, int> WaitReasonHistogram);

/// <summary>單一 CPU 核心在某個時間桶(bucket)內的排程統計。</summary>
internal readonly record struct CSwitchProcessorBucket(
    byte ProcessorNumber,
    DateTime BucketStartUtc,
    DateTime BucketEndUtc,
    int ContextSwitchCount,
    int DistinctThreadCount,
    long BusyDurationTicks,
    long IdleDurationTicks);

/// <summary>執行緒整段生命週期的 CPU 使用彙總(取代舊版逐筆清單重算的 ThreadCpuSummary)。StartedAt/EndedAt/DurationTicks
/// 在從未結算出任何執行區間時為 null,但 ContextSwitchCount 仍會反映實際切換次數(與舊版 ContextSwitchJson
/// 對應欄位的計數語意一致)。</summary>
internal readonly record struct ThreadCpuUsage(DateTime? StartedAt, DateTime? EndedAt, long? DurationTicks, int ContextSwitchCount);

/// <summary>
/// 以固定時間窗格(bucket)串流彙總 CSwitch 事件,取代「每執行緒保留完整 CSwitchEventInfo 清單直到 ThreadStop 才彙總」的做法。
/// 記憶體用量為 O(活躍執行緒數 + CPU 核心數),與追蹤期間實際發生的 CSwitch 事件總數無關,
/// 且輸出的資料列數為 O(時間桶數 × 活躍執行緒/CPU 數),遠小於原始事件數,可大幅降低寫入 SQLite 的量。
///
/// 演算法重點:
/// - SwitchIn/SwitchOut 次數、Priority、WaitReason、IdealProcessorMismatch 屬於「事件當下時間點」的統計,
///   直接歸到事件時間戳記所在的桶(不須切分)。
/// - RunDurationTicks(執行緒實際佔用 CPU 的時間)屬於「區間量」(從切入到切出),若區間跨越桶邊界,
///   會依重疊比例精確切分到每個跨越的桶,並同時分別餵給「執行緒桶」與「該次執行所在 CPU 核心的桶」。
/// - 只有呼叫過 <see cref="RegisterThread"/> 的執行緒才會產生執行緒桶(對應 ThreadStart/ThreadDCStart),
///   但 CPU 核心桶的切換次數/相異執行緒數對所有事件都會計入,不受此限制。
/// </summary>
internal sealed class CSwitchBucketAggregator(TimeSpan bucketSize, uint idleThreadId = 0)
{
    private readonly long _bucketTicks = bucketSize.Ticks > 0
        ? bucketSize.Ticks
        : throw new ArgumentOutOfRangeException(nameof(bucketSize), "Bucket 大小必須大於零。");

    private readonly Dictionary<uint, ThreadState> _threads = [];
    private readonly Dictionary<byte, ProcessorAccumulator> _processors = [];

    public int UnmatchedCpuIntervalCount { get; private set; }

    public int IncompleteCpuIntervalCount { get; private set; }

    /// <summary>每當一個執行緒桶被結算(時間窗結束或執行緒/追蹤結束)時觸發。</summary>
    public event Action<CSwitchThreadBucket>? ThreadBucketFlushed;

    /// <summary>每當一個 CPU 核心桶被結算時觸發。</summary>
    public event Action<CSwitchProcessorBucket>? ProcessorBucketFlushed;

    public void Reset()
    {
        _threads.Clear();
        _processors.Clear();
        UnmatchedCpuIntervalCount = 0;
        IncompleteCpuIntervalCount = 0;
    }

    /// <summary>對應 ThreadStart/ThreadDCStart,開始追蹤這個執行緒的 CSwitch 統計。</summary>
    public void RegisterThread(uint threadId, uint processId)
    {
        _threads[threadId] = new ThreadState(processId);
    }

    /// <summary>處理一筆 CSwitch 事件,增量更新對應的執行緒桶與 CPU 核心桶。</summary>
    public void OnCSwitch(in CSwitchEventInfo data)
    {
        DateTime eventBucketStart = AlignToBucket(data.Timestamp);
        _threads.TryGetValue(data.OldThreadId, out ThreadState? oldThreadState);
        _threads.TryGetValue(data.NewThreadId, out ThreadState? newThreadState);

        // 1) 結算 OldThreadId 先前「切入」留下的執行區間(區間量,依桶邊界精確切分)。
        if (oldThreadState is not null)
        {
            if (oldThreadState.RunningSinceByProcessor.Remove(data.ProcessorNumber, out DateTime startedAt))
            {
                if (data.Timestamp >= startedAt)
                {
                    CloseRunInterval(data.OldThreadId, oldThreadState, data.ProcessorNumber, startedAt, data.Timestamp);
                }
                else
                {
                    UnmatchedCpuIntervalCount++;
                }
            }
            else
            {
                // 從未記錄過切入時間(例如追蹤開始前就已在執行),無法結算區間。
                UnmatchedCpuIntervalCount++;
            }
        }

        // 2) 事件當下時間點的統計:SwitchOut / Priority / WaitReason / IdealProcessorMismatch。
        if (oldThreadState is not null)
        {
            CSwitchThreadBucketAccumulator oldBucket = GetOrCreateThreadBucket(data.OldThreadId, oldThreadState, eventBucketStart);
            oldBucket.SwitchOutCount++;
            oldThreadState.ContextSwitchCount++;
            UpdatePriority(oldBucket, data.OldThreadPriority);
            oldBucket.WaitReasonHistogram[data.OldThreadWaitReason] =
                oldBucket.WaitReasonHistogram.GetValueOrDefault(data.OldThreadWaitReason) + 1;
            if (data.OldThreadWaitIdealProcessor != data.ProcessorNumber)
            {
                oldBucket.IdealProcessorMismatchCount++;
            }
        }

        // 3) NewThreadId 的 SwitchIn 統計,並記錄「開始執行」時間供下次切出時結算區間。
        if (newThreadState is not null)
        {
            CSwitchThreadBucketAccumulator newBucket = GetOrCreateThreadBucket(data.NewThreadId, newThreadState, eventBucketStart);
            newBucket.SwitchInCount++;
            newThreadState.ContextSwitchCount++;
            UpdatePriority(newBucket, data.NewThreadPriority);

            if (!newThreadState.RunningSinceByProcessor.TryAdd(data.ProcessorNumber, data.Timestamp))
            {
                // 同一顆 CPU 上,這個執行緒還沒切出就又切入(遺漏事件或資料異常):以新時間覆蓋並記一筆。
                IncompleteCpuIntervalCount++;
                newThreadState.RunningSinceByProcessor[data.ProcessorNumber] = data.Timestamp;
            }
        }

        // 4) CPU 核心桶:切換次數與出現過的相異執行緒,不受執行緒是否被追蹤限制。
        ProcessorAccumulator processorBucket = GetOrCreateProcessorBucket(data.ProcessorNumber, eventBucketStart);
        processorBucket.ContextSwitchCount++;
        processorBucket.DistinctThreads.Add(data.OldThreadId);
        processorBucket.DistinctThreads.Add(data.NewThreadId);
    }

    /// <summary>
    /// 對應 ThreadStop/ThreadDCStop(或追蹤結束時仍未收到 ThreadStop 的情形),
    /// 結算尚未結束的執行區間並沖出目前桶,回傳整段生命週期的 CPU 使用彙總。
    /// </summary>
    public ThreadCpuUsage? CloseThread(uint threadId, DateTime endedAtUtc)
    {
        if (!_threads.Remove(threadId, out ThreadState? state))
        {
            return null;
        }

        foreach ((byte processorNumber, DateTime startedAt) in state.RunningSinceByProcessor)
        {
            if (endedAtUtc >= startedAt)
            {
                CloseRunInterval(threadId, state, processorNumber, startedAt, endedAtUtc);
            }
            else
            {
                UnmatchedCpuIntervalCount++;
            }
        }

        if (state.Current is CSwitchThreadBucketAccumulator current)
        {
            FlushThreadBucket(threadId, current);
        }

        return new ThreadCpuUsage(
            state.FirstRunStartedAtUtc,
            state.LastRunEndedAtUtc,
            state.FirstRunStartedAtUtc is null ? null : state.TotalDurationTicks,
            state.ContextSwitchCount);
    }

    /// <summary>追蹤全部結束時呼叫,把所有 CPU 核心桶尚未沖出的殘餘資料沖出。</summary>
    public void FlushRemainingProcessorBuckets()
    {
        foreach ((byte processorNumber, ProcessorAccumulator accumulator) in _processors)
        {
            EmitProcessorBucket(processorNumber, accumulator);
        }

        _processors.Clear();
    }

    private void CloseRunInterval(uint threadId, ThreadState threadState, byte processorNumber, DateTime startedAt, DateTime endedAt)
    {
        threadState.TotalDurationTicks = checked(threadState.TotalDurationTicks + (endedAt - startedAt).Ticks);
        threadState.FirstRunStartedAtUtc = threadState.FirstRunStartedAtUtc is null || startedAt < threadState.FirstRunStartedAtUtc
            ? startedAt
            : threadState.FirstRunStartedAtUtc;
        threadState.LastRunEndedAtUtc = threadState.LastRunEndedAtUtc is null || endedAt > threadState.LastRunEndedAtUtc
            ? endedAt
            : threadState.LastRunEndedAtUtc;

        bool isIdle = threadId == idleThreadId;
        DateTime cursor = startedAt;
        while (cursor < endedAt)
        {
            DateTime sliceBucketStart = AlignToBucket(cursor);
            DateTime sliceBucketEnd = sliceBucketStart.AddTicks(_bucketTicks);
            DateTime sliceEnd = sliceBucketEnd < endedAt ? sliceBucketEnd : endedAt;
            long sliceTicks = (sliceEnd - cursor).Ticks;

            CSwitchThreadBucketAccumulator threadBucket = GetOrCreateThreadBucket(threadId, threadState, sliceBucketStart);
            threadBucket.RunDurationTicks += sliceTicks;

            ProcessorAccumulator processorBucket = GetOrCreateProcessorBucket(processorNumber, sliceBucketStart);
            if (isIdle)
            {
                processorBucket.IdleDurationTicks += sliceTicks;
            }
            else
            {
                processorBucket.BusyDurationTicks += sliceTicks;
            }

            cursor = sliceEnd;
        }
    }

    private CSwitchThreadBucketAccumulator GetOrCreateThreadBucket(uint threadId, ThreadState state, DateTime bucketStart)
    {
        if (state.Current is CSwitchThreadBucketAccumulator current)
        {
            if (current.BucketStartUtc == bucketStart)
            {
                return current;
            }

            if (bucketStart < current.BucketStartUtc)
            {
                // 理論上事件應依時間遞增到達;若真的發生輕微逆序,併入目前桶以避免資料遺失。
                return current;
            }

            FlushThreadBucket(threadId, current);
        }

        CSwitchThreadBucketAccumulator created = new(state.ProcessId, bucketStart);
        state.Current = created;
        return created;
    }

    private ProcessorAccumulator GetOrCreateProcessorBucket(byte processorNumber, DateTime bucketStart)
    {
        if (_processors.TryGetValue(processorNumber, out ProcessorAccumulator? current))
        {
            if (current.BucketStartUtc == bucketStart)
            {
                return current;
            }

            if (bucketStart < current.BucketStartUtc)
            {
                return current;
            }

            EmitProcessorBucket(processorNumber, current);
        }

        ProcessorAccumulator created = new(bucketStart);
        _processors[processorNumber] = created;
        return created;
    }

    private void FlushThreadBucket(uint threadId, CSwitchThreadBucketAccumulator accumulator)
    {
        ThreadBucketFlushed?.Invoke(new CSwitchThreadBucket(
            accumulator.ProcessId,
            threadId,
            accumulator.BucketStartUtc,
            accumulator.BucketStartUtc.AddTicks(_bucketTicks),
            accumulator.SwitchInCount,
            accumulator.SwitchOutCount,
            accumulator.RunDurationTicks,
            accumulator.MinPriority,
            accumulator.MaxPriority,
            accumulator.IdealProcessorMismatchCount,
            accumulator.WaitReasonHistogram));
    }

    private void EmitProcessorBucket(byte processorNumber, ProcessorAccumulator accumulator)
    {
        ProcessorBucketFlushed?.Invoke(new CSwitchProcessorBucket(
            processorNumber,
            accumulator.BucketStartUtc,
            accumulator.BucketStartUtc.AddTicks(_bucketTicks),
            accumulator.ContextSwitchCount,
            accumulator.DistinctThreads.Count,
            accumulator.BusyDurationTicks,
            accumulator.IdleDurationTicks));
    }

    private static void UpdatePriority(CSwitchThreadBucketAccumulator bucket, int priority)
    {
        bucket.MinPriority = bucket.MinPriority is null || priority < bucket.MinPriority ? priority : bucket.MinPriority;
        bucket.MaxPriority = bucket.MaxPriority is null || priority > bucket.MaxPriority ? priority : bucket.MaxPriority;
    }

    private DateTime AlignToBucket(DateTime timestamp)
    {
        long ticks = timestamp.Ticks;
        long alignedTicks = ticks - (ticks % _bucketTicks);
        return new DateTime(alignedTicks, timestamp.Kind);
    }

    private sealed class ThreadState(uint processId)
    {
        public uint ProcessId { get; } = processId;

        public Dictionary<byte, DateTime> RunningSinceByProcessor { get; } = [];

        public CSwitchThreadBucketAccumulator? Current { get; set; }

        public long TotalDurationTicks { get; set; }

        public int ContextSwitchCount { get; set; }

        public DateTime? FirstRunStartedAtUtc { get; set; }

        public DateTime? LastRunEndedAtUtc { get; set; }
    }

    private sealed class CSwitchThreadBucketAccumulator(uint processId, DateTime bucketStartUtc)
    {
        public uint ProcessId { get; } = processId;

        public DateTime BucketStartUtc { get; } = bucketStartUtc;

        public int SwitchInCount { get; set; }

        public int SwitchOutCount { get; set; }

        public long RunDurationTicks { get; set; }

        public int? MinPriority { get; set; }

        public int? MaxPriority { get; set; }

        public int IdealProcessorMismatchCount { get; set; }

        public Dictionary<int, int> WaitReasonHistogram { get; } = [];
    }

    private sealed class ProcessorAccumulator(DateTime bucketStartUtc)
    {
        public DateTime BucketStartUtc { get; } = bucketStartUtc;

        public int ContextSwitchCount { get; set; }

        public HashSet<uint> DistinctThreads { get; } = [];

        public long BusyDurationTicks { get; set; }

        public long IdleDurationTicks { get; set; }
    }
}
