using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using QSoft.ETW;

namespace WpfApp1.Services;

/// <summary>匯出結果的具體實作，僅由 <see cref="EtlSqliteWriter"/> 建立。</summary>
internal sealed record EtlSqliteExportResult(string DatabasePath, long RowsWritten) : IEtlSqliteExportResult;

/// <summary>
/// ETL → SQLite 的實際儲存實作(storage implementation，依規定留在 WpfApp1)。
/// 採用 Microsoft.Data.Sqlite 官方建議的批次匯入模式：
/// 1) 匯入到與目的檔同目錄的暫存資料庫(*.importing)，避免中途失敗時破壞既有 .db。
/// 2) 全程只開一個交易(transaction)，且每張表重複使用同一個已參數化的 SqliteCommand(重設參數值即可)。
/// 3) SQLite 非同步 API 內部仍是同步執行，因此全程使用同步呼叫，不使用 ExecuteNonQueryAsync 等。
/// 4) Schema 分兩階段：先建表(不建索引)以加速大量 INSERT，成功 commit 後才建立索引與 AI 檢視表、執行 PRAGMA optimize。
/// 5) 全部成功才會把暫存資料庫改名成目的檔(File.Move overwrite，同磁碟區內近似原子操作)；
///    任何一步丟例外，暫存檔會在 finally 內被刪除，不會產生半成品 .db。
/// </summary>
internal static class EtlSqliteWriter
{
    public static IEtlSqliteExportResult Export(
        string outputDatabasePath,
        EtlSqliteExportOptions options,
        EtlReadResult traceResult,
        IReadOnlyList<ProcessRecord> processes,
        IReadOnlyList<ThreadRecord> threads,
        IReadOnlyList<ServiceRecord> services,
        IReadOnlyList<ServiceSnapshotRecord> serviceSnapshots,
        IReadOnlyList<EnergyMeasurementInterval> meterIntervals,
        IReadOnlyList<MemoryUtilizationEventInfo> memorySnapshots,
        IReadOnlyList<InterruptServiceRoutineInterval> isrIntervals,
        IReadOnlyList<DeferredProcedureCallInterval> dpcIntervals,
        IReadOnlyList<CSwitchThreadBucket> threadBuckets,
        IReadOnlyList<CSwitchProcessorBucket> processorBuckets,
        IReadOnlyList<ProcessCounterEventInfo> processCounters,
        IReadOnlyList<BufferedProfileSample> profileSamples,
        IReadOnlyList<WmiActivityEventInfo_11> wmi11,
        IReadOnlyList<WmiActivityEventInfo_12> wmi12,
        IReadOnlyList<WmiActivityEventInfo_13> wmi13,
        IReadOnlyList<WmiActivityEventInfo_16> wmi16,
        IReadOnlyList<WmiActivityEventInfo_17> wmi17,
        IReadOnlyList<WmiActivityEventInfo_20> wmi20,
        IReadOnlyList<WmiActivityEventInfo_22> wmi22,
        IReadOnlyList<WmiActivityEventInfo_24> wmi24,
        IReadOnlyList<WmiActivityEventInfo_100> wmi100,
        IReadOnlyList<WmiActivityEventInfo_101> wmi101,
        IReadOnlyList<WmiActivityEventInfo_5857> wmi5857,
        IReadOnlyList<WmiActivityEventInfo_5858> wmi5858,
        IReadOnlyList<KernelAcpiEventInfo_TemperatureNotification> acpiTemperatureNotification,
        IReadOnlyList<KernelAcpiEventInfo_AmlMethodTrace> acpiAmlMethodTrace,
        IReadOnlyList<KernelAcpiEventInfo_TemperatureChange> acpiTemperatureChange,
        IReadOnlyList<KernelAcpiEventInfo_FrequentAmlMethod> acpiFrequentAmlMethod,
        IReadOnlyList<KernelPowerEventInfo_63> kernelPower63,
        IReadOnlyList<EnergyEstimationEngineEventInfo_37> energyEstimates,
        IReadOnlyList<EnergyEstimationEngineEventInfo_33> energyQueryStats,
        IReadOnlyList<EnergyEstimationEngineEventInfo_14> energyCpuSamples,
        IReadOnlyList<EnergyEstimationEngineEventInfo_18> energyComponentSamples,
        IReadOnlyList<EnergyEstimationEngineEventInfo_35> standbySamples,
        IReadOnlyList<PowerMeterPollingEventInfo_2> powerMeterRundowns,
        IReadOnlyList<PowerMeterPollingEventInfo_3> powerMeterEvent3,
        IReadOnlyList<PowerMeterPollingEventInfo_4> powerMeterEvent4,
        IReadOnlyList<BufferedDiskIoOperation> diskIoOperations)
    {
        if (!options.OverwriteExisting && File.Exists(outputDatabasePath))
        {
            throw new IOException($"目的 SQLite 資料庫已存在：{outputDatabasePath}");
        }

        string tempPath = GetTempPath(outputDatabasePath);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputDatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        long rowsWritten = 0;
        try
        {
            using (SqliteConnection connection = OpenTempDatabase(tempPath))
            {
                CreateTables(connection);

                using SqliteTransaction transaction = connection.BeginTransaction();

                // process_key/thread_key：以 ProcessRecord/ThreadRecord 物件身分(已由既有內建 collector
                // 依生命週期正確處理過 PID/TID 重用)指派穩定的整數代理鍵，而非直接使用會重複的 process_id/thread_id。
                Dictionary<ProcessRecord, long> processKeys = AssignKeys(processes);
                Dictionary<ThreadRecord, long> threadKeys = AssignKeys(threads);
                ProcessResolver processResolver = new(processes, processKeys);
                ThreadResolver threadResolver = new(threads, threadKeys);

                rowsWritten += WriteTraceInfo(connection, transaction, traceResult, traceResult.SourceFilePath);
                rowsWritten += WriteProcesses(connection, transaction, processes, processKeys);
                rowsWritten += WriteThreads(connection, transaction, threads, threadKeys, processResolver);
                rowsWritten += WriteImages(connection, transaction, processes, processKeys);
                rowsWritten += WriteProcessMemorySamples(connection, transaction, processCounters, processResolver);
                rowsWritten += WriteCpuBuckets(connection, transaction, threadBuckets, processorBuckets, threadResolver, processResolver);
                rowsWritten += WriteInterruptEvents(connection, transaction, isrIntervals, dpcIntervals);
                rowsWritten += WriteProfileSamples(connection, transaction, profileSamples, processResolver, threadResolver);
                rowsWritten += WriteServices(connection, transaction, services, serviceSnapshots, processResolver, threadResolver);
                rowsWritten += WriteMemorySnapshots(connection, transaction, memorySnapshots);
                rowsWritten += WriteWmiEvents(
                    connection, transaction, processResolver, threadResolver,
                    wmi11, wmi12, wmi13, wmi16, wmi17, wmi20, wmi22, wmi24, wmi100, wmi101, wmi5857, wmi5858);
                rowsWritten += WriteAcpiEvents(
                    connection, transaction, processResolver, threadResolver,
                    acpiTemperatureNotification, acpiAmlMethodTrace, acpiTemperatureChange, acpiFrequentAmlMethod);
                rowsWritten += WriteKernelPowerEvents(connection, transaction, kernelPower63, processResolver, threadResolver);
                rowsWritten += WriteEnergyEstimates(connection, transaction, energyEstimates, processResolver, threadResolver);
                rowsWritten += WriteEnergyQueryStats(connection, transaction, energyQueryStats);
                rowsWritten += WriteEnergyCpuSamples(connection, transaction, energyCpuSamples);
                rowsWritten += WriteEnergyComponentSamples(connection, transaction, energyComponentSamples);
                rowsWritten += WriteStandbySamples(connection, transaction, standbySamples);
                rowsWritten += WritePowerMeters(connection, transaction, powerMeterRundowns, powerMeterEvent3, powerMeterEvent4, meterIntervals);
                rowsWritten += WriteDiskIoOperations(connection, transaction, diskIoOperations, processResolver, threadResolver);
                transaction.Commit();

                CreateIndexesAndViews(connection);

                using (SqliteCommand optimize = connection.CreateCommand())
                {
                    optimize.CommandText = "PRAGMA optimize;";
                    optimize.ExecuteNonQuery();
                }
            }

            SqliteConnection.ClearAllPools();
            PublishDatabase(tempPath, outputDatabasePath);
        }
        catch
        {
            TryDeleteDatabaseFiles(tempPath);
            throw;
        }
        finally
        {
            TryDeleteDatabaseFiles(tempPath);
        }

        return new EtlSqliteExportResult(outputDatabasePath, rowsWritten);
    }

    private static string GetTempPath(string outputDatabasePath) =>
        $"{outputDatabasePath}.{Guid.NewGuid():N}.importing";

    private static void PublishDatabase(string tempPath, string outputDatabasePath)
    {
        if (!File.Exists(outputDatabasePath))
        {
            File.Move(tempPath, outputDatabasePath);
            return;
        }

        string backupPath = $"{outputDatabasePath}.{Guid.NewGuid():N}.backup";
        try
        {
            File.Replace(tempPath, outputDatabasePath, backupPath, ignoreMetadataErrors: true);
            TryDeleteDatabaseFiles(backupPath);
        }
        catch
        {
            if (!File.Exists(outputDatabasePath) && File.Exists(backupPath))
            {
                File.Move(backupPath, outputDatabasePath);
            }

            throw;
        }
        finally
        {
            if (File.Exists(outputDatabasePath))
            {
                TryDeleteDatabaseFiles(backupPath);
            }
        }

        TryDeleteDatabaseSidecars(outputDatabasePath);
    }

    private static void TryDeleteDatabaseFiles(string databasePath)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            string path = databasePath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 檔案可能仍被其他連線鎖定(例如上一輪失敗尚未釋放)，略過即可，下一次匯出會再嘗試刪除。
            }
        }
    }

    private static void TryDeleteDatabaseSidecars(string databasePath)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string path = databasePath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 主資料庫已完整替換；仍被外部讀取者鎖定的舊 sidecar 交由 SQLite 在下次開啟時處理。
            }
        }
    }

    private static SqliteConnection OpenTempDatabase(string tempPath)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = tempPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        // 暫存匯入資料庫是用完即丟的一次性檔案：關閉持久性保證以換取匯入速度是合理取捨；
        // 只有全部成功寫完、改名成目的檔之前，過程中的任何失敗都會整檔刪除重來，不需要 WAL/journal 復原能力。
        using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText = """
            PRAGMA journal_mode = OFF;
            PRAGMA synchronous = OFF;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -131072;
            PRAGMA locking_mode = EXCLUSIVE;
            PRAGMA page_size = 8192;
            """;
        pragmas.ExecuteNonQuery();

        return connection;
    }

    private static Dictionary<T, long> AssignKeys<T>(IReadOnlyList<T> items) where T : class
    {
        Dictionary<T, long> keys = new(ReferenceEqualityComparer.Instance);
        long next = 1;
        foreach (T item in items)
        {
            keys[item] = next++;
        }

        return keys;
    }

    private static long ToUnixMicros(DateTime timestamp)
    {
        DateTime utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        };

        return (utc - DateTime.UnixEpoch).Ticks / 10;
    }

    private static long? ToUnixMicros(DateTime? timestamp) => timestamp is DateTime value ? ToUnixMicros(value) : null;

    /// <summary>把 ulong 位元組原封不動轉成 long(位元組相同、僅型別改變)，供 SQLite INTEGER 欄位無損保存 64-bit 計數器/位址。</summary>
    private static long ToSigned(ulong value) => unchecked((long)value);

    private static long? ToSigned(ulong? value) => value is ulong v ? ToSigned(v) : null;

    private static long? FileTimeToUnixMicros(ulong? fileTime)
    {
        const long UnixEpochFileTime = 116_444_736_000_000_000;
        return fileTime is ulong value ? (ToSigned(value) - UnixEpochFileTime) / 10 : null;
    }

    private static string GetEnergyComponentName(uint component) => component switch
    {
        0 => "states",
        1 => "other",
        2 => "cpu",
        3 => "soc",
        4 => "display",
        5 => "disk",
        6 => "network",
        _ => "unknown",
    };

    /// <summary>依 process_id + 時間戳解析出穩定的 process_key，處理同一個 PID 先後被不同程序重用的情形。</summary>
    private sealed class ProcessResolver
    {
        private readonly Dictionary<uint, List<ProcessRecord>> _byPid;
        private readonly Dictionary<ProcessRecord, long> _keys;

        public ProcessResolver(IReadOnlyList<ProcessRecord> processes, Dictionary<ProcessRecord, long> keys)
        {
            _keys = keys;
            _byPid = [];
            foreach (ProcessRecord process in processes)
            {
                if (!_byPid.TryGetValue(process.ProcessId, out List<ProcessRecord>? list))
                {
                    list = [];
                    _byPid[process.ProcessId] = list;
                }

                list.Add(process);
            }
        }

        public long? ResolveKey(uint processId, DateTime timestamp) => Resolve(processId, timestamp) is ProcessRecord p ? _keys[p] : null;

        public ProcessRecord? Resolve(uint processId, DateTime timestamp)
        {
            if (!_byPid.TryGetValue(processId, out List<ProcessRecord>? candidates))
            {
                return null;
            }

            ProcessRecord? best = null;
            foreach (ProcessRecord candidate in candidates)
            {
                DateTime start = candidate.StartTime ?? DateTime.MinValue;
                DateTime end = candidate.EndTime ?? DateTime.MaxValue;
                if (timestamp < start || timestamp > end)
                {
                    continue;
                }

                if (best is null || (candidate.StartTime ?? DateTime.MinValue) > (best.StartTime ?? DateTime.MinValue))
                {
                    best = candidate;
                }
            }

            return best;
        }
    }

    /// <summary>依 thread_id + 時間戳解析出穩定的 thread_key，處理同一個 TID 先後被不同執行緒重用的情形。</summary>
    private sealed class ThreadResolver
    {
        private readonly Dictionary<uint, List<ThreadRecord>> _byTid;
        private readonly Dictionary<ThreadRecord, long> _keys;

        public ThreadResolver(IReadOnlyList<ThreadRecord> threads, Dictionary<ThreadRecord, long> keys)
        {
            _keys = keys;
            _byTid = [];
            foreach (ThreadRecord thread in threads)
            {
                if (!_byTid.TryGetValue(thread.ThreadId, out List<ThreadRecord>? list))
                {
                    list = [];
                    _byTid[thread.ThreadId] = list;
                }

                list.Add(thread);
            }
        }

        public long? ResolveKey(uint threadId, DateTime timestamp) => Resolve(threadId, timestamp) is ThreadRecord t ? _keys[t] : null;

        public ThreadRecord? Resolve(uint threadId, DateTime timestamp)
        {
            if (!_byTid.TryGetValue(threadId, out List<ThreadRecord>? candidates))
            {
                return null;
            }

            ThreadRecord? best = null;
            foreach (ThreadRecord candidate in candidates)
            {
                DateTime start = candidate.CreateTime ?? DateTime.MinValue;
                DateTime end = candidate.ExitTime ?? DateTime.MaxValue;
                if (timestamp < start || timestamp > end)
                {
                    continue;
                }

                if (best is null || (candidate.CreateTime ?? DateTime.MinValue) > (best.CreateTime ?? DateTime.MinValue))
                {
                    best = candidate;
                }
            }

            return best;
        }
    }

    /// <summary>單一資料表的可重用參數化 INSERT 指令，官方建議的「同一交易 + 重複使用同一個已參數化命令」寫法。</summary>
    private sealed class TableWriter
    {
        private readonly SqliteCommand _command;
        private readonly Dictionary<string, SqliteParameter> _parameters;
        private readonly List<SqliteParameter> _assignedParameters;
        private readonly bool _returnsRowId;

        public TableWriter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            params string[] columns)
            : this(connection, transaction, table, returnsRowId: false, columns)
        {
        }

        public TableWriter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            bool returnsRowId = false,
            params string[] columns)
        {
            _returnsRowId = returnsRowId;
            _command = connection.CreateCommand();
            _command.Transaction = transaction;
            _command.CommandText =
                $"INSERT INTO {table} ({string.Join(',', columns)}) VALUES ({string.Join(',', columns.Select(c => "@" + c))})" +
                (returnsRowId ? " RETURNING rowid;" : ";");
            _parameters = new Dictionary<string, SqliteParameter>(columns.Length);
            _assignedParameters = new List<SqliteParameter>(columns.Length);
            foreach (string column in columns)
            {
                SqliteParameter parameter = _command.CreateParameter();
                parameter.ParameterName = "@" + column;
                // 每個資料表被多種事件種類共用寫入(例如 wmi_events 涵蓋 11/12/13/... 多種 event_id，
                // 各自只會 Set() 自己用到的欄位)，因此參數預設值必須是 DBNull，
                // 否則沒被目前這種事件種類用到的欄位在繫結時會丟「Value must be set」例外。
                parameter.Value = DBNull.Value;
                _command.Parameters.Add(parameter);
                _parameters[column] = parameter;
            }
            _command.Prepare();
        }

        public TableWriter Set(string column, object? value)
        {
            SqliteParameter parameter = _parameters[column];
            parameter.Value = value ?? DBNull.Value;
            _assignedParameters.Add(parameter);
            return this;
        }

        public void Insert()
        {
            try
            {
                if (_returnsRowId)
                {
                    throw new InvalidOperationException("此 INSERT 會回傳 rowid，請改用 InsertAndGetRowId()。");
                }

                _command.ExecuteNonQuery();
            }
            finally
            {
                ResetAssignedParameters();
            }
        }

        public long InsertAndGetRowId()
        {
            try
            {
                if (!_returnsRowId)
                {
                    throw new InvalidOperationException("此 INSERT 未設定回傳 rowid。");
                }

                return Convert.ToInt64(_command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            finally
            {
                ResetAssignedParameters();
            }
        }

        private void ResetAssignedParameters()
        {
            foreach (SqliteParameter parameter in _assignedParameters)
            {
                parameter.Value = DBNull.Value;
            }
            _assignedParameters.Clear();
        }
    }

    /// <summary>建表階段刻意不建索引，讓大量 INSERT 期間不必維護索引結構；索引與檢視表留到 commit 後才建立(schema/index 分離)。</summary>
    private static void CreateTables(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE trace_info (
                id INTEGER PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                source_etl_path TEXT NOT NULL,
                imported_at_utc_us INTEGER NOT NULL,
                trace_start_utc_us INTEGER,
                trace_end_utc_us INTEGER,
                processor_count INTEGER,
                usable_memory_bytes INTEGER,
                memory_page_size_bytes INTEGER,
                buffers_lost INTEGER,
                events_lost INTEGER,
                perf_frequency_hz INTEGER
            );

            CREATE TABLE processes (
                process_key INTEGER PRIMARY KEY,
                process_id INTEGER NOT NULL,
                parent_process_id INTEGER,
                parent_process_key INTEGER,
                session_id INTEGER,
                object_address INTEGER,
                directory_table_base INTEGER,
                flags INTEGER,
                exit_code INTEGER,
                user_sid TEXT,
                package_full_name TEXT,
                application_id TEXT,
                image_file_name TEXT,
                command_line TEXT,
                started_at_utc_us INTEGER,
                ended_at_utc_us INTEGER
            );

            CREATE TABLE threads (
                thread_key INTEGER PRIMARY KEY,
                thread_id INTEGER NOT NULL,
                process_key INTEGER,
                process_id INTEGER NOT NULL,
                created_at_utc_us INTEGER,
                exited_at_utc_us INTEGER,
                stack_base INTEGER,
                stack_limit INTEGER,
                user_stack_base INTEGER,
                user_stack_limit INTEGER,
                processor_affinity INTEGER,
                start_address INTEGER,
                start_address_module_name TEXT,
                start_address_module_offset INTEGER,
                teb_base INTEGER,
                service_id INTEGER,
                base_priority INTEGER,
                page_priority INTEGER,
                page_priority_name TEXT,
                io_priority INTEGER,
                io_priority_name TEXT,
                thread_name TEXT
            );

            CREATE TABLE images (
                id INTEGER PRIMARY KEY,
                process_key INTEGER,
                process_id INTEGER,
                file_name TEXT,
                image_base INTEGER,
                image_size INTEGER,
                default_base INTEGER,
                image_checksum INTEGER,
                time_date_stamp INTEGER,
                loaded_at_utc_us INTEGER,
                unloaded_at_utc_us INTEGER,
                is_rundown INTEGER NOT NULL
            );

            CREATE TABLE process_memory_samples (
                id INTEGER PRIMARY KEY,
                process_key INTEGER,
                process_id INTEGER NOT NULL,
                sampled_at_utc_us INTEGER NOT NULL,
                working_set_bytes INTEGER,
                peak_working_set_bytes INTEGER,
                virtual_bytes INTEGER,
                peak_virtual_bytes INTEGER,
                private_bytes INTEGER,
                page_fault_count INTEGER,
                handle_count INTEGER
            );

            CREATE TABLE thread_cpu_buckets (
                id INTEGER PRIMARY KEY,
                thread_key INTEGER,
                process_key INTEGER,
                thread_id INTEGER NOT NULL,
                process_id INTEGER NOT NULL,
                bucket_start_utc_us INTEGER NOT NULL,
                bucket_end_utc_us INTEGER NOT NULL,
                switch_in_count INTEGER,
                switch_out_count INTEGER,
                run_duration_us INTEGER,
                min_priority INTEGER,
                max_priority INTEGER,
                ideal_processor_mismatch_count INTEGER
            );

            CREATE TABLE thread_wait_reason_buckets (
                id INTEGER PRIMARY KEY,
                thread_cpu_bucket_id INTEGER NOT NULL,
                wait_reason INTEGER NOT NULL,
                wait_reason_name TEXT,
                event_count INTEGER NOT NULL
            );

            CREATE TABLE processor_cpu_buckets (
                id INTEGER PRIMARY KEY,
                processor_number INTEGER NOT NULL,
                bucket_start_utc_us INTEGER NOT NULL,
                bucket_end_utc_us INTEGER NOT NULL,
                context_switch_count INTEGER,
                distinct_thread_count INTEGER,
                busy_duration_us INTEGER,
                idle_duration_us INTEGER
            );

            CREATE TABLE dpc_events (
                id INTEGER PRIMARY KEY,
                start_utc_us INTEGER NOT NULL,
                timestamp_utc_us INTEGER NOT NULL,
                duration_us INTEGER NOT NULL,
                processor_number INTEGER,
                call_type INTEGER,
                call_type_name TEXT,
                handler_routine_address INTEGER,
                handler_module_name TEXT,
                handler_module_offset INTEGER
            );

            CREATE TABLE isr_events (
                id INTEGER PRIMARY KEY,
                start_utc_us INTEGER NOT NULL,
                timestamp_utc_us INTEGER NOT NULL,
                duration_us INTEGER NOT NULL,
                processor_number INTEGER,
                handler_routine_address INTEGER,
                handler_module_name TEXT,
                handler_module_offset INTEGER,
                vector INTEGER,
                return_value INTEGER
            );

            CREATE TABLE profile_samples (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                processor_number INTEGER,
                process_key INTEGER,
                thread_key INTEGER,
                process_id INTEGER,
                thread_id INTEGER,
                instruction_pointer INTEGER
            );

            CREATE TABLE services (
                service_key INTEGER PRIMARY KEY,
                service_name TEXT NOT NULL,
                image_file_name TEXT,
                latest_process_id INTEGER,
                current_state INTEGER,
                current_state_name TEXT,
                start_type INTEGER,
                start_type_name TEXT,
                group_name TEXT,
                group_started_at_utc_us INTEGER,
                group_stopped_at_utc_us INTEGER
            );

            CREATE TABLE service_state_changes (
                id INTEGER PRIMARY KEY,
                service_key INTEGER NOT NULL,
                timestamp_utc_us INTEGER NOT NULL,
                current_state INTEGER,
                current_state_name TEXT,
                process_id INTEGER,
                process_key INTEGER,
                image_name TEXT,
                service_management_thread_id INTEGER,
                service_management_thread_key INTEGER,
                execution_phase INTEGER,
                raw_start_type INTEGER
            );

            CREATE TABLE service_main_intervals (
                id INTEGER PRIMARY KEY,
                service_key INTEGER NOT NULL,
                transition_type INTEGER,
                transition_type_name TEXT,
                main_call_start_utc_us INTEGER NOT NULL,
                main_call_end_utc_us INTEGER NOT NULL,
                main_call_duration_us INTEGER NOT NULL
            );

            CREATE TABLE service_group_intervals (
                id INTEGER PRIMARY KEY,
                group_name TEXT NOT NULL,
                started_at_utc_us INTEGER,
                stopped_at_utc_us INTEGER
            );

            CREATE TABLE service_snapshots (
                id INTEGER PRIMARY KEY,
                snapshot_service_id INTEGER,
                service_name TEXT,
                display_name TEXT,
                group_name TEXT,
                process_id INTEGER,
                process_name TEXT,
                state INTEGER,
                state_name TEXT
            );

            CREATE TABLE memory_snapshots (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                priority_levels INTEGER,
                zero_page_count INTEGER,
                free_page_count INTEGER,
                modified_page_count INTEGER,
                modified_no_write_page_count INTEGER,
                bad_page_count INTEGER,
                modified_page_file_page_count INTEGER,
                paged_pool_page_count INTEGER,
                non_paged_pool_page_count INTEGER,
                mdl_page_count INTEGER,
                commit_page_count INTEGER,
                in_use_page_count INTEGER,
                standby_total_page_count INTEGER,
                non_superfetch_standby_page_count INTEGER,
                repurposed_total_page_count INTEGER,
                page_size_bytes INTEGER
            );

            CREATE TABLE memory_priority_pages (
                id INTEGER PRIMARY KEY,
                memory_snapshot_id INTEGER NOT NULL,
                priority_level INTEGER NOT NULL,
                standby_page_count INTEGER,
                repurposed_page_count INTEGER,
                repurposed_delta_page_count INTEGER
            );

            CREATE TABLE wmi_events (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                event_id INTEGER NOT NULL,
                version INTEGER,
                opcode INTEGER,
                process_id INTEGER,
                thread_id INTEGER,
                process_key INTEGER,
                thread_key INTEGER,
                processor_number INTEGER,
                correlation_id TEXT,
                operation_id INTEGER,
                group_operation_id INTEGER,
                operation TEXT,
                namespace_name TEXT,
                class_name TEXT,
                method_name TEXT,
                implementation_class TEXT,
                client_process_id INTEGER,
                client_process_creation_time_filetime INTEGER,
                client_process_creation_time_utc_us INTEGER,
                client_machine TEXT,
                client_machine_fqdn TEXT,
                user_name TEXT,
                is_local INTEGER,
                protocol TEXT,
                flags INTEGER,
                interval_ms INTEGER,
                query TEXT,
                result_code INTEGER,
                provider_name TEXT,
                provider_guid TEXT,
                host_id INTEGER,
                path TEXT,
                error_id INTEGER,
                message TEXT,
                component_name TEXT,
                message_detail TEXT,
                file_name TEXT,
                event5858_id TEXT,
                possible_cause TEXT,
                code INTEGER,
                host_process TEXT,
                host_process_id INTEGER,
                provider_path TEXT,
                component TEXT
            );

            CREATE TABLE acpi_events (
                id INTEGER PRIMARY KEY,
                event_kind TEXT NOT NULL,
                timestamp_utc_us INTEGER NOT NULL,
                process_id INTEGER,
                thread_id INTEGER,
                process_key INTEGER,
                thread_key INTEGER,
                processor_number INTEGER,
                thermal_zone_device_instance TEXT,
                temperature_raw INTEGER,
                aml_method_name TEXT,
                aml_method_state INTEGER,
                aml_elapsed_time_raw INTEGER,
                frequency INTEGER,
                tmp_raw INTEGER,
                psv_raw INTEGER,
                ac0_raw INTEGER, ac1_raw INTEGER, ac2_raw INTEGER, ac3_raw INTEGER, ac4_raw INTEGER,
                ac5_raw INTEGER, ac6_raw INTEGER, ac7_raw INTEGER, ac8_raw INTEGER, ac9_raw INTEGER,
                hot_raw INTEGER,
                crt_raw INTEGER
            );

            CREATE TABLE kernel_power_events (
                id INTEGER PRIMARY KEY,
                event_id INTEGER NOT NULL,
                timestamp_utc_us INTEGER NOT NULL,
                process_id INTEGER,
                thread_id INTEGER,
                process_key INTEGER,
                thread_key INTEGER,
                requested_resolution_100ns INTEGER,
                requested_resolution_ms REAL,
                target_process_id INTEGER,
                app_name TEXT,
                sub_process_tag INTEGER,
                request_ignored INTEGER
            );

            CREATE TABLE energy_estimates (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                process_id INTEGER,
                process_key INTEGER,
                thread_id INTEGER,
                thread_key INTEGER,
                processor_number INTEGER,
                app_name TEXT,
                user_id INTEGER,
                interval_us INTEGER,
                cpu_energy_mj INTEGER,
                gpu_energy_mj INTEGER,
                display_energy_mj INTEGER,
                disk_energy_mj INTEGER,
                network_energy_mj INTEGER,
                mbb_energy_mj INTEGER,
                loss_energy_uj INTEGER,
                other_energy_mj INTEGER,
                emi_energy_mj INTEGER,
                npu_energy_mj INTEGER,
                work_on_behalf_cpu_energy_mj INTEGER,
                attributed_cpu_energy_mj INTEGER,
                record_flags INTEGER,
                record_measured INTEGER,
                interactivity_state INTEGER,
                interactivity_state_name TEXT,
                is_dc_power INTEGER,
                is_monitor_on INTEGER,
                is_energy_saver INTEGER,
                is_low_power_epoch INTEGER,
                is_foreground INTEGER,
                is_container INTEGER,
                is_committed INTEGER
            );

            CREATE TABLE energy_query_stats (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                sru_work_item_type INTEGER,
                provider_state INTEGER,
                device_state INTEGER
            );

            CREATE TABLE energy_cpu_samples (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                cpu_id INTEGER,
                current_frequency INTEGER,
                last_busy_frequency INTEGER,
                energy_mj INTEGER
            );

            CREATE TABLE energy_component_samples (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                component_code INTEGER,
                component_name TEXT,
                energy_delta_mj INTEGER
            );

            CREATE TABLE standby_samples (
                id INTEGER PRIMARY KEY,
                timestamp_utc_us INTEGER NOT NULL,
                last_standby_total_us INTEGER,
                curr_standby_total_us INTEGER,
                delta_standby_total_us INTEGER,
                last_drips_total_us INTEGER,
                curr_drips_total_us INTEGER,
                delta_drips_total_us INTEGER,
                last_activation_total_us INTEGER,
                curr_activation_total_us INTEGER,
                delta_activation_total_us INTEGER
            );

            CREATE TABLE power_meters (
                meter_id INTEGER PRIMARY KEY,
                channel_name TEXT,
                default_sampling_period_us INTEGER,
                first_seen_utc_us INTEGER,
                last_seen_utc_us INTEGER
            );

            CREATE TABLE power_meter_samples (
                id INTEGER PRIMARY KEY,
                meter_id INTEGER NOT NULL,
                event_id INTEGER NOT NULL,
                timestamp_utc_us INTEGER NOT NULL,
                absolute_energy INTEGER,
                absolute_time INTEGER,
                value INTEGER
            );

            CREATE TABLE disk_io_operations (
                id INTEGER PRIMARY KEY,
                event_id INTEGER,
                version INTEGER,
                opcode INTEGER,
                timestamp_utc_us INTEGER NOT NULL,
                started_at_utc_us INTEGER,
                completed_at_utc_us INTEGER,
                duration_us INTEGER,
                process_id INTEGER,
                process_key INTEGER,
                thread_id INTEGER,
                thread_key INTEGER,
                correlation_id INTEGER,
                transfer_size_bytes INTEGER,
                operation TEXT,
                match_status TEXT
            );

            CREATE TABLE schema_catalog (
                object_name TEXT PRIMARY KEY,
                object_type TEXT NOT NULL,
                description TEXT NOT NULL,
                preferred_for_ai INTEGER NOT NULL
            );

            CREATE TABLE schema_columns (
                object_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                declared_type TEXT NOT NULL,
                unit TEXT,
                is_nullable INTEGER NOT NULL,
                is_primary_key INTEGER NOT NULL,
                description TEXT NOT NULL,
                PRIMARY KEY (object_name, column_name)
            );

            CREATE TABLE schema_relationships (
                id INTEGER PRIMARY KEY,
                from_table TEXT NOT NULL,
                from_column TEXT NOT NULL,
                to_table TEXT NOT NULL,
                to_column TEXT NOT NULL,
                description TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>成功 commit 之後才建立索引與 AI 檢視表：避免大量 INSERT 期間維護索引結構拖慢匯入速度。</summary>
    private static void CreateIndexesAndViews(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE INDEX ix_processes_process_id ON processes(process_id);
            CREATE INDEX ix_threads_process_key ON threads(process_key);
            CREATE INDEX ix_threads_thread_id ON threads(thread_id);
            CREATE INDEX ix_images_process_key ON images(process_key);
            CREATE INDEX ix_process_memory_samples_process_key_time ON process_memory_samples(process_key, sampled_at_utc_us);
            CREATE INDEX ix_thread_cpu_buckets_thread_key_time ON thread_cpu_buckets(thread_key, bucket_start_utc_us);
            CREATE INDEX ix_thread_cpu_buckets_process_key_time ON thread_cpu_buckets(process_key, bucket_start_utc_us);
            CREATE INDEX ix_thread_wait_reason_buckets_bucket ON thread_wait_reason_buckets(thread_cpu_bucket_id);
            CREATE INDEX ix_processor_cpu_buckets_time ON processor_cpu_buckets(processor_number, bucket_start_utc_us);
            CREATE INDEX ix_dpc_events_time ON dpc_events(start_utc_us);
            CREATE INDEX ix_dpc_events_module ON dpc_events(handler_module_name);
            CREATE INDEX ix_isr_events_time ON isr_events(start_utc_us);
            CREATE INDEX ix_isr_events_module ON isr_events(handler_module_name);
            CREATE INDEX ix_profile_samples_process_key_time ON profile_samples(process_key, timestamp_utc_us);
            CREATE INDEX ix_service_state_changes_service_key_time ON service_state_changes(service_key, timestamp_utc_us);
            CREATE INDEX ix_service_main_intervals_service_key ON service_main_intervals(service_key);
            CREATE INDEX ix_memory_priority_pages_snapshot ON memory_priority_pages(memory_snapshot_id);
            CREATE INDEX ix_memory_snapshots_time ON memory_snapshots(timestamp_utc_us);
            CREATE INDEX ix_wmi_events_time ON wmi_events(timestamp_utc_us);
            CREATE INDEX ix_wmi_events_event_id ON wmi_events(event_id);
            CREATE INDEX ix_wmi_events_operation_id ON wmi_events(operation_id);
            CREATE INDEX ix_wmi_events_process_key ON wmi_events(process_key);
            CREATE INDEX ix_wmi_events_pair_operation ON wmi_events(process_id, operation_id, event_id, timestamp_utc_us);
            CREATE INDEX ix_wmi_events_pair_provider ON wmi_events(process_id, group_operation_id, event_id, timestamp_utc_us);
            CREATE INDEX ix_acpi_events_time ON acpi_events(timestamp_utc_us);
            CREATE INDEX ix_acpi_events_kind ON acpi_events(event_kind);
            CREATE INDEX ix_kernel_power_events_time ON kernel_power_events(timestamp_utc_us);
            CREATE INDEX ix_energy_estimates_process_key_time ON energy_estimates(process_key, timestamp_utc_us);
            CREATE INDEX ix_energy_estimates_time ON energy_estimates(timestamp_utc_us);
            CREATE INDEX ix_power_meter_samples_meter_time ON power_meter_samples(meter_id, timestamp_utc_us);
            CREATE INDEX ix_disk_io_operations_process_key_time ON disk_io_operations(process_key, timestamp_utc_us);

            CREATE VIEW ai_process_overview AS
            SELECT
                p.process_key,
                p.process_id,
                p.image_file_name,
                p.command_line,
                p.parent_process_key,
                parent.image_file_name AS parent_image_file_name,
                p.started_at_utc_us,
                p.ended_at_utc_us,
                CASE WHEN p.ended_at_utc_us IS NULL THEN NULL ELSE (p.ended_at_utc_us - p.started_at_utc_us) END AS lifetime_us,
                p.exit_code,
                p.package_full_name
            FROM processes p
            LEFT JOIN processes parent ON parent.process_key = p.parent_process_key;

            CREATE VIEW ai_process_memory_latest AS
            WITH ranked AS (
                SELECT s.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY s.process_key
                           ORDER BY s.sampled_at_utc_us DESC, s.id DESC
                       ) AS sample_rank
                FROM process_memory_samples s
                WHERE s.process_key IS NOT NULL
            )
            SELECT r.process_key, p.image_file_name, r.sampled_at_utc_us, r.working_set_bytes, r.private_bytes,
                   r.virtual_bytes, r.peak_working_set_bytes, r.peak_virtual_bytes, r.page_fault_count, r.handle_count
            FROM ranked r
            JOIN processes p ON p.process_key = r.process_key
            WHERE r.sample_rank = 1;

            CREATE VIEW ai_cpu_by_process AS
            SELECT b.process_key, p.image_file_name, p.process_id,
                   COUNT(*) AS bucket_count,
                   SUM(b.run_duration_us) AS total_run_duration_us,
                   SUM(b.switch_in_count) AS total_switch_in_count,
                   MIN(b.bucket_start_utc_us) AS first_bucket_start_utc_us,
                   MAX(b.bucket_end_utc_us) AS last_bucket_end_utc_us
            FROM thread_cpu_buckets b
            JOIN processes p ON p.process_key = b.process_key
            GROUP BY b.process_key;

            CREATE VIEW ai_interrupt_hotspots AS
            SELECT 'dpc' AS event_kind, handler_module_name, COUNT(*) AS event_count,
                   SUM(duration_us) AS total_duration_us, AVG(duration_us) AS avg_duration_us, MAX(duration_us) AS max_duration_us
            FROM dpc_events GROUP BY handler_module_name
            UNION ALL
            SELECT 'isr' AS event_kind, handler_module_name, COUNT(*) AS event_count,
                   SUM(duration_us) AS total_duration_us, AVG(duration_us) AS avg_duration_us, MAX(duration_us) AS max_duration_us
            FROM isr_events GROUP BY handler_module_name;

            CREATE VIEW ai_service_lifecycle AS
            SELECT s.service_key, s.service_name, s.image_file_name, s.current_state_name, s.start_type_name, s.group_name,
                   sc.timestamp_utc_us, sc.current_state_name AS transition_state_name, sc.process_key
            FROM services s
            LEFT JOIN service_state_changes sc ON sc.service_key = s.service_key
            ORDER BY s.service_key, sc.timestamp_utc_us;

            CREATE VIEW ai_wmi_operations AS
            WITH calls AS (
                SELECT e.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY e.process_id, e.operation_id
                           ORDER BY e.timestamp_utc_us, e.id
                       ) AS operation_sequence
                FROM wmi_events e
                WHERE e.event_id IN (11, 20, 22)
            ),
            stops AS (
                SELECT e.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY e.process_id, e.operation_id
                           ORDER BY e.timestamp_utc_us, e.id
                       ) AS operation_sequence
                FROM wmi_events e
                WHERE e.event_id = 13
            )
            SELECT
                calls.event_id AS start_event_id,
                calls.operation_id,
                calls.group_operation_id,
                calls.timestamp_utc_us AS started_at_utc_us,
                stops.timestamp_utc_us AS stopped_at_utc_us,
                CASE WHEN stops.timestamp_utc_us IS NULL THEN NULL ELSE stops.timestamp_utc_us - calls.timestamp_utc_us END AS duration_us,
                calls.namespace_name,
                CASE calls.event_id WHEN 11 THEN 'query' WHEN 20 THEN 'async_notification' WHEN 22 THEN 'method' END AS operation_kind,
                CASE
                    WHEN calls.event_id = 22 THEN COALESCE(calls.class_name, '') || '::' || COALESCE(calls.method_name, '')
                    ELSE calls.operation
                END AS operation_target,
                calls.client_process_id,
                calls.client_process_creation_time_utc_us,
                calls.client_machine,
                calls.user_name,
                stops.result_code,
                (
                    SELECT provider.provider_name
                    FROM wmi_events provider
                    WHERE provider.event_id = 12
                      AND provider.process_id = calls.process_id
                      AND provider.group_operation_id = calls.group_operation_id
                    ORDER BY provider.timestamp_utc_us, provider.id
                    LIMIT 1
                ) AS provider_name
            FROM calls
            LEFT JOIN stops
              ON stops.process_id = calls.process_id
             AND stops.operation_id = calls.operation_id
             AND stops.operation_sequence = calls.operation_sequence
             AND stops.timestamp_utc_us >= calls.timestamp_utc_us
            UNION ALL
            SELECT
                polling.event_id,
                NULL,
                polling.group_operation_id,
                polling.timestamp_utc_us,
                NULL,
                NULL,
                polling.namespace_name,
                'polling_query',
                polling.query,
                polling.client_process_id,
                NULL,
                NULL,
                NULL,
                NULL,
                (
                    SELECT provider.provider_name
                    FROM wmi_events provider
                    WHERE provider.event_id = 12
                      AND provider.process_id = polling.process_id
                      AND provider.group_operation_id = polling.group_operation_id
                    ORDER BY provider.timestamp_utc_us, provider.id
                    LIMIT 1
                )
            FROM wmi_events polling
            WHERE polling.event_id = 24;

            CREATE VIEW ai_energy_by_app AS
            SELECT app_name,
                   COUNT(*) AS sample_count,
                   COUNT(DISTINCT process_key) AS process_count,
                   SUM(cpu_energy_mj) AS total_cpu_energy_mj,
                   SUM(gpu_energy_mj) AS total_gpu_energy_mj,
                   SUM(display_energy_mj) AS total_display_energy_mj,
                   SUM(disk_energy_mj) AS total_disk_energy_mj,
                   SUM(network_energy_mj) AS total_network_energy_mj,
                   SUM(mbb_energy_mj) AS total_mbb_energy_mj,
                   SUM(loss_energy_uj) / 1000.0 AS total_loss_energy_mj,
                   SUM(other_energy_mj) AS total_other_energy_mj,
                   SUM(emi_energy_mj) AS total_emi_energy_mj,
                   SUM(npu_energy_mj) AS total_npu_energy_mj,
                   SUM(cpu_energy_mj + gpu_energy_mj + display_energy_mj + disk_energy_mj + network_energy_mj +
                       mbb_energy_mj + (loss_energy_uj / 1000.0) + other_energy_mj + emi_energy_mj + npu_energy_mj) AS total_energy_mj,
                   SUM(CASE WHEN is_foreground = 1 THEN 1 ELSE 0 END) AS foreground_sample_count
            FROM energy_estimates
            GROUP BY app_name;

            CREATE VIEW ai_memory_timeline AS
            SELECT timestamp_utc_us, free_page_count * page_size_bytes AS free_bytes,
                   in_use_page_count * page_size_bytes AS in_use_bytes,
                   standby_total_page_count * page_size_bytes AS standby_bytes,
                   commit_page_count * page_size_bytes AS commit_bytes
            FROM memory_snapshots
            ORDER BY timestamp_utc_us;

            CREATE VIEW ai_thermal_trend AS
            SELECT timestamp_utc_us, thermal_zone_device_instance, temperature_raw
            FROM acpi_events
            WHERE event_kind = 'temperature_change'
            ORDER BY timestamp_utc_us;
            """;
        command.ExecuteNonQuery();
        WriteSchemaMetadata(connection, transaction);
        transaction.Commit();
    }

    private static long WriteTraceInfo(SqliteConnection connection, SqliteTransaction transaction, EtlReadResult result, string sourceEtlPath)
    {
        TableWriter writer = new(
            connection, transaction, "trace_info",
            "id", "schema_version", "source_etl_path", "imported_at_utc_us", "trace_start_utc_us", "trace_end_utc_us",
            "processor_count", "usable_memory_bytes", "memory_page_size_bytes", "buffers_lost", "events_lost", "perf_frequency_hz");
        writer.Set("id", 1)
            .Set("schema_version", EtlSqliteSchema.CurrentVersion)
            .Set("source_etl_path", sourceEtlPath)
            .Set("imported_at_utc_us", ToUnixMicros(DateTime.UtcNow))
            .Set("trace_start_utc_us", ToUnixMicros(result.TraceStartTime))
            .Set("trace_end_utc_us", ToUnixMicros(result.TraceEndTime))
            .Set("processor_count", result.ProcessorCount)
            .Set("usable_memory_bytes", ToSigned(result.UsableMemorySizeBytes))
            .Set("memory_page_size_bytes", result.MemoryPageSizeBytes)
            .Set("buffers_lost", result.BuffersLost)
            .Set("events_lost", result.EventsLost)
            .Set("perf_frequency_hz", result.PerfFreq);
        writer.Insert();
        return 1;
    }

    private static long WriteProcesses(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ProcessRecord> processes, Dictionary<ProcessRecord, long> keys)
    {
        TableWriter writer = new(
            connection, transaction, "processes",
            "process_key", "process_id", "parent_process_id", "parent_process_key", "session_id",
            "object_address", "directory_table_base", "flags", "exit_code", "user_sid", "package_full_name",
            "application_id", "image_file_name", "command_line", "started_at_utc_us", "ended_at_utc_us");

        long count = 0;
        foreach (ProcessRecord process in processes)
        {
            writer.Set("process_key", keys[process])
                .Set("process_id", process.ProcessId)
                .Set("parent_process_id", process.ParentProcessId)
                .Set("parent_process_key", process.Parent is ProcessRecord parent && keys.TryGetValue(parent, out long parentKey) ? parentKey : null)
                .Set("session_id", process.SessionId)
                .Set("object_address", ToSigned(process.ObjectAddress))
                .Set("directory_table_base", ToSigned(process.DirectoryTableBase))
                .Set("flags", process.Flags)
                .Set("exit_code", process.ExitCode)
                .Set("user_sid", process.UserSid)
                .Set("package_full_name", process.PackageFullName)
                .Set("application_id", process.ApplicationId)
                .Set("image_file_name", process.ImageFileName)
                .Set("command_line", process.CommandLine)
                .Set("started_at_utc_us", ToUnixMicros(process.StartTime))
                .Set("ended_at_utc_us", ToUnixMicros(process.EndTime));
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteThreads(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ThreadRecord> threads, Dictionary<ThreadRecord, long> keys, ProcessResolver processResolver)
    {
        TableWriter writer = new(
            connection, transaction, "threads",
            "thread_key", "thread_id", "process_key", "process_id", "created_at_utc_us", "exited_at_utc_us",
            "stack_base", "stack_limit", "user_stack_base", "user_stack_limit", "processor_affinity",
            "start_address", "start_address_module_name", "start_address_module_offset", "teb_base",
            "service_id", "base_priority", "page_priority", "page_priority_name", "io_priority", "io_priority_name", "thread_name");

        long count = 0;
        foreach (ThreadRecord thread in threads)
        {
            DateTime referenceTime = thread.CreateTime ?? thread.ExitTime ?? DateTime.MinValue;
            long? processKey = processResolver.ResolveKey(thread.ProcessId, referenceTime);
            writer.Set("thread_key", keys[thread])
                .Set("thread_id", thread.ThreadId)
                .Set("process_key", processKey)
                .Set("process_id", thread.ProcessId)
                .Set("created_at_utc_us", ToUnixMicros(thread.CreateTime))
                .Set("exited_at_utc_us", ToUnixMicros(thread.ExitTime))
                .Set("stack_base", ToSigned(thread.StackBase))
                .Set("stack_limit", ToSigned(thread.StackLimit))
                .Set("user_stack_base", ToSigned(thread.UserStackBase))
                .Set("user_stack_limit", ToSigned(thread.UserStackLimit))
                .Set("processor_affinity", ToSigned(thread.ProcessorAffinity))
                .Set("start_address", ToSigned(thread.StartAddress))
                .Set("start_address_module_name", thread.StartAddressModuleName)
                .Set("start_address_module_offset", ToSigned(thread.StartAddressModuleOffset))
                .Set("teb_base", ToSigned(thread.TebBase))
                .Set("service_id", thread.ServiceId)
                .Set("base_priority", thread.BasePriority)
                .Set("page_priority", (int?)thread.PagePriority)
                .Set("page_priority_name", thread.PagePriority?.ToString())
                .Set("io_priority", (int?)thread.IoPriority)
                .Set("io_priority_name", thread.IoPriority?.ToString())
                .Set("thread_name", thread.Name);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteImages(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ProcessRecord> processes, Dictionary<ProcessRecord, long> processKeys)
    {
        TableWriter writer = new(
            connection, transaction, "images",
            "process_key", "process_id", "file_name", "image_base", "image_size", "default_base",
            "image_checksum", "time_date_stamp", "loaded_at_utc_us", "unloaded_at_utc_us", "is_rundown");

        long count = 0;
        foreach (ProcessRecord process in processes)
        {
            long processKey = processKeys[process];
            foreach (ModuleInfo module in process.Modules)
            {
                writer.Set("process_key", processKey)
                    .Set("process_id", module.ProcessId)
                    .Set("file_name", module.FileName)
                    .Set("image_base", ToSigned(module.ImageBase))
                    .Set("image_size", ToSigned(module.ImageSize))
                    .Set("default_base", ToSigned(module.DefaultBase))
                    .Set("image_checksum", module.ImageCheckSum)
                    .Set("time_date_stamp", module.TimeDateStamp)
                    .Set("loaded_at_utc_us", module.IsRundown ? null : ToUnixMicros(module.LoadTime))
                    .Set("unloaded_at_utc_us", ToUnixMicros(module.UnloadTime))
                    .Set("is_rundown", module.IsRundown ? 1 : 0);
                writer.Insert();
                count++;
            }
        }

        return count;
    }

    private static long WriteProcessMemorySamples(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ProcessCounterEventInfo> samples, ProcessResolver processResolver)
    {
        TableWriter writer = new(
            connection, transaction, "process_memory_samples",
            "process_key", "process_id", "sampled_at_utc_us", "working_set_bytes", "peak_working_set_bytes",
            "virtual_bytes", "peak_virtual_bytes", "private_bytes", "page_fault_count", "handle_count");

        long count = 0;
        foreach (ProcessCounterEventInfo sample in samples)
        {
            writer.Set("process_key", processResolver.ResolveKey(sample.ProcessId, sample.Timestamp))
                .Set("process_id", sample.ProcessId)
                .Set("sampled_at_utc_us", ToUnixMicros(sample.Timestamp))
                .Set("working_set_bytes", ToSigned(sample.WorkingSetSize))
                .Set("peak_working_set_bytes", ToSigned(sample.PeakWorkingSetSize))
                .Set("virtual_bytes", ToSigned(sample.VirtualSize))
                .Set("peak_virtual_bytes", ToSigned(sample.PeakVirtualSize))
                .Set("private_bytes", ToSigned(sample.PagefileUsage))
                .Set("page_fault_count", sample.PageFaultCount)
                .Set("handle_count", sample.HandleCount);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteCpuBuckets(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<CSwitchThreadBucket> threadBuckets, IReadOnlyList<CSwitchProcessorBucket> processorBuckets,
        ThreadResolver threadResolver, ProcessResolver processResolver)
    {
        TableWriter threadWriter = new(
            connection, transaction, "thread_cpu_buckets", returnsRowId: true,
            "thread_key", "process_key", "thread_id", "process_id", "bucket_start_utc_us", "bucket_end_utc_us",
            "switch_in_count", "switch_out_count", "run_duration_us", "min_priority", "max_priority",
            "ideal_processor_mismatch_count");
        TableWriter waitReasonWriter = new(
            connection, transaction, "thread_wait_reason_buckets",
            "thread_cpu_bucket_id", "wait_reason", "wait_reason_name", "event_count");

        long count = 0;
        foreach (CSwitchThreadBucket bucket in threadBuckets)
        {
            long? threadKey = threadResolver.ResolveKey(bucket.ThreadId, bucket.BucketStartUtc);
            long? processKey = processResolver.ResolveKey(bucket.ProcessId, bucket.BucketStartUtc);
            threadWriter.Set("thread_key", threadKey)
                .Set("process_key", processKey)
                .Set("thread_id", bucket.ThreadId)
                .Set("process_id", bucket.ProcessId)
                .Set("bucket_start_utc_us", ToUnixMicros(bucket.BucketStartUtc))
                .Set("bucket_end_utc_us", ToUnixMicros(bucket.BucketEndUtc))
                .Set("switch_in_count", bucket.SwitchInCount)
                .Set("switch_out_count", bucket.SwitchOutCount)
                .Set("run_duration_us", bucket.RunDurationTicks / 10)
                .Set("min_priority", bucket.MinPriority)
                .Set("max_priority", bucket.MaxPriority)
                .Set("ideal_processor_mismatch_count", bucket.IdealProcessorMismatchCount);
            long bucketId = threadWriter.InsertAndGetRowId();
            count++;

            foreach ((int waitReason, int eventCount) in bucket.WaitReasonHistogram)
            {
                waitReasonWriter.Set("thread_cpu_bucket_id", bucketId)
                    .Set("wait_reason", waitReason)
                    .Set("wait_reason_name", DescribeWaitReason(waitReason))
                    .Set("event_count", eventCount);
                waitReasonWriter.Insert();
                count++;
            }
        }

        TableWriter processorWriter = new(
            connection, transaction, "processor_cpu_buckets",
            "processor_number", "bucket_start_utc_us", "bucket_end_utc_us", "context_switch_count",
            "distinct_thread_count", "busy_duration_us", "idle_duration_us");
        foreach (CSwitchProcessorBucket bucket in processorBuckets)
        {
            processorWriter.Set("processor_number", bucket.ProcessorNumber)
                .Set("bucket_start_utc_us", ToUnixMicros(bucket.BucketStartUtc))
                .Set("bucket_end_utc_us", ToUnixMicros(bucket.BucketEndUtc))
                .Set("context_switch_count", bucket.ContextSwitchCount)
                .Set("distinct_thread_count", bucket.DistinctThreadCount)
                .Set("busy_duration_us", bucket.BusyDurationTicks / 10)
                .Set("idle_duration_us", bucket.IdleDurationTicks / 10);
            processorWriter.Insert();
            count++;
        }

        return count;
    }

    /// <summary>對應 Windows KWAIT_REASON 列舉的常用子集，其餘數值仍保留原始碼、名稱標示為 Other(N)。</summary>
    private static string DescribeWaitReason(int reason) => reason switch
    {
        0 => "Executive",
        5 => "WrDelayExecution",
        7 => "WrSuspended",
        13 => "WrQueue",
        31 => "WrDispatchInt",
        32 => "WrPreempted",
        _ => $"Other({reason})",
    };

    private static long WriteInterruptEvents(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<InterruptServiceRoutineInterval> isrIntervals,
        IReadOnlyList<DeferredProcedureCallInterval> dpcIntervals)
    {
        TableWriter isrWriter = new(
            connection, transaction, "isr_events",
            "start_utc_us", "timestamp_utc_us", "duration_us", "processor_number",
            "handler_routine_address", "handler_module_name", "handler_module_offset", "vector", "return_value");

        long count = 0;
        foreach (InterruptServiceRoutineInterval interval in isrIntervals)
        {
            long startUs = ToUnixMicros(interval.StartTime);
            long stopUs = ToUnixMicros(interval.StopTime);
            isrWriter.Set("start_utc_us", startUs)
                .Set("timestamp_utc_us", stopUs)
                .Set("duration_us", stopUs - startUs)
                .Set("processor_number", interval.Processor)
                .Set("handler_routine_address", ToSigned(interval.HandlerRoutineAddress))
                .Set("handler_module_name", interval.HandlerModuleName)
                .Set("handler_module_offset", ToSigned(interval.HandlerStackFrame?.RelativeVirtualAddress))
                .Set("vector", interval.Vector)
                .Set("return_value", interval.ReturnValue);
            isrWriter.Insert();
            count++;
        }

        TableWriter dpcWriter = new(
            connection, transaction, "dpc_events",
            "start_utc_us", "timestamp_utc_us", "duration_us", "processor_number",
            "call_type", "call_type_name", "handler_routine_address", "handler_module_name", "handler_module_offset");
        foreach (DeferredProcedureCallInterval interval in dpcIntervals)
        {
            long startUs = ToUnixMicros(interval.StartTime);
            long stopUs = ToUnixMicros(interval.StopTime);
            dpcWriter.Set("start_utc_us", startUs)
                .Set("timestamp_utc_us", stopUs)
                .Set("duration_us", stopUs - startUs)
                .Set("processor_number", interval.Processor)
                .Set("call_type", (int)interval.CallType)
                .Set("call_type_name", interval.CallType.ToString())
                .Set("handler_routine_address", ToSigned(interval.HandlerRoutineAddress))
                .Set("handler_module_name", interval.HandlerModuleName)
                .Set("handler_module_offset", ToSigned(interval.HandlerStackFrame?.RelativeVirtualAddress));
            dpcWriter.Insert();
            count++;
        }

        return count;
    }

    private static long WriteProfileSamples(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<BufferedProfileSample> samples, ProcessResolver processResolver, ThreadResolver threadResolver)
    {
        TableWriter writer = new(
            connection, transaction, "profile_samples",
            "timestamp_utc_us", "processor_number", "process_key", "thread_key", "process_id", "thread_id", "instruction_pointer");

        long count = 0;
        foreach (BufferedProfileSample sample in samples)
        {
            writer.Set("timestamp_utc_us", ToUnixMicros(sample.Timestamp))
                .Set("processor_number", sample.ProcessorNumber)
                .Set("process_key", processResolver.ResolveKey(sample.ProcessId, sample.Timestamp))
                .Set("thread_key", threadResolver.ResolveKey(sample.ThreadId, sample.Timestamp))
                .Set("process_id", sample.ProcessId)
                .Set("thread_id", sample.ThreadId)
                .Set("instruction_pointer", ToSigned(sample.InstructionPointer));
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteServices(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ServiceRecord> services, IReadOnlyList<ServiceSnapshotRecord> snapshots,
        ProcessResolver processResolver, ThreadResolver threadResolver)
    {
        TableWriter serviceWriter = new(
            connection, transaction, "services",
            "service_key", "service_name", "image_file_name", "latest_process_id", "current_state", "current_state_name",
            "start_type", "start_type_name", "group_name", "group_started_at_utc_us", "group_stopped_at_utc_us");
        TableWriter stateChangeWriter = new(
            connection, transaction, "service_state_changes",
            "service_key", "timestamp_utc_us", "current_state", "current_state_name", "process_id", "process_key",
            "image_name", "service_management_thread_id", "service_management_thread_key", "execution_phase", "raw_start_type");
        TableWriter mainIntervalWriter = new(
            connection, transaction, "service_main_intervals",
            "service_key", "transition_type", "transition_type_name",
            "main_call_start_utc_us", "main_call_end_utc_us", "main_call_duration_us");

        long count = 0;
        long nextServiceKey = 1;
        foreach (ServiceRecord service in services)
        {
            long serviceKey = nextServiceKey++;
            serviceWriter.Set("service_key", serviceKey)
                .Set("service_name", service.ServiceName)
                .Set("image_file_name", service.ImageFileName)
                .Set("latest_process_id", service.ProcessId)
                .Set("current_state", (int?)service.CurrentState)
                .Set("current_state_name", service.CurrentState?.ToString())
                .Set("start_type", (int?)service.StartType)
                .Set("start_type_name", service.StartType?.ToString())
                .Set("group_name", service.Group?.Name)
                .Set("group_started_at_utc_us", ToUnixMicros(service.Group?.StartTime))
                .Set("group_stopped_at_utc_us", ToUnixMicros(service.Group?.StopTime));
            serviceWriter.Insert();
            count++;

            foreach (ServiceStateChangeRecord change in service.StateChanges)
            {
                stateChangeWriter.Set("service_key", serviceKey)
                    .Set("timestamp_utc_us", ToUnixMicros(change.Timestamp))
                    .Set("current_state", (int)change.State)
                    .Set("current_state_name", change.State.ToString())
                    .Set("process_id", change.ProcessId)
                    .Set("process_key", processResolver.ResolveKey(change.ProcessId, change.Timestamp))
                    .Set("image_name", change.ImageName)
                    .Set("service_management_thread_id", change.ServiceManagementThreadId)
                    .Set("service_management_thread_key", threadResolver.ResolveKey(change.ServiceManagementThreadId, change.Timestamp))
                    .Set("execution_phase", change.ExecutionPhase)
                    .Set("raw_start_type", change.RawStartType);
                stateChangeWriter.Insert();
                count++;
            }

            foreach (ServiceTransitionRecord transition in service.Transitions)
            {
                if (transition.ServiceMainCallTimeRange is not ServiceMainCallTimeRange range)
                {
                    continue;
                }

                long startUs = ToUnixMicros(range.StartTime);
                long endUs = ToUnixMicros(range.EndTime);
                mainIntervalWriter.Set("service_key", serviceKey)
                    .Set("transition_type", (int)transition.TransitionType)
                    .Set("transition_type_name", transition.TransitionType.ToString())
                    .Set("main_call_start_utc_us", startUs)
                    .Set("main_call_end_utc_us", endUs)
                    .Set("main_call_duration_us", endUs - startUs);
                mainIntervalWriter.Insert();
                count++;
            }
        }

        TableWriter groupWriter = new(
            connection, transaction, "service_group_intervals",
            "group_name", "started_at_utc_us", "stopped_at_utc_us");
        HashSet<ServiceGroupRecord> seenGroups = new(ReferenceEqualityComparer.Instance);
        foreach (ServiceRecord service in services)
        {
            if (service.Group is ServiceGroupRecord group && seenGroups.Add(group))
            {
                groupWriter.Set("group_name", group.Name)
                    .Set("started_at_utc_us", ToUnixMicros(group.StartTime))
                    .Set("stopped_at_utc_us", ToUnixMicros(group.StopTime));
                groupWriter.Insert();
                count++;
            }
        }

        TableWriter snapshotWriter = new(
            connection, transaction, "service_snapshots",
            "snapshot_service_id", "service_name", "display_name", "group_name", "process_id", "process_name",
            "state", "state_name");
        foreach (ServiceSnapshotRecord snapshot in snapshots)
        {
            snapshotWriter.Set("snapshot_service_id", snapshot.Id)
                .Set("service_name", snapshot.Name)
                .Set("display_name", snapshot.DisplayName)
                .Set("group_name", snapshot.GroupName)
                .Set("process_id", snapshot.ProcessId)
                .Set("process_name", snapshot.ProcessName)
                .Set("state", (int)snapshot.State)
                .Set("state_name", snapshot.State.ToString());
            snapshotWriter.Insert();
            count++;
        }

        return count;
    }

    private static long WriteMemorySnapshots(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<MemoryUtilizationEventInfo> snapshots)
    {
        TableWriter writer = new(
            connection, transaction, "memory_snapshots", returnsRowId: true,
            "timestamp_utc_us", "priority_levels", "zero_page_count", "free_page_count", "modified_page_count",
            "modified_no_write_page_count", "bad_page_count", "modified_page_file_page_count", "paged_pool_page_count",
            "non_paged_pool_page_count", "mdl_page_count", "commit_page_count", "in_use_page_count",
            "standby_total_page_count", "non_superfetch_standby_page_count", "repurposed_total_page_count", "page_size_bytes");
        TableWriter priorityWriter = new(
            connection, transaction, "memory_priority_pages",
            "memory_snapshot_id", "priority_level", "standby_page_count", "repurposed_page_count", "repurposed_delta_page_count");

        long count = 0;
        foreach (MemoryUtilizationEventInfo snapshot in snapshots)
        {
            writer.Set("timestamp_utc_us", ToUnixMicros(snapshot.Timestamp))
                .Set("priority_levels", snapshot.PriorityLevels)
                .Set("zero_page_count", snapshot.ZeroPageCount)
                .Set("free_page_count", snapshot.FreePageCount)
                .Set("modified_page_count", snapshot.ModifiedPageCount)
                .Set("modified_no_write_page_count", snapshot.ModifiedNoWritePageCount)
                .Set("bad_page_count", snapshot.BadPageCount)
                .Set("modified_page_file_page_count", snapshot.ModifiedPageFilePageCount)
                .Set("paged_pool_page_count", snapshot.PagedPoolPageCount)
                .Set("non_paged_pool_page_count", snapshot.NonPagedPoolPageCount)
                .Set("mdl_page_count", snapshot.MdlPageCount)
                .Set("commit_page_count", snapshot.CommitPageCount)
                .Set("in_use_page_count", snapshot.InUsePageCount)
                .Set("standby_total_page_count", snapshot.StandbyTotalPageCount)
                .Set("non_superfetch_standby_page_count", snapshot.NonSuperfetchStandbyPageCount)
                .Set("repurposed_total_page_count", snapshot.RepurposedTotalPageCount)
                .Set("page_size_bytes", MemoryUtilizationEventInfo.PageSizeBytes);
            long snapshotId = writer.InsertAndGetRowId();
            count++;

            int levels = Math.Max(snapshot.StandbyPriorityPageCounts.Count, snapshot.RepurposedPriorityPageCounts.Count);
            for (int level = 0; level < levels; level++)
            {
                long standby = level < snapshot.StandbyPriorityPageCounts.Count ? snapshot.StandbyPriorityPageCounts[level] : 0;
                long repurposed = level < snapshot.RepurposedPriorityPageCounts.Count ? snapshot.RepurposedPriorityPageCounts[level] : 0;
                long repurposedDelta = level < snapshot.RepurposedSubListPageCounts.Count ? snapshot.RepurposedSubListPageCounts[level] : 0;
                priorityWriter.Set("memory_snapshot_id", snapshotId)
                    .Set("priority_level", level)
                    .Set("standby_page_count", standby)
                    .Set("repurposed_page_count", repurposed)
                    .Set("repurposed_delta_page_count", repurposedDelta);
                priorityWriter.Insert();
                count++;
            }
        }

        return count;
    }

    private static long WriteWmiEvents(
        SqliteConnection connection, SqliteTransaction transaction,
        ProcessResolver processResolver, ThreadResolver threadResolver,
        IReadOnlyList<WmiActivityEventInfo_11> wmi11, IReadOnlyList<WmiActivityEventInfo_12> wmi12,
        IReadOnlyList<WmiActivityEventInfo_13> wmi13, IReadOnlyList<WmiActivityEventInfo_16> wmi16,
        IReadOnlyList<WmiActivityEventInfo_17> wmi17, IReadOnlyList<WmiActivityEventInfo_20> wmi20,
        IReadOnlyList<WmiActivityEventInfo_22> wmi22, IReadOnlyList<WmiActivityEventInfo_24> wmi24,
        IReadOnlyList<WmiActivityEventInfo_100> wmi100, IReadOnlyList<WmiActivityEventInfo_101> wmi101,
        IReadOnlyList<WmiActivityEventInfo_5857> wmi5857, IReadOnlyList<WmiActivityEventInfo_5858> wmi5858)
    {
        TableWriter writer = new(
            connection, transaction, "wmi_events",
            "timestamp_utc_us", "event_id", "version", "opcode", "process_id", "thread_id", "process_key", "thread_key",
            "processor_number", "correlation_id", "operation_id", "group_operation_id", "operation", "namespace_name",
            "class_name", "method_name", "implementation_class", "client_process_id",
            "client_process_creation_time_filetime", "client_process_creation_time_utc_us",
            "client_machine", "client_machine_fqdn", "user_name", "is_local", "protocol", "flags", "interval_ms", "query",
            "result_code", "provider_name", "provider_guid", "host_id", "path", "error_id", "message", "component_name",
            "message_detail", "file_name", "event5858_id", "possible_cause", "code", "host_process", "host_process_id",
            "provider_path", "component");

        void Base(DateTime timestamp, ushort eventId, byte version, byte opcode, uint processId, uint threadId)
        {
            writer.Set("timestamp_utc_us", ToUnixMicros(timestamp))
                .Set("event_id", eventId)
                .Set("version", version)
                .Set("opcode", opcode)
                .Set("process_id", processId)
                .Set("thread_id", threadId)
                .Set("process_key", processResolver.ResolveKey(processId, timestamp))
                .Set("thread_key", threadResolver.ResolveKey(threadId, timestamp));
        }

        long count = 0;

        foreach (WmiActivityEventInfo_11 e in wmi11)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("processor_number", e.ProcessorNumber).Set("correlation_id", e.CorrelationId)
                .Set("operation_id", e.OperationId).Set("group_operation_id", e.GroupOperationId)
                .Set("operation", e.Operation).Set("namespace_name", e.NamespaceName)
                .Set("client_process_id", e.ClientProcessId)
                .Set("client_process_creation_time_filetime", ToSigned(e.ClientProcessCreationTime))
                .Set("client_process_creation_time_utc_us", FileTimeToUnixMicros(e.ClientProcessCreationTime))
                .Set("client_machine", e.ClientMachine).Set("client_machine_fqdn", e.ClientMachineFQDN)
                .Set("user_name", e.User).Set("is_local", e.IsLocal ? 1 : 0);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_12 e in wmi12)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("processor_number", e.ProcessorNumber).Set("group_operation_id", e.GroupOperationId)
                .Set("operation", e.Operation).Set("host_id", e.HostId)
                .Set("provider_name", e.ProviderName).Set("provider_guid", e.ProviderGuid).Set("path", e.Path);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_13 e in wmi13)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("processor_number", e.ProcessorNumber).Set("operation_id", e.OperationId).Set("result_code", e.ResultCode);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_16 e in wmi16)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("operation_id", e.OperationId).Set("operation", e.Operation)
                .Set("error_id", e.ErrorId).Set("message", e.Message);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_17 e in wmi17)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("correlation_id", e.CorrelationId).Set("protocol", e.Protocol).Set("operation", e.Operation)
                .Set("user_name", e.User).Set("namespace_name", e.Namespace);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_20 e in wmi20)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("operation_id", e.OperationID).Set("operation", e.Operation).Set("flags", e.Flags)
                .Set("client_process_id", e.ClientProcessId).Set("client_machine_fqdn", e.ClientMachineFQDN)
                .Set("client_process_creation_time_filetime", ToSigned(e.ClientProcessCreationTime))
                .Set("client_process_creation_time_utc_us", FileTimeToUnixMicros(e.ClientProcessCreationTime))
                .Set("is_local", e.IsLocal ? 1 : 0);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_22 e in wmi22)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("processor_number", e.ProcessorNumber).Set("correlation_id", e.CorrelationId)
                .Set("operation_id", e.OperationId).Set("group_operation_id", e.GroupOperationId)
                .Set("class_name", e.ClassName).Set("method_name", e.MethodName)
                .Set("implementation_class", e.ImplementationClass).Set("client_machine", e.ClientMachine)
                .Set("client_machine_fqdn", e.ClientMachineFQDN).Set("user_name", e.User)
                .Set("client_process_id", e.ClientProcessId)
                .Set("client_process_creation_time_filetime", ToSigned(e.ClientProcessCreationTime))
                .Set("client_process_creation_time_utc_us", FileTimeToUnixMicros(e.ClientProcessCreationTime))
                .Set("namespace_name", e.NamespaceName).Set("is_local", e.IsLocal ? 1 : 0);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_24 e in wmi24)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("namespace_name", e.NamespaceName).Set("client_process_id", e.ClientProcessId)
                .Set("interval_ms", e.IntervalMs).Set("query", e.Query).Set("group_operation_id", e.GroupOperationId);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_100 e in wmi100)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("component_name", e.ComponentName).Set("message_detail", e.MessageDetail).Set("file_name", e.FileName);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_101 e in wmi101)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("component_name", e.ComponentName).Set("error_id", e.ErrorId)
                .Set("message", e.ErrorDetail).Set("file_name", e.FileName);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_5857 e in wmi5857)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("provider_name", e.ProviderName).Set("code", e.Code).Set("host_process", e.HostProcess)
                .Set("host_process_id", e.ProcessID).Set("provider_path", e.ProviderPath);
            writer.Insert();
            count++;
        }

        foreach (WmiActivityEventInfo_5858 e in wmi5858)
        {
            Base(e.Timestamp, e.EventId, e.Version, e.Opcode, e.ProcessId, e.ThreadId);
            writer.Set("event5858_id", e.Id).Set("client_machine", e.ClientMachine).Set("user_name", e.User)
                .Set("client_process_id", e.ClientProcessId).Set("component", e.Component).Set("operation", e.Operation)
                .Set("result_code", e.ResultCode).Set("possible_cause", e.PossibleCause);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteAcpiEvents(
        SqliteConnection connection, SqliteTransaction transaction,
        ProcessResolver processResolver, ThreadResolver threadResolver,
        IReadOnlyList<KernelAcpiEventInfo_TemperatureNotification> temperatureNotification,
        IReadOnlyList<KernelAcpiEventInfo_AmlMethodTrace> amlMethodTrace,
        IReadOnlyList<KernelAcpiEventInfo_TemperatureChange> temperatureChange,
        IReadOnlyList<KernelAcpiEventInfo_FrequentAmlMethod> frequentAmlMethod)
    {
        TableWriter writer = new(
            connection, transaction, "acpi_events",
            "event_kind", "timestamp_utc_us", "process_id", "thread_id", "process_key", "thread_key", "processor_number",
            "thermal_zone_device_instance", "temperature_raw", "aml_method_name", "aml_method_state", "aml_elapsed_time_raw",
            "frequency", "tmp_raw", "psv_raw", "ac0_raw", "ac1_raw", "ac2_raw", "ac3_raw", "ac4_raw", "ac5_raw", "ac6_raw",
            "ac7_raw", "ac8_raw", "ac9_raw", "hot_raw", "crt_raw");

        long count = 0;
        foreach (KernelAcpiEventInfo_TemperatureNotification e in temperatureNotification)
        {
            writer.Set("event_kind", "temperature_notification")
                .Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("process_id", e.ProcessId).Set("thread_id", e.ThreadId)
                .Set("process_key", processResolver.ResolveKey(e.ProcessId, e.Timestamp))
                .Set("thread_key", threadResolver.ResolveKey(e.ThreadId, e.Timestamp))
                .Set("processor_number", null)
                .Set("thermal_zone_device_instance", e.ThermalZoneDeviceInstance)
                .Set("tmp_raw", e._TMP).Set("psv_raw", e._PSV)
                .Set("ac0_raw", e._AC0).Set("ac1_raw", e._AC1).Set("ac2_raw", e._AC2).Set("ac3_raw", e._AC3)
                .Set("ac4_raw", e._AC4).Set("ac5_raw", e._AC5).Set("ac6_raw", e._AC6).Set("ac7_raw", e._AC7)
                .Set("ac8_raw", e._AC8).Set("ac9_raw", e._AC9).Set("hot_raw", e._HOT).Set("crt_raw", e._CRT);
            writer.Insert();
            count++;
        }

        foreach (KernelAcpiEventInfo_AmlMethodTrace e in amlMethodTrace)
        {
            writer.Set("event_kind", "aml_method_trace")
                .Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("process_id", e.ProcessId).Set("thread_id", e.ThreadId)
                .Set("process_key", processResolver.ResolveKey(e.ProcessId, e.Timestamp))
                .Set("thread_key", threadResolver.ResolveKey(e.ThreadId, e.Timestamp))
                .Set("processor_number", e.ProcessorNumber)
                .Set("aml_method_name", e.AmlMethodName).Set("aml_method_state", e.AmlMethodState)
                .Set("aml_elapsed_time_raw", ToSigned(e.AmlElapsedTime))
                .Set("tmp_raw", null).Set("psv_raw", null).Set("ac0_raw", null).Set("ac1_raw", null)
                .Set("ac2_raw", null).Set("ac3_raw", null).Set("ac4_raw", null).Set("ac5_raw", null)
                .Set("ac6_raw", null).Set("ac7_raw", null).Set("ac8_raw", null).Set("ac9_raw", null)
                .Set("hot_raw", null).Set("crt_raw", null);
            writer.Insert();
            count++;
        }

        foreach (KernelAcpiEventInfo_TemperatureChange e in temperatureChange)
        {
            writer.Set("event_kind", "temperature_change")
                .Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("process_id", e.ProcessId).Set("thread_id", e.ThreadId)
                .Set("process_key", processResolver.ResolveKey(e.ProcessId, e.Timestamp))
                .Set("thread_key", threadResolver.ResolveKey(e.ThreadId, e.Timestamp))
                .Set("processor_number", null)
                .Set("thermal_zone_device_instance", e.ThermalZoneDeviceInstance)
                .Set("temperature_raw", e.Temperature)
                .Set("tmp_raw", null).Set("psv_raw", null).Set("ac0_raw", null).Set("ac1_raw", null)
                .Set("ac2_raw", null).Set("ac3_raw", null).Set("ac4_raw", null).Set("ac5_raw", null)
                .Set("ac6_raw", null).Set("ac7_raw", null).Set("ac8_raw", null).Set("ac9_raw", null)
                .Set("hot_raw", null).Set("crt_raw", null);
            writer.Insert();
            count++;
        }

        foreach (KernelAcpiEventInfo_FrequentAmlMethod e in frequentAmlMethod)
        {
            writer.Set("event_kind", "frequent_aml_method")
                .Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("process_id", e.ProcessId).Set("thread_id", e.ThreadId)
                .Set("process_key", processResolver.ResolveKey(e.ProcessId, e.Timestamp))
                .Set("thread_key", threadResolver.ResolveKey(e.ThreadId, e.Timestamp))
                .Set("processor_number", null)
                .Set("aml_method_name", e.AmlMethodName).Set("frequency", ToSigned(e.Frequency))
                .Set("tmp_raw", null).Set("psv_raw", null).Set("ac0_raw", null).Set("ac1_raw", null)
                .Set("ac2_raw", null).Set("ac3_raw", null).Set("ac4_raw", null).Set("ac5_raw", null)
                .Set("ac6_raw", null).Set("ac7_raw", null).Set("ac8_raw", null).Set("ac9_raw", null)
                .Set("hot_raw", null).Set("crt_raw", null);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteKernelPowerEvents(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<KernelPowerEventInfo_63> events, ProcessResolver processResolver, ThreadResolver threadResolver)
    {
        TableWriter writer = new(
            connection, transaction, "kernel_power_events",
            "event_id", "timestamp_utc_us", "process_id", "thread_id", "process_key", "thread_key",
            "requested_resolution_100ns", "requested_resolution_ms", "target_process_id", "app_name", "sub_process_tag", "request_ignored");

        long count = 0;
        foreach (KernelPowerEventInfo_63 e in events)
        {
            writer.Set("event_id", e.EventId)
                .Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("process_id", e.ProcessId).Set("thread_id", e.ThreadId)
                .Set("process_key", processResolver.ResolveKey(e.ProcessId, e.Timestamp))
                .Set("thread_key", threadResolver.ResolveKey(e.ThreadId, e.Timestamp))
                .Set("requested_resolution_100ns", e.RequestedResolution)
                .Set("requested_resolution_ms", e.RequestedResolution / 10_000.0)
                .Set("target_process_id", e.Pid)
                .Set("app_name", e.AppName)
                .Set("sub_process_tag", e.SubProcessTag)
                .Set("request_ignored", e.RequestIgnored ? 1 : 0);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteEnergyEstimates(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<EnergyEstimationEngineEventInfo_37> estimates, ProcessResolver processResolver, ThreadResolver threadResolver)
    {
        TableWriter writer = new(
            connection, transaction, "energy_estimates",
            "timestamp_utc_us", "process_id", "process_key", "thread_id", "thread_key", "processor_number", "app_name",
            "user_id", "interval_us", "cpu_energy_mj", "gpu_energy_mj", "display_energy_mj", "disk_energy_mj",
            "network_energy_mj", "mbb_energy_mj", "loss_energy_uj", "other_energy_mj", "emi_energy_mj", "npu_energy_mj",
            "work_on_behalf_cpu_energy_mj", "attributed_cpu_energy_mj", "record_flags", "record_measured",
            "interactivity_state", "interactivity_state_name", "is_dc_power", "is_monitor_on", "is_energy_saver",
            "is_low_power_epoch", "is_foreground", "is_container", "is_committed");

        long count = 0;
        foreach (EnergyEstimationEngineEventInfo_37 e in estimates)
        {
            writer.Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("process_id", e.ProcessId)
                .Set("process_key", processResolver.ResolveKey(e.ProcessId, e.Timestamp))
                .Set("thread_id", e.ThreadId)
                .Set("thread_key", threadResolver.ResolveKey(e.ThreadId, e.Timestamp))
                .Set("processor_number", e.ProcessorNumber)
                .Set("app_name", e.AppName)
                .Set("user_id", e.UserId)
                .Set("interval_us", (long)e.TimeInMSec * 1000)
                .Set("cpu_energy_mj", ToSigned(e.CpuEnergy))
                .Set("gpu_energy_mj", ToSigned(e.GpuEnergy))
                .Set("display_energy_mj", ToSigned(e.DisplayEnergy))
                .Set("disk_energy_mj", ToSigned(e.DiskEnergy))
                .Set("network_energy_mj", ToSigned(e.NetworkEnergy))
                .Set("mbb_energy_mj", ToSigned(e.MbbEnergy))
                .Set("loss_energy_uj", ToSigned(e.LossEnergy))
                .Set("other_energy_mj", ToSigned(e.OtherEnergy))
                .Set("emi_energy_mj", ToSigned(e.EmiEnergy))
                .Set("npu_energy_mj", ToSigned(e.NpuEnergy))
                .Set("work_on_behalf_cpu_energy_mj", ToSigned(e.WorkOnBehalfCPUEnergy))
                .Set("attributed_cpu_energy_mj", ToSigned(e.AttributedCPUEnergy))
                .Set("record_flags", e.RecordFlags)
                .Set("record_measured", e.RecordMeasured)
                .Set("interactivity_state", e.InteractivityState)
                .Set("interactivity_state_name", DescribeInteractivityState(e.InteractivityState))
                .Set("is_dc_power", (e.RecordFlags & 0x1) != 0 ? 1 : 0)
                .Set("is_monitor_on", (e.RecordFlags & 0x2) != 0 ? 1 : 0)
                .Set("is_energy_saver", (e.RecordFlags & 0x4) != 0 ? 1 : 0)
                .Set("is_low_power_epoch", (e.RecordFlags & 0x8) != 0 ? 1 : 0)
                .Set("is_foreground", (e.RecordFlags & 0x10) != 0 ? 1 : 0)
                .Set("is_container", (e.RecordFlags & 0x80) != 0 ? 1 : 0)
                .Set("is_committed", e.Committed != 0 ? 1 : 0);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static string DescribeInteractivityState(uint state) => state switch
    {
        0 => "NotUnique",
        1 => "Minimized",
        2 => "Visible",
        3 => "Focus",
        _ => $"Undefined({state})",
    };

    private static long WriteEnergyQueryStats(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<EnergyEstimationEngineEventInfo_33> events)
    {
        TableWriter writer = new(connection, transaction, "energy_query_stats", "timestamp_utc_us", "sru_work_item_type", "provider_state", "device_state");
        long count = 0;
        foreach (EnergyEstimationEngineEventInfo_33 e in events)
        {
            writer.Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("sru_work_item_type", e.SruWorkItemType)
                .Set("provider_state", e.ProviderState)
                .Set("device_state", e.DeviceState);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteEnergyCpuSamples(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<EnergyEstimationEngineEventInfo_14> events)
    {
        TableWriter writer = new(connection, transaction, "energy_cpu_samples", "timestamp_utc_us", "cpu_id", "current_frequency", "last_busy_frequency", "energy_mj");
        long count = 0;
        foreach (EnergyEstimationEngineEventInfo_14 e in events)
        {
            writer.Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("cpu_id", e.CpuId)
                .Set("current_frequency", e.CurrentFrequency)
                .Set("last_busy_frequency", e.LastBusyFrequency)
                .Set("energy_mj", ToSigned(e.Energy));
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteEnergyComponentSamples(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<EnergyEstimationEngineEventInfo_18> events)
    {
        TableWriter writer = new(
            connection, transaction, "energy_component_samples",
            "timestamp_utc_us", "component_code", "component_name", "energy_delta_mj");
        long count = 0;
        foreach (EnergyEstimationEngineEventInfo_18 e in events)
        {
            writer.Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("component_code", e.Component)
                .Set("component_name", GetEnergyComponentName(e.Component))
                .Set("energy_delta_mj", ToSigned(e.EnergyDelta));
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WriteStandbySamples(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<EnergyEstimationEngineEventInfo_35> events)
    {
        TableWriter writer = new(
            connection, transaction, "standby_samples",
            "timestamp_utc_us", "last_standby_total_us", "curr_standby_total_us", "delta_standby_total_us",
            "last_drips_total_us", "curr_drips_total_us", "delta_drips_total_us",
            "last_activation_total_us", "curr_activation_total_us", "delta_activation_total_us");
        long count = 0;
        foreach (EnergyEstimationEngineEventInfo_35 e in events)
        {
            // 原始欄位單位為 100ns tick(QPC 刻度)，除以 10 換算成微秒。
            writer.Set("timestamp_utc_us", ToUnixMicros(e.Timestamp))
                .Set("last_standby_total_us", ToSigned(e.LastStandbyTotal) / 10)
                .Set("curr_standby_total_us", ToSigned(e.CurrStandbyTotal) / 10)
                .Set("delta_standby_total_us", ToSigned(e.DeltaStandbyTotal) / 10)
                .Set("last_drips_total_us", ToSigned(e.LastDripsTotal) / 10)
                .Set("curr_drips_total_us", ToSigned(e.CurrDripsTotal) / 10)
                .Set("delta_drips_total_us", ToSigned(e.DeltaDripsTotal) / 10)
                .Set("last_activation_total_us", ToSigned(e.LastActivationTotal) / 10)
                .Set("curr_activation_total_us", ToSigned(e.CurrActivationTotal) / 10)
                .Set("delta_activation_total_us", ToSigned(e.DeltaActivationTotal) / 10);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static long WritePowerMeters(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<PowerMeterPollingEventInfo_2> rundowns,
        IReadOnlyList<PowerMeterPollingEventInfo_3> event3,
        IReadOnlyList<PowerMeterPollingEventInfo_4> event4,
        IReadOnlyList<EnergyMeasurementInterval> meterIntervals)
    {
        Dictionary<ulong, string> channelNames = [];
        Dictionary<ulong, (long First, long Last)> seenRange = [];
        foreach (PowerMeterPollingEventInfo_2 e in rundowns)
        {
            channelNames[e.MeterId] = e.ChannelName;
        }

        void Track(ulong meterId, long timestampUs)
        {
            if (seenRange.TryGetValue(meterId, out (long First, long Last) range))
            {
                seenRange[meterId] = (Math.Min(range.First, timestampUs), Math.Max(range.Last, timestampUs));
            }
            else
            {
                seenRange[meterId] = (timestampUs, timestampUs);
            }
        }

        TableWriter sampleWriter = new(
            connection, transaction, "power_meter_samples",
            "meter_id", "event_id", "timestamp_utc_us", "absolute_energy", "absolute_time", "value");

        long count = 0;
        foreach (PowerMeterPollingEventInfo_3 e in event3)
        {
            long timestampUs = ToUnixMicros(e.Timestamp);
            Track(e.MeterId, timestampUs);
            sampleWriter.Set("meter_id", ToSigned(e.MeterId)).Set("event_id", e.EventId)
                .Set("timestamp_utc_us", timestampUs).Set("absolute_energy", null).Set("absolute_time", null)
                .Set("value", ToSigned(e.Value));
            sampleWriter.Insert();
            count++;
        }

        foreach (PowerMeterPollingEventInfo_4 e in event4)
        {
            long timestampUs = ToUnixMicros(e.Timestamp);
            Track(e.MeterId, timestampUs);
            sampleWriter.Set("meter_id", ToSigned(e.MeterId)).Set("event_id", e.EventId)
                .Set("timestamp_utc_us", timestampUs).Set("absolute_energy", ToSigned(e.AbsoluteEnergy))
                .Set("absolute_time", ToSigned(e.AbsoluteTime)).Set("value", null);
            sampleWriter.Insert();
            count++;
        }

        TableWriter meterWriter = new(
            connection, transaction, "power_meters",
            "meter_id", "channel_name", "default_sampling_period_us", "first_seen_utc_us", "last_seen_utc_us");
        Dictionary<ulong, ulong> samplingPeriods = [];
        foreach (PowerMeterPollingEventInfo_2 e in rundowns)
        {
            samplingPeriods[e.MeterId] = e.DefaultSamplingPeriodInMs;
        }

        HashSet<ulong> meterIds = [.. channelNames.Keys, .. seenRange.Keys];
        foreach (ulong meterId in meterIds)
        {
            bool hasSamples = seenRange.TryGetValue(meterId, out (long First, long Last) range);
            meterWriter.Set("meter_id", ToSigned(meterId))
                .Set("channel_name", channelNames.TryGetValue(meterId, out string? name) ? name : $"0x{meterId:X}")
                .Set("default_sampling_period_us", samplingPeriods.TryGetValue(meterId, out ulong periodMs) ? (long)periodMs * 1000 : null)
                .Set("first_seen_utc_us", hasSamples ? range.First : null)
                .Set("last_seen_utc_us", hasSamples ? range.Last : null);
            meterWriter.Insert();
            count++;
        }

        return count;
    }

    private static long WriteDiskIoOperations(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<BufferedDiskIoOperation> operations, ProcessResolver processResolver, ThreadResolver threadResolver)
    {
        TableWriter writer = new(
            connection, transaction, "disk_io_operations",
            "event_id", "version", "opcode", "timestamp_utc_us", "started_at_utc_us", "completed_at_utc_us", "duration_us",
            "process_id", "process_key", "thread_id", "thread_key", "correlation_id", "transfer_size_bytes", "operation", "match_status");

        long count = 0;
        foreach (BufferedDiskIoOperation op in operations)
        {
            long? startUs = ToUnixMicros(op.StartedAt);
            long? endUs = ToUnixMicros(op.CompletedAt);
            writer.Set("event_id", op.EventId).Set("version", op.Version).Set("opcode", op.Opcode)
                .Set("timestamp_utc_us", ToUnixMicros(op.Timestamp))
                .Set("started_at_utc_us", startUs)
                .Set("completed_at_utc_us", endUs)
                .Set("duration_us", startUs is long s && endUs is long e ? e - s : null)
                .Set("process_id", op.ProcessId)
                .Set("process_key", processResolver.ResolveKey(op.ProcessId, op.Timestamp))
                .Set("thread_id", op.ThreadId)
                .Set("thread_key", threadResolver.ResolveKey(op.ThreadId, op.Timestamp))
                .Set("correlation_id", op.CorrelationId)
                .Set("transfer_size_bytes", ToSigned(op.TransferSize))
                .Set("operation", op.Operation)
                .Set("match_status", op.MatchStatus);
            writer.Insert();
            count++;
        }

        return count;
    }

    private static void WriteSchemaMetadata(SqliteConnection connection, SqliteTransaction transaction)
    {
        TableWriter catalogWriter = new(
            connection, transaction, "schema_catalog",
            "object_name", "object_type", "description", "preferred_for_ai");
        (string Name, string Type, string Description, bool PreferredForAi)[] catalog =
        [
            ("trace_info", "table", "整份 ETL 匯入的中繼資料，單一列：擷取時間範圍、CPU/記憶體資訊、遺失事件數。", true),
            ("processes", "table", "程序生命週期，一列代表一次 process_id 的存活區間(已處理 PID 重用)；parent_process_key 指向已解析的正確父程序。", true),
            ("threads", "table", "執行緒生命週期(已處理 TID 重用)，process_key 指向所屬程序。", false),
            ("images", "table", "程序載入的模組(DLL/驅動)生命週期，process_key 指向所屬程序。", false),
            ("process_memory_samples", "table", "程序記憶體計數器抽樣(工作集/私有位元組/分頁錯誤等)。", true),
            ("thread_cpu_buckets", "table", "以固定時間窗彙總的執行緒 CPU 使用量，取代逐筆 CSwitch 事件。", true),
            ("thread_wait_reason_buckets", "table", "thread_cpu_buckets 底下依等待原因(wait_reason)分組的次數，子表取代序列化 JSON。", false),
            ("processor_cpu_buckets", "table", "以固定時間窗彙總的每顆 CPU 使用量(忙碌/閒置時間、切換次數)。", true),
            ("dpc_events", "table", "DPC(Deferred Procedure Call)執行區間，含處理常式位址與所屬模組。", true),
            ("isr_events", "table", "ISR(中斷服務常式)執行區間，含處理常式位址與所屬模組。", true),
            ("profile_samples", "table", "取樣型 CPU Profile(PerfInfoProfile)，每筆代表某處理器某時間點的指令暫存器位置。", false),
            ("services", "table", "Windows 服務彙總(目前狀態/啟動類型/所屬服務群組)。", true),
            ("service_state_changes", "table", "服務狀態變化明細，process_key/service_management_thread_key 為當下解析出的程序/執行緒。", true),
            ("service_main_intervals", "table", "服務 ServiceMain() 呼叫區間(啟動/停止流程實際執行時間)。", false),
            ("service_group_intervals", "table", "服務群組(svchost -k 分組)執行緒佔用區間。", false),
            ("service_snapshots", "table", "追蹤開始當下(rundown)SCM 服務快照。", false),
            ("memory_snapshots", "table", "系統記憶體用量快照(Kernel-Memory MemInfo)，分頁計數欄位單位皆為分頁數，乘上 page_size_bytes 即位元組。", true),
            ("memory_priority_pages", "table", "memory_snapshots 底下依優先權(0-7)分組的 standby/repurposed 分頁數，子表取代序列化 JSON。", false),
            ("wmi_events", "table", "WMI 活動提供者統一事件表，涵蓋 ID 11/12/13/16/17/20/22/24/100/101/5857/5858，未用到的欄位為 NULL。", true),
            ("energy_estimates", "table", "Energy Estimation Engine Event 37，每個應用程式/硬體遙測項目每個估算週期的能耗分量(單位 mJ，除 loss_energy_uj 為 μJ)。", true),
            ("energy_query_stats", "table", "Energy Estimation Engine Event 33，估算查詢週期觸發原因與裝置狀態快照。", false),
            ("energy_cpu_samples", "table", "Energy Estimation Engine Event 14，每顆邏輯 CPU 查詢間隔的原始能耗輸入。", false),
            ("energy_component_samples", "table", "Energy Estimation Engine Event 18，個別元件代碼及能耗差值。", false),
            ("standby_samples", "table", "Energy Estimation Engine Event 35，Modern Standby 週期的深度閒置/喚醒佔比原始計數。", false),
            ("power_meters", "table", "硬體電表(rail)中繼資料：MeterId、通道名稱、預設輪詢週期、觀察到的時間範圍。", false),
            ("power_meter_samples", "table", "電表 Event 3/4 原始讀數(absolute_energy/value 為硬體位元組原始值，未做差值換算)。", false),
            ("acpi_events", "table", "ACPI 溫度通知/AML Trace/溫度變化/常見 AML 方法統一事件表，event_kind 區分來源事件。", true),
            ("kernel_power_events", "table", "Kernel Power Event 63：電源請求解析度變更/是否忽略請求。", false),
            ("disk_io_operations", "table", "磁碟 IO 完成操作(DiskIoOperationCompleted)，含配對後的耗時。", true),
            ("schema_catalog", "table", "資料表與 AI View 用途說明中繼資料(本表)。", true),
            ("schema_columns", "table", "所有資料表與 AI View 的欄位型別、單位、可空性與欄位說明。", true),
            ("schema_relationships", "table", "資料表之間外鍵關係中繼資料，供分析工具快速理解 JOIN 路徑。", true),
            ("ai_process_overview", "view", "程序摘要：生命週期、父程序、映像名稱與命令列。", true),
            ("ai_process_memory_latest", "view", "每個程序最新一筆記憶體樣本。", true),
            ("ai_cpu_by_process", "view", "依程序彙總執行緒 CPU 時間桶與切換次數。", true),
            ("ai_interrupt_hotspots", "view", "合併 DPC 與 ISR 的中斷處理熱點資料。", true),
            ("ai_service_lifecycle", "view", "服務狀態轉換時間線，附帶服務程序關聯。", true),
            ("ai_wmi_operations", "view", "WMI 查詢、非同步通知、方法及輪詢操作，已配對完成事件與 provider。", true),
            ("ai_energy_by_app", "view", "依應用程式彙總所有能耗分量，loss 已由 μJ 換算為 mJ。", true),
            ("ai_thermal_trend", "view", "ACPI 溫度變化時間線。", true),
            ("ai_memory_timeline", "view", "系統記憶體壓力時間線，將分頁數換算成 bytes。", true),
        ];
        foreach ((string name, string type, string description, bool preferredForAi) in catalog)
        {
            catalogWriter.Set("object_name", name).Set("object_type", type).Set("description", description)
                .Set("preferred_for_ai", preferredForAi);
            catalogWriter.Insert();
        }

        TableWriter relWriter = new(
            connection, transaction, "schema_relationships",
            "from_table", "from_column", "to_table", "to_column", "description");
        (string FromTable, string FromColumn, string ToTable, string ToColumn, string Description)[] relationships =
        [
            ("processes", "parent_process_key", "processes", "process_key", "父程序(已解析 PID 重用)"),
            ("threads", "process_key", "processes", "process_key", "執行緒所屬程序"),
            ("images", "process_key", "processes", "process_key", "模組所屬程序"),
            ("process_memory_samples", "process_key", "processes", "process_key", "記憶體樣本所屬程序"),
            ("thread_cpu_buckets", "thread_key", "threads", "thread_key", "CPU 用量桶所屬執行緒"),
            ("thread_cpu_buckets", "process_key", "processes", "process_key", "CPU 用量桶所屬程序"),
            ("thread_wait_reason_buckets", "thread_cpu_bucket_id", "thread_cpu_buckets", "id", "等待原因子表所屬 CPU 用量桶"),
            ("memory_priority_pages", "memory_snapshot_id", "memory_snapshots", "id", "優先權分頁子表所屬記憶體快照"),
            ("service_state_changes", "service_key", "services", "service_key", "狀態變化所屬服務"),
            ("service_state_changes", "process_key", "processes", "process_key", "狀態變化發生時的服務程序"),
            ("service_state_changes", "service_management_thread_key", "threads", "thread_key", "執行服務管理工作的執行緒"),
            ("service_main_intervals", "service_key", "services", "service_key", "ServiceMain 區間所屬服務"),
            ("wmi_events", "process_key", "processes", "process_key", "事件發生當下解析出的行程"),
            ("wmi_events", "thread_key", "threads", "thread_key", "事件發生當下解析出的執行緒"),
            ("energy_estimates", "process_key", "processes", "process_key", "能耗估算解析出的行程"),
            ("energy_estimates", "thread_key", "threads", "thread_key", "能耗估算解析出的執行緒"),
            ("power_meter_samples", "meter_id", "power_meters", "meter_id", "電表讀數所屬電表"),
            ("disk_io_operations", "process_key", "processes", "process_key", "磁碟 IO 所屬行程"),
            ("disk_io_operations", "thread_key", "threads", "thread_key", "磁碟 IO 所屬執行緒"),
        ];
        foreach ((string fromTable, string fromColumn, string toTable, string toColumn, string description) in relationships)
        {
            relWriter.Set("from_table", fromTable).Set("from_column", fromColumn)
                .Set("to_table", toTable).Set("to_column", toColumn).Set("description", description);
            relWriter.Insert();
        }

        List<(string ObjectName, string ColumnName, string DeclaredType, string? Unit, bool IsNullable, bool IsPrimaryKey)> columns = [];
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = """
                SELECT name, type, "notnull", pk
                FROM pragma_table_info($object_name)
                ORDER BY cid;
                """;
            SqliteParameter objectName = pragma.Parameters.Add("$object_name", SqliteType.Text);
            foreach ((string name, _, _, _) in catalog)
            {
                objectName.Value = name;
                using SqliteDataReader reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    string columnName = reader.GetString(0);
                    bool isPrimaryKey = reader.GetInt64(3) != 0;
                    columns.Add((
                        name,
                        columnName,
                        reader.GetString(1),
                        GetColumnUnit(columnName),
                        reader.GetInt64(2) == 0 && !isPrimaryKey,
                        isPrimaryKey));
                }
            }
        }

        TableWriter columnWriter = new(
            connection, transaction, "schema_columns",
            "object_name", "column_name", "declared_type", "unit", "is_nullable", "is_primary_key", "description");
        foreach ((string objectName, string columnName, string declaredType, string? unit, bool isNullable, bool isPrimaryKey) in columns)
        {
            columnWriter.Set("object_name", objectName).Set("column_name", columnName)
                .Set("declared_type", declaredType).Set("unit", unit)
                .Set("is_nullable", isNullable).Set("is_primary_key", isPrimaryKey)
                .Set("description", GetColumnDescription(columnName));
            columnWriter.Insert();
        }
    }

    private static string? GetColumnUnit(string columnName)
    {
        if (columnName.EndsWith("_utc_us", StringComparison.Ordinal) ||
            columnName.EndsWith("_duration_us", StringComparison.Ordinal) ||
            columnName.EndsWith("_interval_us", StringComparison.Ordinal) ||
            columnName.EndsWith("_period_us", StringComparison.Ordinal) ||
            columnName.EndsWith("_time_us", StringComparison.Ordinal) ||
            columnName.EndsWith("_total_us", StringComparison.Ordinal))
        {
            return "microsecond";
        }

        if (columnName.EndsWith("_bytes", StringComparison.Ordinal))
        {
            return "byte";
        }

        if (columnName.EndsWith("_mj", StringComparison.Ordinal))
        {
            return "millijoule";
        }

        if (columnName.EndsWith("_uj", StringComparison.Ordinal))
        {
            return "microjoule";
        }

        if (columnName.EndsWith("_hz", StringComparison.Ordinal))
        {
            return "hertz";
        }

        if (columnName.EndsWith("_percent", StringComparison.Ordinal))
        {
            return "percent";
        }

        if (columnName.EndsWith("_100ns", StringComparison.Ordinal))
        {
            return "100-nanosecond interval";
        }

        if (columnName.EndsWith("_ms", StringComparison.Ordinal))
        {
            return "millisecond";
        }

        if (columnName.EndsWith("_filetime", StringComparison.Ordinal))
        {
            return "100-nanosecond interval since 1601-01-01 UTC";
        }

        return null;
    }

    private static string GetColumnDescription(string columnName) => columnName switch
    {
        "process_key" => "程序生命週期代理鍵；JOIN processes.process_key，不可用 process_id 取代。",
        "thread_key" => "執行緒生命週期代理鍵；JOIN threads.thread_key，不可用 thread_id 取代。",
        "parent_process_key" => "已依時間解析 PID 重用後的父程序代理鍵。",
        "service_key" => "服務代理鍵；JOIN services.service_key。",
        "meter_id" => "電表識別碼；JOIN power_meters.meter_id。",
        "timestamp_utc_us" => "UTC Unix epoch 微秒時間戳。",
        "started_at_utc_us" => "區間起點，UTC Unix epoch 微秒。",
        "ended_at_utc_us" or "stopped_at_utc_us" or "completed_at_utc_us" => "區間終點，UTC Unix epoch 微秒；NULL 表示未知或尚未結束。",
        "client_process_creation_time_filetime" => "WMI 原始 ClientProcessCreationTime FILETIME 值。",
        "client_process_creation_time_utc_us" => "由 WMI FILETIME 轉換的 UTC Unix epoch 微秒時間戳。",
        "aml_elapsed_time_raw" => "ACPI AmlElapsedTime 原始 UInt64；官方 manifest 未定義單位，不進行推測性換算。",
        "requested_resolution_100ns" => "Windows 計時器解析度請求原始值，每單位 100ns。",
        "requested_resolution_ms" => "Windows 計時器解析度請求換算為毫秒。",
        "component_code" => "Energy Estimation Engine 元件代碼：0=states、1=other、2=cpu、3=soc、4=display、5=disk、6=network。",
        "component_name" => "由 component_code 解碼的穩定英文元件名稱。",
        _ when columnName.EndsWith("_utc_us", StringComparison.Ordinal) => "UTC Unix epoch 微秒時間戳。",
        _ when columnName.EndsWith("_us", StringComparison.Ordinal) => "微秒。",
        _ when columnName.EndsWith("_bytes", StringComparison.Ordinal) => "位元組。",
        _ when columnName.EndsWith("_mj", StringComparison.Ordinal) => "毫焦耳。",
        _ when columnName.EndsWith("_uj", StringComparison.Ordinal) => "微焦耳。",
        _ when columnName.StartsWith("is_", StringComparison.Ordinal) => "布林值：0=false，1=true。",
        _ => columnName.Replace('_', ' '),
    };
}
