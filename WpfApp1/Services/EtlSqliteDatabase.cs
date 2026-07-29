using System.IO;
using System.Globalization;
using System.Data;
using Microsoft.Data.Sqlite;
using WpfApp1.Models;

namespace WpfApp1.Services;

internal interface IEtlSqliteDatabase
{
    //IReadOnlyList<EtlTableDefinition> GetBrowsableTables();
    EtlTablePage ReadTablePage(string databasePath, string tableName, int pageNumber, int pageSize);
    void Initialize(SqliteConnection connection);
    long CreateTrace(SqliteConnection connection, SqliteTransaction transaction, string etlPath);
    //void ReadAnalysis(string databasePath);
    string GetDatabasePath(string etlPath);
    SqliteConnection Open(string etlPath);
}

internal sealed class EtlSqliteDatabase : IEtlSqliteDatabase
{
    private static readonly IReadOnlyList<EtlTableDefinition> BrowsableTables =
    [
        new("Traces", "追蹤記錄"),
        new("ProcessLifetimes", "處理程序生命週期"),
        new("ThreadLifetimes", "執行緒生命週期"),
        new("ImageModules", "映像模組"),
        new("ContextSwitchEvents", "內容切換事件"),
        new("DpcEvents", "DPC 事件"),
        new("InterruptEvents", "中斷事件"),
        new("ProfileEvents", "Profile 事件"),
        new("DiskIoEvents", "磁碟 I/O 事件"),
        new("FileIoEvents", "檔案 I/O 事件"),
        new("WmiActivityEvents", "WMI 活動事件"),
        new("EnergyEstimationEvents", "能源估算事件"),
        new("KernelAcpiEvents", "Kernel ACPI 事件"),
        new("KernelPowerEvents", "Kernel 電源事件"),
        new("PowerMeterPollingEvents", "電錶輪詢事件"),
    ];

    //public IReadOnlyList<EtlTableDefinition> GetBrowsableTables() => BrowsableTables;

    public EtlTablePage ReadTablePage(string databasePath, string tableName, int pageNumber, int pageSize)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("找不到 ETL 對應的 SQLite 資料庫檔案。", databasePath);
        }

        EtlTableDefinition table = BrowsableTables.FirstOrDefault(table => table.Name == tableName)
            ?? throw new ArgumentException("不支援瀏覽指定的資料表。", nameof(tableName));

        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        long totalRowCount;
        using (SqliteCommand countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(table.Name)}";
            totalRowCount = (long)(countCommand.ExecuteScalar() ?? 0L);
        }

        int totalPages = Math.Max(1, checked((int)Math.Ceiling(totalRowCount / (double)pageSize)));
        pageNumber = Math.Min(pageNumber, totalPages);
        long offset = checked((long)(pageNumber - 1) * pageSize);

        using SqliteCommand pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"SELECT * FROM {QuoteIdentifier(table.Name)} LIMIT $pageSize OFFSET $offset";
        pageCommand.Parameters.AddWithValue("$pageSize", pageSize);
        pageCommand.Parameters.AddWithValue("$offset", offset);

        using SqliteDataReader reader = pageCommand.ExecuteReader();
        var rows = new DataTable(table.Name);
        rows.Load(reader);
        return new EtlTablePage(rows.DefaultView, totalRowCount, pageNumber, totalPages);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    public void Initialize(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = OFF;
            PRAGMA synchronous = OFF;
            PRAGMA locking_mode = EXCLUSIVE;
            PRAGMA temp_store = MEMORY;

            CREATE TABLE IF NOT EXISTS Traces (
                TraceId INTEGER PRIMARY KEY,
                SourceEtlPath TEXT NOT NULL,
                ImportedAtUtc TEXT NOT NULL,
                TraceStartUtc TEXT NULL,
                TraceEndUtc TEXT NULL,
                ProcessorCount INTEGER NULL,
                BuffersLost INTEGER NULL,
                EventsLost INTEGER NULL,
                ImportStatus TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ProcessLifetimes (
                ProcessLifetimeId INTEGER PRIMARY KEY,
                TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                ProcessId INTEGER NOT NULL,
                StartUtc TEXT NOT NULL,
                EndUtc TEXT NULL,
                ImageFileName TEXT NULL,
                ParentProcessId INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS ThreadLifetimes (
                ThreadLifetimeId INTEGER PRIMARY KEY,
                TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                ProcessId INTEGER NOT NULL,
                ThreadId INTEGER NOT NULL,
                StartUtc TEXT NOT NULL,
                EndUtc TEXT NULL,
                StackBase TEXT NULL,
                StackLimit TEXT NULL,
                Win32StartAddress TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS ImageModules (
                ImageModuleId INTEGER PRIMARY KEY,
                TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                ProcessId INTEGER NOT NULL,
                LoadUtc TEXT NOT NULL,
                UnloadUtc TEXT NULL,
                ImageBase TEXT NULL,
                ImageSize TEXT NULL,
                FileName TEXT NULL,
                CheckSum INTEGER NULL,
                TimeDateStamp INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ProcessLifetimes_Trace_Process_Time
                ON ProcessLifetimes (TraceId, ProcessId, StartUtc, EndUtc);
            CREATE INDEX IF NOT EXISTS IX_ThreadLifetimes_Trace_Thread_Time
                ON ThreadLifetimes (TraceId, ThreadId, StartUtc, EndUtc);
            CREATE INDEX IF NOT EXISTS IX_ImageModules_Trace_Process_Address_Time
                ON ImageModules (TraceId, ProcessId, ImageBase, LoadUtc, UnloadUtc);

            CREATE TABLE IF NOT EXISTS ContextSwitchEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessorNumber INTEGER NOT NULL, NewThreadId INTEGER NOT NULL, OldThreadId INTEGER NOT NULL,
                NewProcessId INTEGER NULL, OldProcessId INTEGER NULL, NewThreadPriority INTEGER NOT NULL, OldThreadPriority INTEGER NOT NULL,
                PreviousCState INTEGER NOT NULL, OldThreadWaitReason INTEGER NOT NULL, OldThreadWaitMode INTEGER NOT NULL,
                OldThreadState INTEGER NOT NULL, OldThreadWaitIdealProcessor INTEGER NOT NULL, NewThreadWaitTime INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS DpcEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessorNumber INTEGER NOT NULL, EtwEventId INTEGER NOT NULL, Version INTEGER NOT NULL,
                Opcode INTEGER NOT NULL, InitialTime TEXT NULL, Routine TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS InterruptEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessorNumber INTEGER NOT NULL, EtwEventId INTEGER NOT NULL, Version INTEGER NOT NULL,
                Opcode INTEGER NOT NULL, InitialTime TEXT NULL, Routine TEXT NULL, ReturnValue INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS ProfileEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessorNumber INTEGER NOT NULL, EtwEventId INTEGER NOT NULL, Version INTEGER NOT NULL,
                Opcode INTEGER NOT NULL, InstructionPointer TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS DiskIoEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, IsInitiation INTEGER NOT NULL, ProcessId INTEGER NULL, ThreadId INTEGER NULL,
                Irp TEXT NULL, TransferSize INTEGER NULL, ByteOffset INTEGER NULL, FileObject TEXT NULL, Opcode INTEGER NULL, PropertiesJson TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS FileIoEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessId INTEGER NULL, ThreadId INTEGER NULL, EtwEventId INTEGER NOT NULL,
                Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, PropertiesJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS WmiActivityEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessId INTEGER NOT NULL, ThreadId INTEGER NOT NULL, EtwEventId INTEGER NOT NULL,
                Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, PropertiesJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS EnergyEstimationEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessId INTEGER NULL, ThreadId INTEGER NULL, EtwEventId INTEGER NOT NULL,
                Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, PropertiesJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS KernelAcpiEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessId INTEGER NOT NULL, ThreadId INTEGER NOT NULL, EtwEventId INTEGER NOT NULL,
                Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, PropertiesJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS KernelPowerEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessId INTEGER NOT NULL, ThreadId INTEGER NOT NULL, EtwEventId INTEGER NOT NULL,
                Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, PropertiesJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS PowerMeterPollingEvents (
                EventId INTEGER PRIMARY KEY, TraceId INTEGER NOT NULL REFERENCES Traces(TraceId) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL, ProcessId INTEGER NOT NULL, ThreadId INTEGER NOT NULL, EtwEventId INTEGER NOT NULL,
                Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, PropertiesJson TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ContextSwitchEvents_Trace_Time ON ContextSwitchEvents (TraceId, TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_ContextSwitchEvents_Trace_Process_Time ON ContextSwitchEvents (TraceId, NewProcessId, TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_DpcEvents_Trace_Routine_Time ON DpcEvents (TraceId, Routine, TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_InterruptEvents_Trace_Routine_Time ON InterruptEvents (TraceId, Routine, TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_ProfileEvents_Trace_Address_Time ON ProfileEvents (TraceId, InstructionPointer, TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_DiskIoEvents_Trace_Irp_Time ON DiskIoEvents (TraceId, Irp, TimestampUtc);

            CREATE VIEW IF NOT EXISTS CpuExecutionIntervals AS
            SELECT
                TraceId,
                ProcessorNumber,
                NewProcessId AS ProcessId,
                NewThreadId AS ThreadId,
                TimestampUtc AS StartUtc,
                LEAD(TimestampUtc) OVER (PARTITION BY TraceId, ProcessorNumber ORDER BY TimestampUtc) AS EndUtc
            FROM ContextSwitchEvents
            WHERE NewProcessId IS NOT NULL;

            CREATE VIEW IF NOT EXISTS ProcessCpuAnalysis AS
            SELECT
                c.TraceId,
                c.NewProcessId AS ProcessId,
                COUNT(*) AS ContextSwitchCount,
                COALESCE(SUM((julianday(i.EndUtc) - julianday(i.StartUtc)) * 86400000.0), 0.0) AS TotalCpuTimeMs
            FROM ContextSwitchEvents c
            LEFT JOIN CpuExecutionIntervals i
                ON i.TraceId = c.TraceId
                AND i.ProcessorNumber = c.ProcessorNumber
                AND i.StartUtc = c.TimestampUtc
                AND i.ProcessId = c.NewProcessId
            WHERE c.NewProcessId IS NOT NULL
            GROUP BY c.TraceId, c.NewProcessId;
            """;
        command.ExecuteNonQuery();
    }

    public long CreateTrace(SqliteConnection connection, SqliteTransaction transaction, string etlPath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Traces (SourceEtlPath, ImportedAtUtc, ImportStatus)
            VALUES ($sourceEtlPath, $importedAtUtc, 'Importing');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$sourceEtlPath", Path.GetFullPath(etlPath));
        command.Parameters.AddWithValue("$importedAtUtc", DateTime.UtcNow.ToString("O"));
        return (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("無法建立 ETL trace 記錄。"));
    }

    //public AnalysisResult ReadAnalysis(string databasePath)
    //{
    //    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
    //    connection.Open();

    //    long traceId;
    //    DateTime? traceStart;
    //    DateTime? traceEnd;
    //    using (SqliteCommand command = connection.CreateCommand())
    //    {
    //        command.CommandText = "SELECT TraceId, TraceStartUtc, TraceEndUtc FROM Traces WHERE ImportStatus = 'Completed' ORDER BY TraceId DESC LIMIT 1";
    //        using SqliteDataReader reader = command.ExecuteReader();
    //        if (!reader.Read())
    //        {
    //            return new AnalysisResult();
    //        }

    //        traceId = reader.GetInt64(0);
    //        traceStart = ReadTimestamp(reader, 1);
    //        traceEnd = ReadTimestamp(reader, 2);
    //    }

    //    var result = new AnalysisResult();
    //    double durationMs = traceStart is DateTime start && traceEnd is DateTime end && end > start ? (end - start).TotalMilliseconds : 0;

    //    using (SqliteCommand command = connection.CreateCommand())
    //    {
    //        command.CommandText = "SELECT ProcessId, ContextSwitchCount, TotalCpuTimeMs FROM ProcessCpuAnalysis WHERE TraceId = $traceId ORDER BY TotalCpuTimeMs DESC";
    //        command.Parameters.AddWithValue("$traceId", traceId);
    //        using SqliteDataReader reader = command.ExecuteReader();
    //        while (reader.Read())
    //        {
    //            double totalCpuTimeMs = reader.GetDouble(2);
    //            result.ProcessCpuSummaries.Add(new ProcessCpuSummary
    //            {
    //                ProcessId = checked((uint)reader.GetInt64(0)),
    //                ContextSwitchCount = reader.GetInt32(1),
    //                TotalCpuTimeMs = totalCpuTimeMs,
    //                AverageCpuPercent = durationMs > 0 ? Math.Min(100, totalCpuTimeMs / durationMs * 100) : 0,
    //            });
    //        }
    //    }

    //    return result;
    //}

    private static DateTime? ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public string GetDatabasePath(string etlPath)
    {
        return Path.ChangeExtension(etlPath, ".db");
    }

    public SqliteConnection Open(string etlPath)
    {
        string databasePath = GetDatabasePath(etlPath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = false,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());

        connection.Open();
        return connection;
    }
}

public sealed record EtlTableDefinition(string Name, string DisplayName);

public sealed record EtlTablePage(DataView Rows, long TotalRowCount, int PageNumber, int TotalPages);
