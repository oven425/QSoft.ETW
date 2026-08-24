using System.IO;
using Microsoft.Data.Sqlite;
using WpfApp1.Models;

namespace WpfApp1.Services;

public interface IDpcUiStutterAnalysisReader
{
    Task<DpcStutterAnalysisResult> LoadAsync(string databasePath, CancellationToken cancellationToken);
}

internal sealed class DpcUiStutterAnalysisReader : IDpcUiStutterAnalysisReader
{
    private const int AddressBucketShift = 24;
    private const byte ThreadedDpcOpcode = 66;
    private const byte DpcOpcode = 68;
    private const byte TimerDpcOpcode = 69;
    private static readonly int[] BucketSizeCandidatesMs = [100, 250, 500, 1_000, 2_000, 5_000, 10_000];

    public Task<DpcStutterAnalysisResult> LoadAsync(string databasePath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Load(databasePath, cancellationToken), cancellationToken);
    }

    private static DpcStutterAnalysisResult Load(string databasePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("SQLite 資料庫檔案路徑不可為空白。", nameof(databasePath));
        }

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("找不到 SQLite 資料庫檔案。", databasePath);
        }

        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        if (!TableExists(connection, "DpcEvents"))
        {
            return new DpcStutterAnalysisResult();
        }

        TraceSummary summary = ReadTraceSummary(connection);
        if (summary.TotalEventCount == 0)
        {
            return new DpcStutterAnalysisResult();
        }

        cancellationToken.ThrowIfCancellationRequested();

        TimeSpan bucketSize = DetermineBucketSize(summary);
        List<RoutineAggregate> routineAggregates = ReadRoutineAggregates(connection, cancellationToken);
        Dictionary<long, Dictionary<int, int>> processorsByRoutine = ReadRoutineProcessorCounts(connection, cancellationToken);
        Dictionary<long, ModuleResolution> moduleByRoutine = ResolveRoutineModules(
            ReadLoadedImages(connection, cancellationToken),
            routineAggregates);
        List<DpcRoutineHotspot> hotspots = BuildRoutineHotspots(routineAggregates, processorsByRoutine, moduleByRoutine);

        cancellationToken.ThrowIfCancellationRequested();

        List<(long BucketIndex, BucketAccumulator Bucket)> buckets = ReadBucketAccumulators(
            connection,
            summary.StartTicks,
            bucketSize.Ticks,
            cancellationToken);
        int[] bucketSeries = BuildBucketSeries(summary, bucketSize, buckets);
        double meanBucketLoad = bucketSeries.Length == 0 ? 0 : bucketSeries.Average();
        int spikeThreshold = ComputeSpikeThreshold(bucketSeries);
        List<DpcSpikeWindow> spikeWindows = BuildSpikeWindows(
            buckets,
            summary,
            bucketSize,
            meanBucketLoad,
            spikeThreshold,
            moduleByRoutine);

        return new DpcStutterAnalysisResult
        {
            Hotspots = hotspots,
            SpikeWindows = spikeWindows,
            TotalEventCount = summary.TotalEventCount,
            RoutineEventCount = summary.RoutineEventCount,
            EventsWithoutRoutine = summary.EventsWithoutRoutine,
            DistinctProcessorCount = summary.DistinctProcessorCount,
            BucketSize = bucketSize,
            SpikeThreshold = spikeThreshold,
            FirstSeenUtc = ParseTimestamp(summary.StartTicks),
            LastSeenUtc = ParseTimestamp(summary.EndTicks),
        };
    }

    private static TraceSummary ReadTraceSummary(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                MIN(TimestampUtc),
                MAX(TimestampUtc),
                COUNT(*),
                SUM(CASE WHEN Routine IS NOT NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN Routine IS NULL THEN 1 ELSE 0 END),
                COUNT(DISTINCT ProcessorNumber)
            FROM DpcEvents;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return new TraceSummary();
        }

        return new TraceSummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
    }

    private static List<RoutineAggregate> ReadRoutineAggregates(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Routine,
                COUNT(*) AS EventCount,
                SUM(CASE WHEN Opcode = 68 THEN 1 ELSE 0 END) AS DpcCount,
                SUM(CASE WHEN Opcode = 66 THEN 1 ELSE 0 END) AS ThreadedDpcCount,
                SUM(CASE WHEN Opcode = 69 THEN 1 ELSE 0 END) AS TimerDpcCount,
                SUM(CASE WHEN Opcode NOT IN (66, 68, 69) THEN 1 ELSE 0 END) AS UnknownOpcodeCount,
                MIN(TimestampUtc) AS FirstSeenUtc,
                MAX(TimestampUtc) AS LastSeenUtc
            FROM DpcEvents
            WHERE Routine IS NOT NULL
            GROUP BY Routine
            ORDER BY EventCount DESC, FirstSeenUtc;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<RoutineAggregate> aggregates = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            aggregates.Add(new RoutineAggregate(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return aggregates;
    }

    private static Dictionary<long, Dictionary<int, int>> ReadRoutineProcessorCounts(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Routine, ProcessorNumber, COUNT(*) AS EventCount
            FROM DpcEvents
            WHERE Routine IS NOT NULL
            GROUP BY Routine, ProcessorNumber;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<long, Dictionary<int, int>> processorsByRoutine = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long routineBits = reader.GetInt64(0);
            if (!processorsByRoutine.TryGetValue(routineBits, out Dictionary<int, int>? processorCounts))
            {
                processorCounts = [];
                processorsByRoutine[routineBits] = processorCounts;
            }

            processorCounts[reader.GetInt32(1)] = reader.GetInt32(2);
        }

        return processorsByRoutine;
    }

    private static List<(long BucketIndex, BucketAccumulator Bucket)> ReadBucketAccumulators(
        SqliteConnection connection,
        long traceStartTicks,
        long bucketSizeTicks,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                ((TimestampUtc - $traceStartTicks) / $bucketSizeTicks) AS BucketIndex,
                ProcessorNumber,
                Opcode,
                Routine,
                COUNT(*) AS EventCount
            FROM DpcEvents
            GROUP BY BucketIndex, ProcessorNumber, Opcode, Routine
            ORDER BY BucketIndex;
            """;
        command.Parameters.AddWithValue("$traceStartTicks", traceStartTicks);
        command.Parameters.AddWithValue("$bucketSizeTicks", bucketSizeTicks);

        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<long, BucketAccumulator> buckets = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bucketIndex = reader.GetInt64(0);
            if (!buckets.TryGetValue(bucketIndex, out BucketAccumulator? bucket))
            {
                bucket = new BucketAccumulator();
                buckets[bucketIndex] = bucket;
            }

            int eventCount = reader.GetInt32(4);
            int processorNumber = reader.GetInt32(1);
            byte opcode = Convert.ToByte(reader.GetInt32(2));

            bucket.EventCount += eventCount;
            bucket.EventsByProcessor[processorNumber] = bucket.EventsByProcessor.GetValueOrDefault(processorNumber) + eventCount;
            bucket.EventsByOpcode[opcode] = bucket.EventsByOpcode.GetValueOrDefault(opcode) + eventCount;

            if (!reader.IsDBNull(3))
            {
                long routineBits = reader.GetInt64(3);
                bucket.EventsByRoutine[routineBits] = bucket.EventsByRoutine.GetValueOrDefault(routineBits) + eventCount;
                if (!bucket.EventsByRoutineAndOpcode.TryGetValue(routineBits, out Dictionary<byte, int>? opcodeCounts))
                {
                    opcodeCounts = [];
                    bucket.EventsByRoutineAndOpcode[routineBits] = opcodeCounts;
                }

                opcodeCounts[opcode] = opcodeCounts.GetValueOrDefault(opcode) + eventCount;
            }
        }

        return [.. buckets.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value))];
    }

    private static List<LoadedImage> ReadLoadedImages(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!TableExists(connection, "ImageLoads"))
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT FileName, ProcessId, ImageBase, ImageSize, LoadedAtUtc, UnloadedAtUtc
            FROM ImageLoads
            WHERE ImageBase IS NOT NULL
              AND ImageSize IS NOT NULL
              AND ImageSize > 0;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<LoadedImage> images = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            images.Add(new LoadedImage(
                reader.GetString(0),
                reader.GetInt64(1),
                unchecked((ulong)reader.GetInt64(2)),
                unchecked((ulong)reader.GetInt64(3)),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return images;
    }

    private static Dictionary<long, ModuleResolution> ResolveRoutineModules(
        List<LoadedImage> images,
        IReadOnlyList<RoutineAggregate> routineAggregates)
    {
        Dictionary<long, ModuleResolution> result = [];
        if (images.Count == 0 || routineAggregates.Count == 0)
        {
            return result;
        }

        Dictionary<ulong, List<LoadedImage>> imagesByAddressBucket = [];
        foreach (LoadedImage image in images)
        {
            AddLoadedImageToAddressBuckets(imagesByAddressBucket, image);
        }

        foreach (RoutineAggregate aggregate in routineAggregates)
        {
            ulong routine = unchecked((ulong)aggregate.RoutineBits);
            LoadedImage? image = FindUniqueLoadedImage(
                imagesByAddressBucket,
                routine,
                aggregate.FirstSeenTicks,
                aggregate.LastSeenTicks);
            if (image is not null)
            {
                ulong offset = routine - image.ImageBase;
                result[aggregate.RoutineBits] = new ModuleResolution(
                    FormatModuleDisplay(image.FileName, offset),
                    image.FileName);
            }
        }

        return result;
    }

    private static List<DpcRoutineHotspot> BuildRoutineHotspots(
        IReadOnlyList<RoutineAggregate> routineAggregates,
        Dictionary<long, Dictionary<int, int>> processorsByRoutine,
        Dictionary<long, ModuleResolution> moduleByRoutine)
    {
        List<DpcRoutineHotspot> hotspots = [];
        foreach (RoutineAggregate aggregate in routineAggregates)
        {
            Dictionary<int, int> processorCounts = processorsByRoutine.TryGetValue(aggregate.RoutineBits, out Dictionary<int, int>? found)
                ? found
                : [];
            ModuleResolution? module = moduleByRoutine.TryGetValue(aggregate.RoutineBits, out ModuleResolution resolved)
                ? resolved
                : null;

            hotspots.Add(new DpcRoutineHotspot
            {
                RoutineDisplay = ToHex(unchecked((ulong)aggregate.RoutineBits)),
                ModuleDisplay = module?.Display ?? "(無法由既有 Image 對應)",
                ModulePath = module?.Path,
                EventCount = aggregate.EventCount,
                DpcTypeSummary = FormatDpcTypeSummary(aggregate.DpcCount, aggregate.ThreadedDpcCount, aggregate.TimerDpcCount, aggregate.UnknownOpcodeCount),
                NotableCpu = FormatNotableCpu(processorCounts, aggregate.EventCount),
                CpuDistribution = FormatCpuDistribution(processorCounts, aggregate.EventCount),
                FirstSeenUtc = ParseTimestamp(aggregate.FirstSeenTicks),
                LastSeenUtc = ParseTimestamp(aggregate.LastSeenTicks),
            });
        }

        return hotspots;
    }

    private static List<DpcSpikeWindow> BuildSpikeWindows(
        IReadOnlyList<(long BucketIndex, BucketAccumulator Bucket)> buckets,
        TraceSummary summary,
        TimeSpan bucketSize,
        double meanBucketLoad,
        int spikeThreshold,
        Dictionary<long, ModuleResolution> moduleByRoutine)
    {
        if (buckets.Count == 0 || spikeThreshold <= 0)
        {
            return [];
        }

        List<(long BucketIndex, BucketAccumulator Bucket)> spikeBuckets = [.. buckets.Where(bucket => bucket.Bucket.EventCount >= spikeThreshold)];
        if (spikeBuckets.Count == 0)
        {
            return [];
        }

        List<WindowAccumulator> windows = [];
        WindowAccumulator? current = null;
        foreach ((long bucketIndex, BucketAccumulator bucket) in spikeBuckets.OrderBy(static bucket => bucket.BucketIndex))
        {
            if (current is null || bucketIndex != current.LastBucketIndex + 1)
            {
                current = new WindowAccumulator(bucketIndex);
                windows.Add(current);
            }

            current.AddBucket(bucketIndex, bucket);
        }

        return [.. windows
            .OrderByDescending(static window => window.EventCount)
            .ThenBy(static window => window.FirstBucketIndex)
            .Take(20)
            .Select(window => CreateSpikeWindow(window, summary, bucketSize, meanBucketLoad, spikeThreshold, moduleByRoutine))];
    }

    private static DpcSpikeWindow CreateSpikeWindow(
        WindowAccumulator window,
        TraceSummary summary,
        TimeSpan bucketSize,
        double meanBucketLoad,
        int spikeThreshold,
        Dictionary<long, ModuleResolution> moduleByRoutine)
    {
        long startTicks = summary.StartTicks + (window.FirstBucketIndex * bucketSize.Ticks);
        long endTicksExclusive = summary.StartTicks + ((window.LastBucketIndex + 1) * bucketSize.Ticks);
        long endTicks = Math.Min(summary.EndTicks, endTicksExclusive - 1);

        List<DpcSpikeRoutineContribution> topRoutines = [.. window.EventsByRoutine
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(3)
            .Select(pair =>
            {
                ModuleResolution? module = moduleByRoutine.TryGetValue(pair.Key, out ModuleResolution resolved)
                    ? resolved
                    : null;
                return new DpcSpikeRoutineContribution
                {
                    RoutineDisplay = ToHex(unchecked((ulong)pair.Key)),
                    ModuleDisplay = module?.Display ?? "(無法由既有 Image 對應)",
                    ModulePath = module?.Path,
                    EventCount = pair.Value,
                    DpcTypeSummary = FormatDpcTypeSummary(
                        GetOpcodeCount(window.EventsByRoutineAndOpcode, pair.Key, DpcOpcode),
                        GetOpcodeCount(window.EventsByRoutineAndOpcode, pair.Key, ThreadedDpcOpcode),
                        GetOpcodeCount(window.EventsByRoutineAndOpcode, pair.Key, TimerDpcOpcode),
                        GetUnknownOpcodeCount(window.EventsByRoutineAndOpcode, pair.Key)),
                };
            })];

        string thresholdDisplay = window.BucketCount == 1
            ? $"單桶 {window.MaxBucketEventCount:N0} 次；門檻 {spikeThreshold:N0}，平均 {meanBucketLoad:N1}/桶"
            : $"{window.BucketCount:N0} 連續桶；單桶最高 {window.MaxBucketEventCount:N0} 次，門檻 {spikeThreshold:N0}，平均 {meanBucketLoad:N1}/桶";

        return new DpcSpikeWindow
        {
            WindowStartUtc = ParseTimestamp(startTicks),
            WindowEndUtc = ParseTimestamp(endTicks),
            EventCount = window.EventCount,
            DpcTypeSummary = FormatDpcTypeSummary(
                window.EventsByOpcode.GetValueOrDefault(DpcOpcode),
                window.EventsByOpcode.GetValueOrDefault(ThreadedDpcOpcode),
                window.EventsByOpcode.GetValueOrDefault(TimerDpcOpcode),
                window.EventsByOpcode.Where(static pair => pair.Key is not (DpcOpcode or ThreadedDpcOpcode or TimerDpcOpcode)).Sum(static pair => pair.Value)),
            NotableCpu = FormatNotableCpu(window.EventsByProcessor, window.EventCount),
            ThresholdDisplay = thresholdDisplay,
            TopRoutines = topRoutines,
        };
    }

    private static int ComputeSpikeThreshold(int[] bucketCounts)
    {
        if (bucketCounts.Length == 0)
        {
            return 0;
        }

        double mean = bucketCounts.Average();
        double variance = bucketCounts.Select(count => Math.Pow(count - mean, 2)).Average();
        double standardDeviation = Math.Sqrt(variance);
        int[] ordered = [.. bucketCounts.OrderBy(static count => count)];
        int percentileIndex = (int)Math.Floor((ordered.Length - 1) * 0.9);
        int percentile90 = ordered[percentileIndex];
        return Math.Max(2, (int)Math.Ceiling(Math.Max(mean + standardDeviation, percentile90)));
    }

    private static int[] BuildBucketSeries(
        TraceSummary summary,
        TimeSpan bucketSize,
        IReadOnlyList<(long BucketIndex, BucketAccumulator Bucket)> buckets)
    {
        int bucketCount = Math.Max(1, (int)(((summary.EndTicks - summary.StartTicks) / bucketSize.Ticks) + 1));
        int[] counts = new int[bucketCount];
        foreach ((long bucketIndex, BucketAccumulator bucket) in buckets)
        {
            if (bucketIndex >= 0 && bucketIndex < counts.Length)
            {
                counts[bucketIndex] = bucket.EventCount;
            }
        }

        return counts;
    }

    private static TimeSpan DetermineBucketSize(TraceSummary summary)
    {
        long spanTicks = Math.Max(TimeSpan.FromMilliseconds(100).Ticks, summary.EndTicks - summary.StartTicks + 1);
        int targetBucketCount = Math.Clamp((int)Math.Ceiling(Math.Sqrt(summary.TotalEventCount) * 2), 12, 180);
        long rawBucketTicks = Math.Max(TimeSpan.FromMilliseconds(100).Ticks, spanTicks / targetBucketCount);

        foreach (int candidateMs in BucketSizeCandidatesMs)
        {
            TimeSpan candidate = TimeSpan.FromMilliseconds(candidateMs);
            if (candidate.Ticks >= rawBucketTicks)
            {
                return candidate;
            }
        }

        return TimeSpan.FromMilliseconds(BucketSizeCandidatesMs[^1]);
    }

    private static string FormatDpcTypeSummary(int dpcCount, int threadedDpcCount, int timerDpcCount, int unknownOpcodeCount)
    {
        List<string> parts = [];
        if (dpcCount > 0)
        {
            parts.Add($"DPC {dpcCount:N0}");
        }

        if (threadedDpcCount > 0)
        {
            parts.Add($"ThreadedDPC {threadedDpcCount:N0}");
        }

        if (timerDpcCount > 0)
        {
            parts.Add($"TimerDPC {timerDpcCount:N0}");
        }

        if (unknownOpcodeCount > 0)
        {
            parts.Add($"其他 Opcode {unknownOpcodeCount:N0}");
        }

        return parts.Count == 0 ? "-" : string.Join(" / ", parts);
    }

    private static string FormatNotableCpu(IReadOnlyDictionary<int, int> countsByProcessor, int totalEventCount)
    {
        if (totalEventCount <= 0 || countsByProcessor.Count == 0)
        {
            return "-";
        }

        KeyValuePair<int, int> topProcessor = countsByProcessor
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .First();
        return $"CPU {topProcessor.Key} ({(double)topProcessor.Value / totalEventCount:P0})";
    }

    private static string FormatCpuDistribution(IReadOnlyDictionary<int, int> countsByProcessor, int totalEventCount)
    {
        if (totalEventCount <= 0 || countsByProcessor.Count == 0)
        {
            return "-";
        }

        List<KeyValuePair<int, int>> ordered = [.. countsByProcessor
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)];
        List<string> parts = [.. ordered.Take(4).Select(pair => $"CPU {pair.Key}: {pair.Value:N0} ({(double)pair.Value / totalEventCount:P0})")];
        if (ordered.Count > 4)
        {
            int remaining = ordered.Skip(4).Sum(static pair => pair.Value);
            parts.Add($"其他: {remaining:N0} ({(double)remaining / totalEventCount:P0})");
        }

        return string.Join(", ", parts);
    }

    private static string FormatModuleDisplay(string fileName, ulong offset)
    {
        string shortName = Path.GetFileName(fileName);
        return $"{(string.IsNullOrWhiteSpace(shortName) ? fileName : shortName)}+0x{offset:X}";
    }

    private static LoadedImage? FindUniqueLoadedImage(
        Dictionary<ulong, List<LoadedImage>> imagesByAddressBucket,
        ulong routine,
        long firstSeenTicks,
        long lastSeenTicks)
    {
        ulong addressBucket = routine >> AddressBucketShift;
        if (!imagesByAddressBucket.TryGetValue(addressBucket, out List<LoadedImage>? candidates))
        {
            return null;
        }

        LoadedImage? match = null;
        HashSet<(string FileName, ulong ImageBase, ulong ImageSize)> distinctMatches = [];
        foreach (LoadedImage candidate in candidates)
        {
            if (candidate.LoadedAtUtc > lastSeenTicks ||
                (candidate.UnloadedAtUtc is long unloadedAt && unloadedAt < firstSeenTicks) ||
                routine < candidate.ImageBase ||
                routine - candidate.ImageBase >= candidate.ImageSize)
            {
                continue;
            }

            (string FileName, ulong ImageBase, ulong ImageSize) key = (candidate.FileName, candidate.ImageBase, candidate.ImageSize);
            if (!distinctMatches.Add(key))
            {
                if (match is null || PreferCandidate(candidate, match))
                {
                    match = candidate;
                }

                continue;
            }

            if (match is not null && !SameImage(match, candidate))
            {
                return null;
            }

            match = candidate;
        }

        return match;
    }

    private static bool PreferCandidate(LoadedImage candidate, LoadedImage current)
    {
        int candidateRank = candidate.ProcessId is 0 or 4 ? 0 : 1;
        int currentRank = current.ProcessId is 0 or 4 ? 0 : 1;
        if (candidateRank != currentRank)
        {
            return candidateRank < currentRank;
        }

        return candidate.LoadedAtUtc > current.LoadedAtUtc;
    }

    private static bool SameImage(LoadedImage left, LoadedImage right)
    {
        return left.ImageBase == right.ImageBase &&
               left.ImageSize == right.ImageSize &&
               string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddLoadedImageToAddressBuckets(
        Dictionary<ulong, List<LoadedImage>> imagesByAddressBucket,
        LoadedImage image)
    {
        ulong firstBucket = image.ImageBase >> AddressBucketShift;
        ulong lastBucket = checked(image.ImageBase + image.ImageSize - 1) >> AddressBucketShift;
        for (ulong bucket = firstBucket; bucket <= lastBucket; bucket++)
        {
            if (!imagesByAddressBucket.TryGetValue(bucket, out List<LoadedImage>? images))
            {
                images = [];
                imagesByAddressBucket[bucket] = images;
            }

            images.Add(image);
        }
    }

    private static int GetOpcodeCount(
        IReadOnlyDictionary<long, Dictionary<byte, int>> eventsByRoutineAndOpcode,
        long routineBits,
        byte opcode)
    {
        return eventsByRoutineAndOpcode.TryGetValue(routineBits, out Dictionary<byte, int>? counts)
            ? counts.GetValueOrDefault(opcode)
            : 0;
    }

    private static int GetUnknownOpcodeCount(
        IReadOnlyDictionary<long, Dictionary<byte, int>> eventsByRoutineAndOpcode,
        long routineBits)
    {
        return eventsByRoutineAndOpcode.TryGetValue(routineBits, out Dictionary<byte, int>? counts)
            ? counts.Where(static pair => pair.Key is not (DpcOpcode or ThreadedDpcOpcode or TimerDpcOpcode)).Sum(static pair => pair.Value)
            : 0;
    }

    private static string ToHex(ulong value) => $"0x{value:X16}";

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static DateTime ParseTimestamp(long ticks) => new(ticks, DateTimeKind.Utc);

    private sealed record TraceSummary(
        long StartTicks = 0,
        long EndTicks = 0,
        int TotalEventCount = 0,
        int RoutineEventCount = 0,
        int EventsWithoutRoutine = 0,
        int DistinctProcessorCount = 0);

    private sealed record RoutineAggregate(
        long RoutineBits,
        int EventCount,
        int DpcCount,
        int ThreadedDpcCount,
        int TimerDpcCount,
        int UnknownOpcodeCount,
        long FirstSeenTicks,
        long LastSeenTicks);

    private sealed record LoadedImage(
        string FileName,
        long ProcessId,
        ulong ImageBase,
        ulong ImageSize,
        long LoadedAtUtc,
        long? UnloadedAtUtc);

    private sealed record ModuleResolution(string Display, string Path);

    private sealed class BucketAccumulator
    {
        public int EventCount { get; set; }

        public Dictionary<int, int> EventsByProcessor { get; } = [];

        public Dictionary<byte, int> EventsByOpcode { get; } = [];

        public Dictionary<long, int> EventsByRoutine { get; } = [];

        public Dictionary<long, Dictionary<byte, int>> EventsByRoutineAndOpcode { get; } = [];
    }

    private sealed class WindowAccumulator(long firstBucketIndex)
    {
        public long FirstBucketIndex { get; } = firstBucketIndex;

        public long LastBucketIndex { get; private set; } = firstBucketIndex;

        public int BucketCount { get; private set; }

        public int EventCount { get; private set; }

        public int MaxBucketEventCount { get; private set; }

        public Dictionary<int, int> EventsByProcessor { get; } = [];

        public Dictionary<byte, int> EventsByOpcode { get; } = [];

        public Dictionary<long, int> EventsByRoutine { get; } = [];

        public Dictionary<long, Dictionary<byte, int>> EventsByRoutineAndOpcode { get; } = [];

        public void AddBucket(long bucketIndex, BucketAccumulator bucket)
        {
            LastBucketIndex = bucketIndex;
            BucketCount++;
            EventCount += bucket.EventCount;
            MaxBucketEventCount = Math.Max(MaxBucketEventCount, bucket.EventCount);

            MergeCounts(EventsByProcessor, bucket.EventsByProcessor);
            MergeCounts(EventsByOpcode, bucket.EventsByOpcode);
            MergeCounts(EventsByRoutine, bucket.EventsByRoutine);
            foreach ((long routineBits, Dictionary<byte, int> opcodeCounts) in bucket.EventsByRoutineAndOpcode)
            {
                if (!EventsByRoutineAndOpcode.TryGetValue(routineBits, out Dictionary<byte, int>? currentCounts))
                {
                    currentCounts = [];
                    EventsByRoutineAndOpcode[routineBits] = currentCounts;
                }

                MergeCounts(currentCounts, opcodeCounts);
            }
        }
    }

    private static void MergeCounts<TKey>(Dictionary<TKey, int> target, IReadOnlyDictionary<TKey, int> source) where TKey : notnull
    {
        foreach ((TKey key, int value) in source)
        {
            target[key] = target.GetValueOrDefault(key) + value;
        }
    }
}
