using Microsoft.Data.Sqlite;
using QSoft.ETW;
using System;
using System.Globalization;
using System.IO;
using WpfApp1.Analysis;

namespace WpfApp1
{
    internal sealed class DataBase_SQLite : IDisposable
    {
        private const int WriteBatchSize = 10_000;

        private SqliteConnection? _connection;
        private SqliteTransaction? _transaction;
        private int _batchedWriteCount;
        private SqliteCommand? _writeImageLoadCommand;
        private SqliteCommand? _writeImageUnloadCommand;
        private SqliteCommand? _writeProcessStartCommand;
        private SqliteCommand? _writeProcessStopCommand;
        private SqliteCommand? _writeWmiActivityCommand;
        private SqliteCommand? _writeEnergyEstimationEngineCommand;
        private SqliteCommand? _writeEnergyEstimationQueryStatsCommand;
        private SqliteCommand? _writeEnergyEstimationCpuPowerCommand;
        private SqliteCommand? _writeEnergyEstimationEnergyDeltaCommand;
        private SqliteCommand? _writeKernelAcpiCommand;
        private SqliteCommand? _writeKernelPowerCommand;
        private SqliteCommand? _writeThreadEventCommand;
        private SqliteCommand? _writeCpuProfileSampleCommand;
        private SqliteCommand? _writeDpcCommand;
        private SqliteCommand? _writeInterruptCommand;
        private SqliteCommand? _writeThreadLifetimeCommand;

        public void Open(string filename)
        {
            if (_connection is not null)
            {
                throw new InvalidOperationException("SQLite 資料庫已開啟。");
            }

            if (string.IsNullOrWhiteSpace(filename))
            {
                throw new ArgumentException("資料庫檔案路徑不可為空白。", nameof(filename));
            }

            string databasePath = Path.GetFullPath(filename);
            string? directoryPath = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            connection.Open();

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    @"PRAGMA journal_mode = WAL;
                      PRAGMA synchronous = OFF;
                      PRAGMA foreign_keys = ON;

                      CREATE TABLE IF NOT EXISTS ImageLoads
                      (
                          ImageLoadId INTEGER PRIMARY KEY,
                          ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                          ProcessId INTEGER NOT NULL,
                          ImageBase TEXT NULL,
                          ImageSize TEXT NULL,
                          ImageCheckSum INTEGER NULL,
                          TimeDateStamp INTEGER NULL,
                          DefaultBase TEXT NULL,
                          FileName TEXT NOT NULL,
                          LoadedAtUtc TEXT NOT NULL,
                          UnloadedAtUtc TEXT NULL
                      );

                      CREATE INDEX IF NOT EXISTS IX_ImageLoads_ActiveImage
                      ON ImageLoads (ProcessId, ImageBase, UnloadedAtUtc, LoadedAtUtc DESC);

                      CREATE INDEX IF NOT EXISTS IX_ImageLoads_ProcessRecord
                      ON ImageLoads (ProcessRecordId, LoadedAtUtc);

                      CREATE TABLE IF NOT EXISTS Processes
                      (
                          ProcessRecordId INTEGER PRIMARY KEY,
                          ProcessId INTEGER NOT NULL,
                          ParentProcessId INTEGER NOT NULL,
                          ImageFileName TEXT NOT NULL,
                          CommandLine TEXT NOT NULL,
                          StartedAtUtc TEXT NOT NULL,
                          EndedAtUtc TEXT NULL,
                          CpuDurationTicks INTEGER NULL,
                          CpuUsagePercent REAL NULL,
                          UNIQUE (ProcessId, StartedAtUtc)
                      );

                      CREATE INDEX IF NOT EXISTS IX_Processes_ActiveProcess
                       ON Processes (ProcessId, EndedAtUtc, StartedAtUtc DESC);

                       CREATE TABLE IF NOT EXISTS WmiActivityEvents
                       (
                           WmiActivityEventId INTEGER PRIMARY KEY,
                           ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           Operation TEXT NOT NULL,
                           NamespaceName TEXT NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_OperationTimestamp
                       ON WmiActivityEvents (Operation, TimestampUtc);

                       CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_NamespaceTimestamp
                        ON WmiActivityEvents (NamespaceName, TimestampUtc);

                       CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_ProcessRecord
                       ON WmiActivityEvents (ProcessRecordId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS EnergyEstimationEngineEvents
                       (
                           EnergyEstimationEngineEventId INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           AppName TEXT NOT NULL,
                           UserId INTEGER NOT NULL,
                           CpuEnergy INTEGER NOT NULL,
                           GpuEnergy INTEGER NOT NULL,
                           DisplayEnergy INTEGER NOT NULL,
                           DiskEnergy INTEGER NOT NULL,
                           NetworkEnergy INTEGER NOT NULL,
                           MbbEnergy INTEGER NOT NULL,
                           LossEnergy INTEGER NOT NULL,
                           OtherEnergy INTEGER NOT NULL,
                           EmiEnergy INTEGER NOT NULL,
                           TimeInMSec INTEGER NOT NULL,
                           NpuEnergy INTEGER NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_EnergyEstimationEngineEvents_ProcessTimestamp
                        ON EnergyEstimationEngineEvents (ProcessId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS EnergyEstimationEngine_33
                       (
                           EnergyEstimationEngine_33Id INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           SruWorkItemType INTEGER NOT NULL,
                           ProviderState INTEGER NOT NULL,
                           DeviceState INTEGER NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_EnergyEstimationEngine_33_Timestamp
                       ON EnergyEstimationEngine_33 (TimestampUtc);

                       CREATE TABLE IF NOT EXISTS EnergyEstimationEngine_18
                       (
                           EnergyEstimationEngine_18Id INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           Component INTEGER NOT NULL,
                           EnergyDelta TEXT NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_EnergyEstimationEngine_18_Timestamp
                       ON EnergyEstimationEngine_18 (TimestampUtc);

                       CREATE TABLE IF NOT EXISTS EnergyEstimationEngine_14
                       (
                           EnergyEstimationEngine_14Id INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           CpuId INTEGER NOT NULL,
                           CurrentFrequency INTEGER NOT NULL,
                           LastBusyFrequency INTEGER NOT NULL,
                           Energy TEXT NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_EnergyEstimationEngine_14_Timestamp
                       ON EnergyEstimationEngine_14 (TimestampUtc);

                       CREATE TABLE IF NOT EXISTS KernelAcpiEvents
                       (
                           KernelAcpiEventId INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_KernelAcpiEvents_ProcessTimestamp
                       ON KernelAcpiEvents (ProcessId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS KernelPowerEvents
                       (
                           KernelPowerEventId INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_KernelPowerEvents_ProcessTimestamp
                       ON KernelPowerEvents (ProcessId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS ThreadEvents
                       (
                           ThreadEventId INTEGER PRIMARY KEY,
                           ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                           TimestampUtc TEXT NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           StackBase TEXT NULL,
                           StackLimit TEXT NULL,
                           UserStackBase TEXT NULL,
                           UserStackLimit TEXT NULL,
                           Affinity TEXT NULL,
                           Win32StartAddr TEXT NULL,
                           TebBase TEXT NULL,
                           SubProcessTag INTEGER NULL,
                           BasePriority INTEGER NULL,
                               PagePriority INTEGER NULL,
                               IoPriority INTEGER NULL,
                               ThreadFlags INTEGER NULL,
                               CpuStartedAtUtc TEXT NULL,
                               CpuEndedAtUtc TEXT NULL,
                               CpuDurationTicks INTEGER NULL
                           );

                       CREATE INDEX IF NOT EXISTS IX_ThreadEvents_ThreadTimestamp
                       ON ThreadEvents (ThreadId, TimestampUtc);

                              CREATE INDEX IF NOT EXISTS IX_ThreadEvents_ProcessRecord
                              ON ThreadEvents (ProcessRecordId, TimestampUtc);

                              CREATE TABLE IF NOT EXISTS CpuProfileSamples
                              (
                                  CpuProfileSampleId INTEGER PRIMARY KEY,
                                  ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                                  TimestampUtc TEXT NOT NULL,
                                  ProcessorNumber INTEGER NOT NULL,
                                  ProcessId INTEGER NOT NULL,
                                  ThreadId INTEGER NOT NULL,
                                  InstructionPointer TEXT NULL
                              );

                              CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_Timestamp
                              ON CpuProfileSamples (TimestampUtc);

                              CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_ProcessRecordTimestamp
                              ON CpuProfileSamples (ProcessRecordId, TimestampUtc);

                              CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_ProcessTimestamp
                               ON CpuProfileSamples (ProcessId, TimestampUtc);

                               CREATE TABLE IF NOT EXISTS ThreadLifetimes
                               (
                                   ThreadLifetimeId INTEGER PRIMARY KEY,
                                   ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                                   ProcessId INTEGER NOT NULL,
                                   ThreadId INTEGER NOT NULL,
                                   StartedAtUtc TEXT NOT NULL,
                                   EndedAtUtc TEXT NOT NULL,
                                   CpuStartedAtUtc TEXT NULL,
                                   CpuEndedAtUtc TEXT NULL,
                                   CpuDurationTicks INTEGER NULL,
                                   ContextSwitchCount INTEGER NOT NULL,
                                   IsComplete INTEGER NOT NULL,
                                   ContextSwitchJson TEXT NOT NULL,
                                   UNIQUE (ProcessId, ThreadId, StartedAtUtc)
                               );

                               CREATE INDEX IF NOT EXISTS IX_ThreadLifetimes_ProcessRecordStarted
                               ON ThreadLifetimes (ProcessRecordId, StartedAtUtc);

                               CREATE INDEX IF NOT EXISTS IX_ThreadLifetimes_ThreadStarted
                               ON ThreadLifetimes (ThreadId, StartedAtUtc);

                               CREATE INDEX IF NOT EXISTS IX_ThreadLifetimes_IsCompleteEnded
                               ON ThreadLifetimes (IsComplete, EndedAtUtc);

                               CREATE TABLE IF NOT EXISTS DpcEvents
                               (
                                   DpcEventId INTEGER PRIMARY KEY,
                                   TimestampUtc TEXT NOT NULL,
                                   ProcessorNumber INTEGER NOT NULL,
                                   EventId INTEGER NOT NULL,
                                   Version INTEGER NOT NULL,
                                   Opcode INTEGER NOT NULL,
                                   InitialTime TEXT NULL,
                                   Routine TEXT NULL
                               );

                               CREATE INDEX IF NOT EXISTS IX_DpcEvents_RoutineTimestamp
                               ON DpcEvents (Routine, TimestampUtc);

                               CREATE INDEX IF NOT EXISTS IX_DpcEvents_ProcessorTimestamp
                               ON DpcEvents (ProcessorNumber, TimestampUtc);

                               CREATE TABLE IF NOT EXISTS InterruptEvents
                               (
                                   InterruptEventId INTEGER PRIMARY KEY,
                                   TimestampUtc TEXT NOT NULL,
                                   ProcessorNumber INTEGER NOT NULL,
                                   EventId INTEGER NOT NULL,
                                   Version INTEGER NOT NULL,
                                   Opcode INTEGER NOT NULL,
                                   InitialTime TEXT NULL,
                                   Routine TEXT NULL,
                                   ReturnValue INTEGER NULL
                               );

                               CREATE INDEX IF NOT EXISTS IX_InterruptEvents_RoutineTimestamp
                               ON InterruptEvents (Routine, TimestampUtc);

                               CREATE INDEX IF NOT EXISTS IX_InterruptEvents_ProcessorTimestamp
                               ON InterruptEvents (ProcessorNumber, TimestampUtc);";
                       command.ExecuteNonQuery();
            }

            EnsureEnergyEstimationEngineColumns(connection);
            EnsureEnergyEstimationEngineEnergyColumnsAreIntegers(connection);
            EnsureEnergyEstimationEngineTablesWithoutProcessColumns(connection);
            EnsureThreadEventColumns(connection);
            EnsureProcessColumns(connection);
            EnsureWmiActivityColumns(connection);
            EnsureKernelPowerEventColumns(connection);

            SqliteTransaction transaction = connection.BeginTransaction();
            _writeImageLoadCommand = CreateWriteImageLoadCommand(connection, transaction);
            _writeImageUnloadCommand = CreateWriteImageUnloadCommand(connection, transaction);
            _writeProcessStartCommand = CreateWriteProcessStartCommand(connection, transaction);
            _writeProcessStopCommand = CreateWriteProcessStopCommand(connection, transaction);
            _writeWmiActivityCommand = CreateWriteWmiActivityCommand(connection, transaction);
            _writeEnergyEstimationEngineCommand = CreateWriteEnergyEstimationEngineCommand(connection, transaction);
            _writeEnergyEstimationQueryStatsCommand = CreateWriteEnergyEstimationQueryStatsCommand(connection, transaction);
            _writeEnergyEstimationEnergyDeltaCommand = CreateWriteEnergyEstimationEnergyDeltaCommand(connection, transaction);
            _writeEnergyEstimationCpuPowerCommand = CreateWriteEnergyEstimationCpuPowerCommand(connection, transaction);
            _writeKernelAcpiCommand = CreateWriteKernelAcpiCommand(connection, transaction);
            _writeKernelPowerCommand = CreateWriteKernelPowerCommand(connection, transaction);
            _writeThreadEventCommand = CreateWriteThreadEventCommand(connection, transaction);
            _writeCpuProfileSampleCommand = CreateWriteCpuProfileSampleCommand(connection, transaction);
            _writeDpcCommand = CreateWriteDpcCommand(connection, transaction);
            _writeInterruptCommand = CreateWriteInterruptCommand(connection, transaction);
            _writeThreadLifetimeCommand = CreateWriteThreadLifetimeCommand(connection, transaction);
            _connection = connection;
            _transaction = transaction;
            _batchedWriteCount = 0;
        }

        public void WriteImageLoad(in ImageLoadEventInfo data)
        {
            SqliteCommand command = _writeImageLoadCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$imageBase"].Value = ToDbValue(data.ImageBase);
            command.Parameters["$imageSize"].Value = ToDbValue(data.ImageSize);
            command.Parameters["$imageCheckSum"].Value = ToDbValue(data.ImageCheckSum);
            command.Parameters["$timeDateStamp"].Value = ToDbValue(data.TimeDateStamp);
            command.Parameters["$defaultBase"].Value = ToDbValue(data.DefaultBase);
            command.Parameters["$fileName"].Value = data.FileName ?? string.Empty;
            command.Parameters["$loadedAtUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.ExecuteNonQuery();
        }

        public void WriteKernelAcpi(KernelAcpiEventInfo data)
        {
            SqliteCommand command = _writeKernelAcpiCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }

        public void WriteKernelPower(KernelPowerEventInfo data)
        {
            SqliteCommand command = _writeKernelPowerCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }

        public void WriteWmiActivity(in WmiActivityEventInfo data)
        {
            SqliteCommand command = _writeWmiActivityCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$operation"].Value = data.Operation;
            command.Parameters["$namespaceName"].Value = data.NamespaceName;
            command.ExecuteNonQuery();
        }

        public void WriteEnergyEstimationEngine(in EnergyEstimationEngineEventInfo_37 data)
        {
            SqliteCommand command = _writeEnergyEstimationEngineCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$appName"].Value = data.AppName ?? string.Empty;
            command.Parameters["$userId"].Value = data.UserId;
            command.Parameters["$cpuEnergy"].Value = checked((long)data.CpuEnergy);
            command.Parameters["$gpuEnergy"].Value = checked((long)data.GpuEnergy);
            command.Parameters["$displayEnergy"].Value = checked((long)data.DisplayEnergy);
            command.Parameters["$diskEnergy"].Value = checked((long)data.DiskEnergy);
            command.Parameters["$networkEnergy"].Value = checked((long)data.NetworkEnergy);
            command.Parameters["$mbbEnergy"].Value = checked((long)data.MbbEnergy);
            command.Parameters["$lossEnergy"].Value = checked((long)data.LossEnergy);
            command.Parameters["$otherEnergy"].Value = checked((long)data.OtherEnergy);
            command.Parameters["$emiEnergy"].Value = checked((long)data.EmiEnergy);
            command.Parameters["$timeInMSec"].Value = data.TimeInMSec;
            command.Parameters["$npuEnergy"].Value = checked((long)data.NpuEnergy);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteEnergyEstimationEngineQueryStats(in EnergyEstimationEngineEventInfo_33 data)
        {
            SqliteCommand command = _writeEnergyEstimationQueryStatsCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$sruWorkItemType"].Value = data.SruWorkItemType;
            command.Parameters["$providerState"].Value = data.ProviderState;
            command.Parameters["$deviceState"].Value = data.DeviceState;
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteEnergyEstimationEngineEnergyDelta(in EnergyEstimationEngineEventInfo_18 data)
        {
            SqliteCommand command = _writeEnergyEstimationEnergyDeltaCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$component"].Value = data.Component;
            command.Parameters["$energyDelta"].Value = data.EnergyDelta.ToString(CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteEnergyEstimationEngineCpuPower(in EnergyEstimationEngineEventInfo_14 data)
        {
            SqliteCommand command = _writeEnergyEstimationCpuPowerCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$cpuId"].Value = data.CpuId;
            command.Parameters["$currentFrequency"].Value = data.CurrentFrequency;
            command.Parameters["$lastBusyFrequency"].Value = data.LastBusyFrequency;
            command.Parameters["$energy"].Value = data.Energy.ToString(CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteCpuProfileSample(ProfileEventInfo data)
        {
            SqliteCommand command = _writeCpuProfileSampleCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$processorNumber"].Value = data.ProcessorNumber;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$instructionPointer"].Value = ToDbValue(ToHex(data.InstructionPointer));
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteThreadLifetime(
            uint processId,
            uint threadId,
            DateTime startedAt,
            DateTime endedAt,
            DateTime? cpuStartedAt,
            DateTime? cpuEndedAt,
            long? cpuDurationTicks,
            int contextSwitchCount,
            bool isComplete,
            string contextSwitchJson)
        {
            SqliteCommand command = _writeThreadLifetimeCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$processId"].Value = Convert.ToInt64(processId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(threadId, CultureInfo.InvariantCulture);
            command.Parameters["$startedAtUtc"].Value = ToUtcTimestamp(startedAt);
            command.Parameters["$endedAtUtc"].Value = ToUtcTimestamp(endedAt);
            command.Parameters["$cpuStartedAtUtc"].Value = cpuStartedAt is null ? DBNull.Value : ToUtcTimestamp(cpuStartedAt.Value);
            command.Parameters["$cpuEndedAtUtc"].Value = cpuEndedAt is null ? DBNull.Value : ToUtcTimestamp(cpuEndedAt.Value);
            command.Parameters["$cpuDurationTicks"].Value = ToDbValue(cpuDurationTicks);
            command.Parameters["$contextSwitchCount"].Value = contextSwitchCount;
            command.Parameters["$isComplete"].Value = isComplete ? 1 : 0;
            command.Parameters["$contextSwitchJson"].Value = contextSwitchJson;
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteDpc(DpcEventInfo data)
        {
            SqliteCommand command = _writeDpcCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$processorNumber"].Value = data.ProcessorNumber;
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$initialTime"].Value = ToDbValue(ToHex(data.InitialTime));
            command.Parameters["$routine"].Value = ToDbValue(ToHex(data.Routine));
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteInterrupt(InterruptEventInfo data)
        {
            SqliteCommand command = _writeInterruptCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$processorNumber"].Value = data.ProcessorNumber;
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$initialTime"].Value = ToDbValue(ToHex(data.InitialTime));
            command.Parameters["$routine"].Value = ToDbValue(ToHex(data.Routine));
            command.Parameters["$returnValue"].Value = ToDbValue(data.ReturnValue);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteImageUnLoad(in ImageLoadEventInfo data)
        {
            SqliteCommand command = _writeImageUnloadCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$unloadedAtUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$imageBase"].Value = ToDbValue(data.ImageBase);
            command.ExecuteNonQuery();
        }

        public void WriteProcessStart(ProcessInfo process)
        {
            SqliteCommand command = _writeProcessStartCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$processId"].Value = Convert.ToInt64(process.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$parentProcessId"].Value = Convert.ToInt64(process.ParentProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$imageFileName"].Value = process.ImageFileName ?? string.Empty;
            command.Parameters["$commandLine"].Value = process.CommandLine ?? string.Empty;
            command.Parameters["$startedAtUtc"].Value = ToUtcTimestamp(process.StartTime);
            command.ExecuteNonQuery();
        }

        public void WriteProcessStop(ProcessInfo process, long? cpuDurationTicks = null, double? cpuUsagePercent = null)
        {
            DateTime endTime = process.EndTime ?? throw new InvalidOperationException("程序結束事件未提供結束時間。");
            if ((cpuDurationTicks is null) != (cpuUsagePercent is null))
            {
                throw new ArgumentException("CPU 總時間與使用率必須同時提供。");
            }

            if (cpuDurationTicks < 0 || cpuUsagePercent < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cpuDurationTicks), "CPU 使用資料不可為負值。");
            }

            SqliteCommand command = _writeProcessStopCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$endedAtUtc"].Value = ToUtcTimestamp(endTime);
            command.Parameters["$processId"].Value = Convert.ToInt64(process.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$startedAtUtc"].Value = ToUtcTimestamp(process.StartTime);
            command.Parameters["$cpuDurationTicks"].Value = ToDbValue(cpuDurationTicks);
            command.Parameters["$cpuUsagePercent"].Value = ToDbValue(cpuUsagePercent);
            command.ExecuteNonQuery();
        }

        public long WriteThreadEvent(
            in ThreadStartStopEventInfo data,
            DateTime? cpuStartedAt = null,
            DateTime? cpuEndedAt = null,
            long? cpuDurationTicks = null)
        {
            if ((cpuStartedAt is null) != (cpuEndedAt is null) ||
                (cpuStartedAt is null) != (cpuDurationTicks is null))
            {
                throw new ArgumentException("CPU 執行資料必須同時提供開始時間、結束時間與持續時間。");
            }

            if (cpuStartedAt is not null &&
                (cpuEndedAt < cpuStartedAt || cpuDurationTicks < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(cpuDurationTicks), "CPU 執行資料無效。");
            }

            SqliteCommand command = _writeThreadEventCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$stackBase"].Value = ToDbValue(ToHex(data.StackBase));
            command.Parameters["$stackLimit"].Value = ToDbValue(ToHex(data.StackLimit));
            command.Parameters["$userStackBase"].Value = ToDbValue(ToHex(data.UserStackBase));
            command.Parameters["$userStackLimit"].Value = ToDbValue(ToHex(data.UserStackLimit));
            command.Parameters["$affinity"].Value = ToDbValue(ToHex(data.Affinity));
            command.Parameters["$win32StartAddr"].Value = ToDbValue(ToHex(data.Win32StartAddr));
            command.Parameters["$tebBase"].Value = ToDbValue(ToHex(data.TebBase));
            command.Parameters["$subProcessTag"].Value = ToDbValue(data.SubProcessTag);
            command.Parameters["$basePriority"].Value = ToDbValue(data.BasePriority);
            command.Parameters["$pagePriority"].Value = ToDbValue(data.PagePriority);
            command.Parameters["$ioPriority"].Value = ToDbValue(data.IoPriority);
            command.Parameters["$threadFlags"].Value = ToDbValue(data.ThreadFlags);
            command.Parameters["$cpuStartedAtUtc"].Value = cpuStartedAt is null ? DBNull.Value : ToUtcTimestamp(cpuStartedAt.Value);
            command.Parameters["$cpuEndedAtUtc"].Value = cpuEndedAt is null ? DBNull.Value : ToUtcTimestamp(cpuEndedAt.Value);
            command.Parameters["$cpuDurationTicks"].Value = ToDbValue(cpuDurationTicks);
            long threadEventId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            CommitWriteBatchIfNeeded();
            return threadEventId;
        }


        public void Complete()
        {
            _transaction?.Commit();
            Close();
        }

        public void Fail()
        {
            _transaction?.Rollback();
            Close();
        }

        public void Dispose()
        {
            Fail();
        }

        private void Close()
        {
            _writeImageLoadCommand?.Dispose();
            _writeImageLoadCommand = null;
            _writeImageUnloadCommand?.Dispose();
            _writeImageUnloadCommand = null;
            _writeProcessStartCommand?.Dispose();
            _writeProcessStartCommand = null;
            _writeProcessStopCommand?.Dispose();
            _writeProcessStopCommand = null;
            _writeWmiActivityCommand?.Dispose();
            _writeWmiActivityCommand = null;
            _writeEnergyEstimationEngineCommand?.Dispose();
            _writeEnergyEstimationEngineCommand = null;
            _writeEnergyEstimationQueryStatsCommand?.Dispose();
            _writeEnergyEstimationQueryStatsCommand = null;
            _writeEnergyEstimationEnergyDeltaCommand?.Dispose();
            _writeEnergyEstimationEnergyDeltaCommand = null;
            _writeEnergyEstimationCpuPowerCommand?.Dispose();
            _writeEnergyEstimationCpuPowerCommand = null;
            _writeKernelAcpiCommand?.Dispose();
            _writeKernelAcpiCommand = null;
            _writeKernelPowerCommand?.Dispose();
            _writeKernelPowerCommand = null;
            _writeThreadEventCommand?.Dispose();
            _writeThreadEventCommand = null;
            _writeCpuProfileSampleCommand?.Dispose();
            _writeCpuProfileSampleCommand = null;
            _writeDpcCommand?.Dispose();
            _writeDpcCommand = null;
            _writeInterruptCommand?.Dispose();
            _writeInterruptCommand = null;
            _writeThreadLifetimeCommand?.Dispose();
            _writeThreadLifetimeCommand = null;
            _transaction?.Dispose();
            _transaction = null;
            _connection?.Dispose();
            _connection = null;
            _batchedWriteCount = 0;
        }

        private void CommitWriteBatchIfNeeded()
        {
            if (++_batchedWriteCount < WriteBatchSize)
            {
                return;
            }

            SqliteConnection connection = _connection ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            SqliteTransaction completedTransaction = _transaction ?? throw new InvalidOperationException("SQLite 交易尚未建立。");
            completedTransaction.Commit();
            completedTransaction.Dispose();

            SqliteTransaction nextTransaction = connection.BeginTransaction();
            _transaction = nextTransaction;
            BindCommandsToTransaction(nextTransaction);
            _batchedWriteCount = 0;
        }

        private void BindCommandsToTransaction(SqliteTransaction transaction)
        {
            foreach (SqliteCommand? command in new[]
            {
                _writeImageLoadCommand,
                _writeImageUnloadCommand,
                _writeProcessStartCommand,
                _writeProcessStopCommand,
                _writeWmiActivityCommand,
                _writeEnergyEstimationEngineCommand,
                _writeEnergyEstimationQueryStatsCommand,
                _writeEnergyEstimationEnergyDeltaCommand,
                _writeEnergyEstimationCpuPowerCommand,
                _writeKernelAcpiCommand,
                _writeKernelPowerCommand,
                _writeThreadEventCommand,
                _writeCpuProfileSampleCommand,
                _writeDpcCommand,
                _writeInterruptCommand,
                _writeThreadLifetimeCommand,
            })
            {
                if (command is not null)
                {
                    command.Transaction = transaction;
                }
            }
        }

        private static SqliteCommand CreateWriteImageLoadCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO ImageLoads
                    (ProcessRecordId, ProcessId, ImageBase, ImageSize, ImageCheckSum, TimeDateStamp, DefaultBase, FileName, LoadedAtUtc)
                  VALUES
                    (
                        (
                            SELECT ProcessRecordId
                            FROM Processes
                            WHERE ProcessId = $processId
                              AND StartedAtUtc <= $loadedAtUtc
                              AND (EndedAtUtc IS NULL OR EndedAtUtc >= $loadedAtUtc)
                            ORDER BY StartedAtUtc DESC, ProcessRecordId DESC
                            LIMIT 1
                        ),
                        $processId, $imageBase, $imageSize, $imageCheckSum, $timeDateStamp, $defaultBase, $fileName, $loadedAtUtc
                    );";
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$imageBase", SqliteType.Text);
            command.Parameters.Add("$imageSize", SqliteType.Text);
            command.Parameters.Add("$imageCheckSum", SqliteType.Integer);
            command.Parameters.Add("$timeDateStamp", SqliteType.Integer);
            command.Parameters.Add("$defaultBase", SqliteType.Text);
            command.Parameters.Add("$fileName", SqliteType.Text);
            command.Parameters.Add("$loadedAtUtc", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteThreadEventCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO ThreadEvents
                    (ProcessRecordId, TimestampUtc, Opcode, ProcessId, ThreadId, StackBase, StackLimit, UserStackBase, UserStackLimit, Affinity, Win32StartAddr, TebBase, SubProcessTag, BasePriority, PagePriority, IoPriority, ThreadFlags, CpuStartedAtUtc, CpuEndedAtUtc, CpuDurationTicks)
                  VALUES
                    (
                        (
                            SELECT ProcessRecordId
                            FROM Processes
                            WHERE ProcessId = $processId
                              AND StartedAtUtc <= $timestampUtc
                              AND (EndedAtUtc IS NULL OR EndedAtUtc >= $timestampUtc)
                            ORDER BY StartedAtUtc DESC, ProcessRecordId DESC
                            LIMIT 1
                        ),
                        $timestampUtc, $opcode, $processId, $threadId, $stackBase, $stackLimit, $userStackBase, $userStackLimit, $affinity, $win32StartAddr, $tebBase, $subProcessTag, $basePriority, $pagePriority, $ioPriority, $threadFlags, $cpuStartedAtUtc, $cpuEndedAtUtc, $cpuDurationTicks
                    )
                  RETURNING ThreadEventId;";
            foreach (string parameterName in new[] { "$timestampUtc", "$opcode", "$processId", "$threadId", "$stackBase", "$stackLimit", "$userStackBase", "$userStackLimit", "$affinity", "$win32StartAddr", "$tebBase", "$subProcessTag", "$basePriority", "$pagePriority", "$ioPriority", "$threadFlags", "$cpuStartedAtUtc", "$cpuEndedAtUtc", "$cpuDurationTicks" })
            {
                command.Parameters.AddWithValue(parameterName, DBNull.Value);
            }
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteCpuProfileSampleCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO CpuProfileSamples
                    (ProcessRecordId, TimestampUtc, ProcessorNumber, ProcessId, ThreadId, InstructionPointer)
                  VALUES
                    (
                        (
                            SELECT ProcessRecordId
                            FROM Processes
                            WHERE ProcessId = $processId
                              AND StartedAtUtc <= $timestampUtc
                              AND (EndedAtUtc IS NULL OR EndedAtUtc >= $timestampUtc)
                            ORDER BY StartedAtUtc DESC, ProcessRecordId DESC
                            LIMIT 1
                        ),
                        $timestampUtc, $processorNumber, $processId, $threadId, $instructionPointer
                    );";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$processorNumber", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$instructionPointer", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteKernelAcpiCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO KernelAcpiEvents
                    (TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $processId, $threadId);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteKernelPowerCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO KernelPowerEvents
                    (TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, PropertiesJson)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $propertiesJson);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$propertiesJson", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteDpcCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO DpcEvents
                    (TimestampUtc, ProcessorNumber, EventId, Version, Opcode, InitialTime, Routine)
                  VALUES
                    ($timestampUtc, $processorNumber, $eventId, $version, $opcode, $initialTime, $routine);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$processorNumber", SqliteType.Integer);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$initialTime", SqliteType.Text);
            command.Parameters.Add("$routine", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteThreadLifetimeCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO ThreadLifetimes
                    (ProcessRecordId, ProcessId, ThreadId, StartedAtUtc, EndedAtUtc, CpuStartedAtUtc, CpuEndedAtUtc, CpuDurationTicks, ContextSwitchCount, IsComplete, ContextSwitchJson)
                  VALUES
                    (
                        (
                            SELECT ProcessRecordId
                            FROM Processes
                            WHERE ProcessId = $processId
                              AND StartedAtUtc <= $startedAtUtc
                              AND (EndedAtUtc IS NULL OR EndedAtUtc >= $startedAtUtc)
                            ORDER BY StartedAtUtc DESC, ProcessRecordId DESC
                            LIMIT 1
                        ),
                        $processId, $threadId, $startedAtUtc, $endedAtUtc, $cpuStartedAtUtc, $cpuEndedAtUtc, $cpuDurationTicks, $contextSwitchCount, $isComplete, $contextSwitchJson
                    );";
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$startedAtUtc", SqliteType.Text);
            command.Parameters.Add("$endedAtUtc", SqliteType.Text);
            command.Parameters.Add("$cpuStartedAtUtc", SqliteType.Text);
            command.Parameters.Add("$cpuEndedAtUtc", SqliteType.Text);
            command.Parameters.Add("$cpuDurationTicks", SqliteType.Integer);
            command.Parameters.Add("$contextSwitchCount", SqliteType.Integer);
            command.Parameters.Add("$isComplete", SqliteType.Integer);
            command.Parameters.Add("$contextSwitchJson", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteInterruptCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO InterruptEvents
                    (TimestampUtc, ProcessorNumber, EventId, Version, Opcode, InitialTime, Routine, ReturnValue)
                  VALUES
                    ($timestampUtc, $processorNumber, $eventId, $version, $opcode, $initialTime, $routine, $returnValue);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$processorNumber", SqliteType.Integer);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$initialTime", SqliteType.Text);
            command.Parameters.Add("$routine", SqliteType.Text);
            command.Parameters.Add("$returnValue", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteEnergyEstimationEngineCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO EnergyEstimationEngineEvents
                    (TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, AppName, UserId, CpuEnergy, GpuEnergy, DisplayEnergy, DiskEnergy, NetworkEnergy, MbbEnergy, LossEnergy, OtherEnergy, EmiEnergy, TimeInMSec, NpuEnergy)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $appName, $userId, $cpuEnergy, $gpuEnergy, $displayEnergy, $diskEnergy, $networkEnergy, $mbbEnergy, $lossEnergy, $otherEnergy, $emiEnergy, $timeInMSec, $npuEnergy);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$appName", SqliteType.Text);
            command.Parameters.Add("$userId", SqliteType.Integer);
            command.Parameters.Add("$cpuEnergy", SqliteType.Integer);
            command.Parameters.Add("$gpuEnergy", SqliteType.Integer);
            command.Parameters.Add("$displayEnergy", SqliteType.Integer);
            command.Parameters.Add("$diskEnergy", SqliteType.Integer);
            command.Parameters.Add("$networkEnergy", SqliteType.Integer);
            command.Parameters.Add("$mbbEnergy", SqliteType.Integer);
            command.Parameters.Add("$lossEnergy", SqliteType.Integer);
            command.Parameters.Add("$otherEnergy", SqliteType.Integer);
            command.Parameters.Add("$emiEnergy", SqliteType.Integer);
            command.Parameters.Add("$timeInMSec", SqliteType.Integer);
            command.Parameters.Add("$npuEnergy", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteEnergyEstimationQueryStatsCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO EnergyEstimationEngine_33
                    (TimestampUtc, EventId, Version, Opcode, SruWorkItemType, ProviderState, DeviceState)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $sruWorkItemType, $providerState, $deviceState);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$sruWorkItemType", SqliteType.Integer);
            command.Parameters.Add("$providerState", SqliteType.Integer);
            command.Parameters.Add("$deviceState", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteEnergyEstimationEnergyDeltaCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO EnergyEstimationEngine_18
                    (TimestampUtc, EventId, Version, Opcode, Component, EnergyDelta)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $component, $energyDelta);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$component", SqliteType.Integer);
            command.Parameters.Add("$energyDelta", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteEnergyEstimationCpuPowerCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO EnergyEstimationEngine_14
                    (TimestampUtc, EventId, Version, Opcode, CpuId, CurrentFrequency, LastBusyFrequency, Energy)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $cpuId, $currentFrequency, $lastBusyFrequency, $energy);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$cpuId", SqliteType.Integer);
            command.Parameters.Add("$currentFrequency", SqliteType.Integer);
            command.Parameters.Add("$lastBusyFrequency", SqliteType.Integer);
            command.Parameters.Add("$energy", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteWmiActivityCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO WmiActivityEvents
                    (ProcessRecordId, TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, Operation, NamespaceName)
                  VALUES
                    (
                        (
                            SELECT ProcessRecordId
                            FROM Processes
                            WHERE ProcessId = $processId
                              AND StartedAtUtc <= $timestampUtc
                              AND (EndedAtUtc IS NULL OR EndedAtUtc >= $timestampUtc)
                            ORDER BY StartedAtUtc DESC, ProcessRecordId DESC
                            LIMIT 1
                        ),
                        $timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $operation, $namespaceName
                    );";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$operation", SqliteType.Text);
            command.Parameters.Add("$namespaceName", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteImageUnloadCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"UPDATE ImageLoads
                  SET UnloadedAtUtc = $unloadedAtUtc
                  WHERE ImageLoadId =
                  (
                      SELECT ImageLoadId
                      FROM ImageLoads
                      WHERE ProcessId = $processId
                        AND ImageBase IS $imageBase
                        AND UnloadedAtUtc IS NULL
                      ORDER BY LoadedAtUtc DESC, ImageLoadId DESC
                      LIMIT 1
                  );";
            command.Parameters.Add("$unloadedAtUtc", SqliteType.Text);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$imageBase", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteProcessStartCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO Processes
                    (ProcessId, ParentProcessId, ImageFileName, CommandLine, StartedAtUtc)
                  VALUES
                    ($processId, $parentProcessId, $imageFileName, $commandLine, $startedAtUtc);";
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$parentProcessId", SqliteType.Integer);
            command.Parameters.Add("$imageFileName", SqliteType.Text);
            command.Parameters.Add("$commandLine", SqliteType.Text);
            command.Parameters.Add("$startedAtUtc", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteProcessStopCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"UPDATE Processes
                  SET EndedAtUtc = $endedAtUtc,
                      CpuDurationTicks = $cpuDurationTicks,
                      CpuUsagePercent = $cpuUsagePercent
                  WHERE ProcessId = $processId
                    AND StartedAtUtc = $startedAtUtc;";
            command.Parameters.Add("$endedAtUtc", SqliteType.Text);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$startedAtUtc", SqliteType.Text);
            command.Parameters.Add("$cpuDurationTicks", SqliteType.Integer);
            command.Parameters.Add("$cpuUsagePercent", SqliteType.Real);
            command.Prepare();
            return command;
        }

        private static void EnsureEnergyEstimationEngineColumns(SqliteConnection connection)
        {
            (string Name, string Definition)[] columns =
            {
                ("AppName", "TEXT NOT NULL DEFAULT ''"),
                ("UserId", "INTEGER NOT NULL DEFAULT 0"),
                ("CpuEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("GpuEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("DisplayEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("DiskEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("NetworkEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("MbbEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("LossEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("OtherEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("EmiEnergy", "INTEGER NOT NULL DEFAULT 0"),
                ("TimeInMSec", "INTEGER NOT NULL DEFAULT 0"),
                ("NpuEnergy", "INTEGER NOT NULL DEFAULT 0"),
            };

            foreach ((string name, string definition) in columns)
            {
                using SqliteCommand columnExistsCommand = connection.CreateCommand();
                columnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('EnergyEstimationEngineEvents') WHERE name = $name;";
                columnExistsCommand.Parameters.AddWithValue("$name", name);

                if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                using SqliteCommand addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = $"ALTER TABLE EnergyEstimationEngineEvents ADD COLUMN {name} {definition};";
                addColumnCommand.ExecuteNonQuery();
            }
        }

        private static void EnsureEnergyEstimationEngineEnergyColumnsAreIntegers(SqliteConnection connection)
        {
            using SqliteCommand columnTypeCommand = connection.CreateCommand();
            columnTypeCommand.CommandText = "SELECT type FROM pragma_table_info('EnergyEstimationEngineEvents') WHERE name = 'CpuEnergy';";
            string? columnType = columnTypeCommand.ExecuteScalar() as string;

            if (string.Equals(columnType, "INTEGER", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"CREATE TABLE EnergyEstimationEngineEvents_WithIntegerEnergy
                  (
                      EnergyEstimationEngineEventId INTEGER PRIMARY KEY,
                      TimestampUtc TEXT NOT NULL,
                      EventId INTEGER NOT NULL,
                      Version INTEGER NOT NULL,
                      Opcode INTEGER NOT NULL,
                      ProcessId INTEGER NOT NULL,
                      ThreadId INTEGER NOT NULL,
                      AppName TEXT NOT NULL,
                      UserId INTEGER NOT NULL,
                      CpuEnergy INTEGER NOT NULL,
                      GpuEnergy INTEGER NOT NULL,
                      DisplayEnergy INTEGER NOT NULL,
                      DiskEnergy INTEGER NOT NULL,
                      NetworkEnergy INTEGER NOT NULL,
                      MbbEnergy INTEGER NOT NULL,
                      LossEnergy INTEGER NOT NULL,
                      OtherEnergy INTEGER NOT NULL,
                      EmiEnergy INTEGER NOT NULL,
                      TimeInMSec INTEGER NOT NULL,
                      NpuEnergy INTEGER NOT NULL
                  );

                  INSERT INTO EnergyEstimationEngineEvents_WithIntegerEnergy
                  SELECT * FROM EnergyEstimationEngineEvents;

                  DROP TABLE EnergyEstimationEngineEvents;

                  ALTER TABLE EnergyEstimationEngineEvents_WithIntegerEnergy
                  RENAME TO EnergyEstimationEngineEvents;

                  CREATE INDEX IX_EnergyEstimationEngineEvents_ProcessTimestamp
                  ON EnergyEstimationEngineEvents (ProcessId, TimestampUtc);";
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        private static void EnsureEnergyEstimationEngineTablesWithoutProcessColumns(SqliteConnection connection)
        {
            RebuildEnergyEstimationEngineTableWithoutProcessColumns(
                connection,
                "EnergyEstimationEngine_33",
                "EnergyEstimationEngine_33Id INTEGER PRIMARY KEY, TimestampUtc TEXT NOT NULL, EventId INTEGER NOT NULL, Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, SruWorkItemType INTEGER NOT NULL, ProviderState INTEGER NOT NULL, DeviceState INTEGER NOT NULL",
                "EnergyEstimationEngine_33Id, TimestampUtc, EventId, Version, Opcode, SruWorkItemType, ProviderState, DeviceState",
                "IX_EnergyEstimationEngine_33_Timestamp");

            RebuildEnergyEstimationEngineTableWithoutProcessColumns(
                connection,
                "EnergyEstimationEngine_18",
                "EnergyEstimationEngine_18Id INTEGER PRIMARY KEY, TimestampUtc TEXT NOT NULL, EventId INTEGER NOT NULL, Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, Component INTEGER NOT NULL, EnergyDelta TEXT NOT NULL",
                "EnergyEstimationEngine_18Id, TimestampUtc, EventId, Version, Opcode, Component, EnergyDelta",
                "IX_EnergyEstimationEngine_18_Timestamp");

            RebuildEnergyEstimationEngineTableWithoutProcessColumns(
                connection,
                "EnergyEstimationEngine_14",
                "EnergyEstimationEngine_14Id INTEGER PRIMARY KEY, TimestampUtc TEXT NOT NULL, EventId INTEGER NOT NULL, Version INTEGER NOT NULL, Opcode INTEGER NOT NULL, CpuId INTEGER NOT NULL, CurrentFrequency INTEGER NOT NULL, LastBusyFrequency INTEGER NOT NULL, Energy TEXT NOT NULL",
                "EnergyEstimationEngine_14Id, TimestampUtc, EventId, Version, Opcode, CpuId, CurrentFrequency, LastBusyFrequency, Energy",
                "IX_EnergyEstimationEngine_14_Timestamp");
        }

        private static void RebuildEnergyEstimationEngineTableWithoutProcessColumns(
            SqliteConnection connection,
            string tableName,
            string columnDefinitions,
            string retainedColumns,
            string timestampIndexName)
        {
            using SqliteCommand columnExistsCommand = connection.CreateCommand();
            columnExistsCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = 'ProcessId';";
            if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                return;
            }

            string replacementTableName = $"{tableName}_WithoutProcessColumns";
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"CREATE TABLE {replacementTableName} ({columnDefinitions});\n" +
                $"INSERT INTO {replacementTableName} ({retainedColumns}) SELECT {retainedColumns} FROM {tableName};\n" +
                $"DROP TABLE {tableName};\n" +
                $"ALTER TABLE {replacementTableName} RENAME TO {tableName};\n" +
                $"CREATE INDEX {timestampIndexName} ON {tableName} (TimestampUtc);";
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        private static void EnsureThreadEventColumns(SqliteConnection connection)
        {
            (string Name, string Definition)[] columns =
            {
                ("CpuStartedAtUtc", "TEXT NULL"),
                ("CpuEndedAtUtc", "TEXT NULL"),
                ("CpuDurationTicks", "INTEGER NULL"),
            };

            foreach ((string name, string definition) in columns)
            {
                using SqliteCommand columnExistsCommand = connection.CreateCommand();
                columnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ThreadEvents') WHERE name = $name;";
                columnExistsCommand.Parameters.AddWithValue("$name", name);

                if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                using SqliteCommand addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = $"ALTER TABLE ThreadEvents ADD COLUMN {name} {definition};";
                addColumnCommand.ExecuteNonQuery();
            }

            using SqliteCommand createIndexCommand = connection.CreateCommand();
            createIndexCommand.CommandText =
                @"CREATE INDEX IF NOT EXISTS IX_ThreadEvents_ThreadCpuStarted
                  ON ThreadEvents (ThreadId, CpuStartedAtUtc);";
            createIndexCommand.ExecuteNonQuery();
        }

        private static void EnsureWmiActivityColumns(SqliteConnection connection)
        {
            using SqliteCommand columnExistsCommand = connection.CreateCommand();
            columnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WmiActivityEvents') WHERE name = 'ProcessRecordId';";

            if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using SqliteCommand addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = "ALTER TABLE WmiActivityEvents ADD COLUMN ProcessRecordId INTEGER NULL;";
                addColumnCommand.ExecuteNonQuery();
            }

            using SqliteCommand createIndexCommand = connection.CreateCommand();
            createIndexCommand.CommandText =
                @"CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_ProcessRecord
                  ON WmiActivityEvents (ProcessRecordId, TimestampUtc);";
            createIndexCommand.ExecuteNonQuery();
        }

        private static void EnsureKernelPowerEventColumns(SqliteConnection connection)
        {
            using SqliteCommand columnExistsCommand = connection.CreateCommand();
            columnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('KernelPowerEvents') WHERE name = 'PropertiesJson';";

            if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using SqliteCommand addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = "ALTER TABLE KernelPowerEvents ADD COLUMN PropertiesJson TEXT NULL;";
                addColumnCommand.ExecuteNonQuery();
            }
        }

        private static void EnsureProcessColumns(SqliteConnection connection)
        {
            (string Name, string Definition)[] columns =
            {
                ("CpuDurationTicks", "INTEGER NULL"),
                ("CpuUsagePercent", "REAL NULL"),
            };

            foreach ((string name, string definition) in columns)
            {
                using SqliteCommand columnExistsCommand = connection.CreateCommand();
                columnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Processes') WHERE name = $name;";
                columnExistsCommand.Parameters.AddWithValue("$name", name);

                if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                using SqliteCommand addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = $"ALTER TABLE Processes ADD COLUMN {name} {definition};";
                addColumnCommand.ExecuteNonQuery();
            }
        }

        private static string ToUtcTimestamp(DateTime timestamp)
        {
            return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        private static object ToDbValue(object? value)
        {
            return value ?? DBNull.Value;
        }

        private static string? ToHex(object? value)
        {
            if (value is null)
            {
                return null;
            }

            ulong number = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            return number.ToString("X16", CultureInfo.InvariantCulture);
        }
    }
}
