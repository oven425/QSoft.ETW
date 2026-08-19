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
        private const int AddressBucketShift = 24;

        private SqliteConnection? _connection;
        private SqliteTransaction? _transaction;
        private int _batchedWriteCount;
        private SqliteCommand? _writeImageLoadCommand;
        private SqliteCommand? _writeImageUnloadCommand;
        private SqliteCommand? _writeProcessStartCommand;
        private SqliteCommand? _writeProcessStopCommand;
        private SqliteCommand? _writeWmiActivityCommand;
        private SqliteCommand? _writeWmiActivity11Command;
        private SqliteCommand? _writeEnergyEstimationEngineCommand;
        private SqliteCommand? _writeEnergyEstimationQueryStatsCommand;
        private SqliteCommand? _writeEnergyEstimationCpuPowerCommand;
        private SqliteCommand? _writeEnergyEstimationEnergyDeltaCommand;
        private SqliteCommand? _writeThreadEventCommand;
        private SqliteCommand? _writeCpuProfileSampleCommand;
        private SqliteCommand? _writeDpcCommand;
        private SqliteCommand? _writeInterruptCommand;
        private SqliteCommand? _writeThreadLifetimeCommand;
        private SqliteCommand? _writePowerMeterPollingEvent4Command;
        private SqliteCommand? _writeKernelAcpiTemperatureNotificationCommand;
        private SqliteCommand? _writeKernelAcpiAmlMethodTraceCommand;
        private SqliteCommand? _writeKernelAcpiTemperatureChangeCommand;
        private SqliteCommand? _writeKernelAcpiFrequentAmlMethodCommand;
        private SqliteCommand? _writeDiskIoOperationCommand;

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
                          UserSID TEXT NULL,
                          StartedAtUtc TEXT NOT NULL,
                          EndedAtUtc TEXT NULL,
                          CpuDurationTicks INTEGER NULL,
                          CpuUsagePercent REAL NULL,
                          UNIQUE (ProcessId, StartedAtUtc)
                      );

                      CREATE INDEX IF NOT EXISTS IX_Processes_ActiveProcess
                       ON Processes (ProcessId, EndedAtUtc, StartedAtUtc DESC);

                       CREATE TABLE IF NOT EXISTS WmiActivityEvents_24
                       (
                           WmiActivityEventId INTEGER PRIMARY KEY,
                           ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           NamespaceName TEXT NOT NULL,
                           ClientProcessId INTEGER NULL,
                           IntervalMs INTEGER NULL,
                           Query TEXT NULL,
                           GroupOperationId INTEGER NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_24_NamespaceTimestamp
                        ON WmiActivityEvents_24 (NamespaceName, TimestampUtc);

                       CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_24_ProcessRecord
                        ON WmiActivityEvents_24 (ProcessRecordId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS WmiActivityEvents_11
                       (
                           WmiActivityEvent11Id INTEGER PRIMARY KEY,
                           ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           CorrelationId TEXT NULL,
                           GroupOperationId INTEGER NULL,
                           OperationId INTEGER NULL,
                           Operation TEXT NULL,
                           ClientMachine TEXT NULL,
                           ClientMachineFQDN TEXT NULL,
                           UserName TEXT NULL,
                           ClientProcessId INTEGER NULL,
                           ClientProcessCreationTime INTEGER NULL,
                           NamespaceName TEXT NULL,
                           IsLocal INTEGER NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_11_NamespaceTimestamp
                       ON WmiActivityEvents_11 (NamespaceName, TimestampUtc);

                       CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_11_ProcessRecord
                       ON WmiActivityEvents_11 (ProcessRecordId, TimestampUtc);

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

                       CREATE TABLE IF NOT EXISTS KernelAcpiTemperatureNotifications
                       (
                           KernelAcpiTemperatureNotificationId INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           ThermalZoneDeviceInstanceLength INTEGER NOT NULL,
                           ThermalZoneDeviceInstance TEXT NOT NULL,
                           _TMP INTEGER NOT NULL,
                           _PSV INTEGER NOT NULL,
                           _AC0 INTEGER NOT NULL,
                           _AC1 INTEGER NOT NULL,
                           _AC2 INTEGER NOT NULL,
                           _AC3 INTEGER NOT NULL,
                           _AC4 INTEGER NOT NULL,
                           _AC5 INTEGER NOT NULL,
                           _AC6 INTEGER NOT NULL,
                           _AC7 INTEGER NOT NULL,
                           _AC8 INTEGER NOT NULL,
                           _AC9 INTEGER NOT NULL,
                           _HOT INTEGER NOT NULL,
                           _CRT INTEGER NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_KernelAcpiTemperatureNotifications_ProcessTimestamp
                       ON KernelAcpiTemperatureNotifications (ProcessId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS KernelAcpiAmlMethodTraces
                       (
                           KernelAcpiAmlMethodTraceId INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           AmlMethodNameLength INTEGER NOT NULL,
                           AmlMethodName TEXT NOT NULL,
                           AmlMethodState INTEGER NOT NULL,
                           AmlElapsedTime TEXT NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_KernelAcpiAmlMethodTraces_ProcessTimestamp
                       ON KernelAcpiAmlMethodTraces (ProcessId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS KernelAcpiTemperatureChanges
                       (
                           KernelAcpiTemperatureChangeId INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           ThermalZoneDeviceInstanceLength INTEGER NOT NULL,
                           ThermalZoneDeviceInstance TEXT NOT NULL,
                           Temperature INTEGER NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_KernelAcpiTemperatureChanges_ProcessTimestamp
                       ON KernelAcpiTemperatureChanges (ProcessId, TimestampUtc);

                       CREATE TABLE IF NOT EXISTS KernelAcpiFrequentAmlMethods
                       (
                           KernelAcpiFrequentAmlMethodId INTEGER PRIMARY KEY,
                           TimestampUtc TEXT NOT NULL,
                           EventId INTEGER NOT NULL,
                           Version INTEGER NOT NULL,
                           Opcode INTEGER NOT NULL,
                           ProcessId INTEGER NOT NULL,
                           ThreadId INTEGER NOT NULL,
                           AmlMethodNameLength INTEGER NOT NULL,
                           AmlMethodName TEXT NOT NULL,
                           Frequency TEXT NOT NULL
                       );

                       CREATE INDEX IF NOT EXISTS IX_KernelAcpiFrequentAmlMethods_ProcessTimestamp
                       ON KernelAcpiFrequentAmlMethods (ProcessId, TimestampUtc);

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
                                  ImageLoadId INTEGER NULL REFERENCES ImageLoads(ImageLoadId) ON DELETE RESTRICT,
                                  TimestampUtc TEXT NOT NULL,
                                  ProcessorNumber INTEGER NOT NULL,
                                  ProcessId INTEGER NOT NULL,
                                  ThreadId INTEGER NOT NULL,
                                  InstructionPointer TEXT NULL,
                                  ModuleFileName TEXT NULL,
                                  ModuleRva TEXT NULL
                              );

                              CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_Timestamp
                              ON CpuProfileSamples (TimestampUtc);

                              CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_ProcessRecordTimestamp
                              ON CpuProfileSamples (ProcessRecordId, TimestampUtc);

                              CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_ProcessTimestamp
                               ON CpuProfileSamples (ProcessId, TimestampUtc);

                              CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_ImageLoad
                              ON CpuProfileSamples (ImageLoadId, TimestampUtc);

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
                              ON InterruptEvents (ProcessorNumber, TimestampUtc);

                              CREATE TABLE IF NOT EXISTS DiskIoOperations
                              (
                                  DiskIoOperationId INTEGER PRIMARY KEY,
                                  ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                                  TimestampUtc TEXT NOT NULL,
                                  StartedAtUtc TEXT NULL,
                                  CompletedAtUtc TEXT NULL,
                                  EventId INTEGER NOT NULL,
                                  Version INTEGER NOT NULL,
                                  Opcode INTEGER NOT NULL,
                                  ProcessId INTEGER NOT NULL,
                                  ThreadId INTEGER NOT NULL,
                                  CorrelationId TEXT NULL,
                                  TransferSize INTEGER NULL,
                                  Operation TEXT NULL,
                                  LatencyMilliseconds REAL NULL,
                                  MatchStatus TEXT NOT NULL
                              );

                              CREATE INDEX IF NOT EXISTS IX_DiskIoOperations_ProcessRecordTimestamp
                              ON DiskIoOperations (ProcessRecordId, TimestampUtc);

                              CREATE INDEX IF NOT EXISTS IX_DiskIoOperations_MatchStatusTimestamp
                              ON DiskIoOperations (MatchStatus, TimestampUtc);

                              CREATE TABLE IF NOT EXISTS PowerMeterPollingEvents_4
                              (
                                  PowerMeterPollingEvent4Id INTEGER PRIMARY KEY,
                                  TimestampUtc TEXT NOT NULL,
                                  EventId INTEGER NOT NULL,
                                  Version INTEGER NOT NULL,
                                  Opcode INTEGER NOT NULL,
                                  MeterId TEXT NOT NULL,
                                  AbsoluteEnergy INTEGER NOT NULL,
                                  AbsoluteTime TEXT NOT NULL
                              );

                              CREATE INDEX IF NOT EXISTS IX_PowerMeterPollingEvents_4_MeterTimestamp
                              ON PowerMeterPollingEvents_4 (MeterId, TimestampUtc);";
                       command.ExecuteNonQuery();
            }

            RemoveLegacyPowerMeterPollingTables(connection);
            EnsurePowerMeterPollingEvent4EnergyIsInteger(connection);
            EnsureEnergyEstimationEngineColumns(connection);
            EnsureEnergyEstimationEngineEnergyColumnsAreIntegers(connection);
            EnsureEnergyEstimationEngineTablesWithoutProcessColumns(connection);
            EnsureThreadEventColumns(connection);
            EnsureWmiActivityColumns(connection);
            EnsureCpuProfileSampleColumns(connection);

            SqliteTransaction transaction = connection.BeginTransaction();
            _writeImageLoadCommand = CreateWriteImageLoadCommand(connection, transaction);
            _writeImageUnloadCommand = CreateWriteImageUnloadCommand(connection, transaction);
            _writeProcessStartCommand = CreateWriteProcessStartCommand(connection, transaction);
            _writeProcessStopCommand = CreateWriteProcessStopCommand(connection, transaction);
            _writeWmiActivityCommand = CreateWriteWmiActivityCommand(connection, transaction);
            _writeWmiActivity11Command = CreateWriteWmiActivity11Command(connection, transaction);
            _writeEnergyEstimationEngineCommand = CreateWriteEnergyEstimationEngineCommand(connection, transaction);
            _writeEnergyEstimationQueryStatsCommand = CreateWriteEnergyEstimationQueryStatsCommand(connection, transaction);
            _writeEnergyEstimationEnergyDeltaCommand = CreateWriteEnergyEstimationEnergyDeltaCommand(connection, transaction);
            _writeEnergyEstimationCpuPowerCommand = CreateWriteEnergyEstimationCpuPowerCommand(connection, transaction);
            _writeThreadEventCommand = CreateWriteThreadEventCommand(connection, transaction);
            _writeCpuProfileSampleCommand = CreateWriteCpuProfileSampleCommand(connection, transaction);
            _writeDpcCommand = CreateWriteDpcCommand(connection, transaction);
            _writeInterruptCommand = CreateWriteInterruptCommand(connection, transaction);
            _writeThreadLifetimeCommand = CreateWriteThreadLifetimeCommand(connection, transaction);
            _writePowerMeterPollingEvent4Command = CreateWritePowerMeterPollingEvent4Command(connection, transaction);
            _writeKernelAcpiTemperatureNotificationCommand = CreateWriteKernelAcpiTemperatureNotificationCommand(connection, transaction);
            _writeKernelAcpiAmlMethodTraceCommand = CreateWriteKernelAcpiAmlMethodTraceCommand(connection, transaction);
            _writeKernelAcpiTemperatureChangeCommand = CreateWriteKernelAcpiTemperatureChangeCommand(connection, transaction);
            _writeKernelAcpiFrequentAmlMethodCommand = CreateWriteKernelAcpiFrequentAmlMethodCommand(connection, transaction);
            _writeDiskIoOperationCommand = CreateWriteDiskIoOperationCommand(connection, transaction);
            _connection = connection;
            _transaction = transaction;
            _batchedWriteCount = 0;
        }

        public void WriteImageLoad(in ImageLoadEventInfo data)
        {
            SqliteCommand command = _writeImageLoadCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$imageBase"].Value = ToDbValue(ToHex(data.ImageBase));
            command.Parameters["$imageSize"].Value = ToDbValue(ToHex(data.ImageSize));
            command.Parameters["$imageCheckSum"].Value = ToDbValue(data.ImageCheckSum);
            command.Parameters["$timeDateStamp"].Value = ToDbValue(data.TimeDateStamp);
            command.Parameters["$defaultBase"].Value = ToDbValue(data.DefaultBase);
            command.Parameters["$fileName"].Value = data.FileName ?? string.Empty;
            command.Parameters["$loadedAtUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.ExecuteNonQuery();
        }

        public void WriteWmiActivity(in WmiActivityEventInfo_24 data)
        {
            SqliteCommand command = _writeWmiActivityCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$namespaceName"].Value = ToDbValue(data.NamespaceName);
            command.Parameters["$clientProcessId"].Value = Convert.ToInt64(data.ClientProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$intervalMs"].Value = Convert.ToInt64(data.IntervalMs, CultureInfo.InvariantCulture);
            command.Parameters["$query"].Value = ToDbValue(data.Query);
            command.Parameters["$groupOperationId"].Value = Convert.ToInt64(data.GroupOperationId, CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }

        public void WriteWmiActivity(in WmiActivityEventInfo_11 data)
        {
            SqliteCommand command = _writeWmiActivity11Command ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$correlationId"].Value = ToDbValue(data.CorrelationId);
            command.Parameters["$groupOperationId"].Value = Convert.ToInt64(data.GroupOperationId, CultureInfo.InvariantCulture);
            command.Parameters["$operationId"].Value = Convert.ToInt64(data.OperationId, CultureInfo.InvariantCulture);
            command.Parameters["$operation"].Value = ToDbValue(data.Operation);
            command.Parameters["$clientMachine"].Value = ToDbValue(data.ClientMachine);
            command.Parameters["$clientMachineFqdn"].Value = ToDbValue(data.ClientMachineFQDN);
            command.Parameters["$userName"].Value = ToDbValue(data.User);
            command.Parameters["$clientProcessId"].Value = Convert.ToInt64(data.ClientProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$clientProcessCreationTime"].Value = checked((long)data.ClientProcessCreationTime);
            command.Parameters["$namespaceName"].Value = ToDbValue(data.NamespaceName);
            command.Parameters["$isLocal"].Value = data.IsLocal ? 1 : 0;
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
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

        public void WritePowerMeterPollingEvent_4(in PowerMeterPollingEventInfo_4 data)
        {
            SqliteCommand command = _writePowerMeterPollingEvent4Command ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$meterId"].Value = ToHex(data.MeterId) ?? string.Empty;
            command.Parameters["$absoluteEnergy"].Value = checked((long)data.AbsoluteEnergy);
            command.Parameters["$absoluteTime"].Value = data.AbsoluteTime.ToString(CultureInfo.InvariantCulture);
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

        public void WriteKernelAcpiTemperatureNotification(in KernelAcpiEventInfo_TemperatureNotification data)
        {
            SqliteCommand command = _writeKernelAcpiTemperatureNotificationCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$thermalZoneDeviceInstanceLength"].Value = data.ThermalZoneDeviceInstanceLength;
            command.Parameters["$thermalZoneDeviceInstance"].Value = data.ThermalZoneDeviceInstance ?? string.Empty;
            command.Parameters["$tmp"].Value = Convert.ToInt64(data._TMP, CultureInfo.InvariantCulture);
            command.Parameters["$psv"].Value = Convert.ToInt64(data._PSV, CultureInfo.InvariantCulture);
            command.Parameters["$ac0"].Value = Convert.ToInt64(data._AC0, CultureInfo.InvariantCulture);
            command.Parameters["$ac1"].Value = Convert.ToInt64(data._AC1, CultureInfo.InvariantCulture);
            command.Parameters["$ac2"].Value = Convert.ToInt64(data._AC2, CultureInfo.InvariantCulture);
            command.Parameters["$ac3"].Value = Convert.ToInt64(data._AC3, CultureInfo.InvariantCulture);
            command.Parameters["$ac4"].Value = Convert.ToInt64(data._AC4, CultureInfo.InvariantCulture);
            command.Parameters["$ac5"].Value = Convert.ToInt64(data._AC5, CultureInfo.InvariantCulture);
            command.Parameters["$ac6"].Value = Convert.ToInt64(data._AC6, CultureInfo.InvariantCulture);
            command.Parameters["$ac7"].Value = Convert.ToInt64(data._AC7, CultureInfo.InvariantCulture);
            command.Parameters["$ac8"].Value = Convert.ToInt64(data._AC8, CultureInfo.InvariantCulture);
            command.Parameters["$ac9"].Value = Convert.ToInt64(data._AC9, CultureInfo.InvariantCulture);
            command.Parameters["$hot"].Value = Convert.ToInt64(data._HOT, CultureInfo.InvariantCulture);
            command.Parameters["$crt"].Value = Convert.ToInt64(data._CRT, CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteKernelAcpiAmlMethodTrace(in KernelAcpiEventInfo_AmlMethodTrace data)
        {
            SqliteCommand command = _writeKernelAcpiAmlMethodTraceCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$amlMethodNameLength"].Value = data.AmlMethodNameLength;
            command.Parameters["$amlMethodName"].Value = data.AmlMethodName ?? string.Empty;
            command.Parameters["$amlMethodState"].Value = data.AmlMethodState;
            command.Parameters["$amlElapsedTime"].Value = data.AmlElapsedTime.ToString(CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteKernelAcpiTemperatureChange(in KernelAcpiEventInfo_TemperatureChange data)
        {
            SqliteCommand command = _writeKernelAcpiTemperatureChangeCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$thermalZoneDeviceInstanceLength"].Value = data.ThermalZoneDeviceInstanceLength;
            command.Parameters["$thermalZoneDeviceInstance"].Value = data.ThermalZoneDeviceInstance ?? string.Empty;
            command.Parameters["$temperature"].Value = Convert.ToInt64(data.Temperature, CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteKernelAcpiFrequentAmlMethod(in KernelAcpiEventInfo_FrequentAmlMethod data)
        {
            SqliteCommand command = _writeKernelAcpiFrequentAmlMethodCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$timestampUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$eventId"].Value = data.EventId;
            command.Parameters["$version"].Value = data.Version;
            command.Parameters["$opcode"].Value = data.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(data.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$amlMethodNameLength"].Value = data.AmlMethodNameLength;
            command.Parameters["$amlMethodName"].Value = data.AmlMethodName ?? string.Empty;
            command.Parameters["$frequency"].Value = data.Frequency.ToString(CultureInfo.InvariantCulture);
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

        public void WriteDiskIoOperation(DiskIoOperation operation)
        {
            SqliteCommand command = _writeDiskIoOperationCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            string timestampUtc = ToUtcTimestamp(operation.Timestamp);
            string attributionTimestampUtc = operation.StartedAt is DateTime startedAt
                ? ToUtcTimestamp(startedAt)
                : timestampUtc;
            command.Parameters["$timestampUtc"].Value = timestampUtc;
            command.Parameters["$processAttributionTimestampUtc"].Value = attributionTimestampUtc;
            command.Parameters["$startedAtUtc"].Value = operation.StartedAt is DateTime operationStartedAt ? ToUtcTimestamp(operationStartedAt) : DBNull.Value;
            command.Parameters["$completedAtUtc"].Value = operation.CompletedAt is DateTime completedAt ? ToUtcTimestamp(completedAt) : DBNull.Value;
            command.Parameters["$eventId"].Value = operation.EventId;
            command.Parameters["$version"].Value = operation.Version;
            command.Parameters["$opcode"].Value = operation.Opcode;
            command.Parameters["$processId"].Value = Convert.ToInt64(operation.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$threadId"].Value = Convert.ToInt64(operation.ThreadId, CultureInfo.InvariantCulture);
            command.Parameters["$correlationId"].Value = ToDbValue(operation.CorrelationId);
            command.Parameters["$transferSize"].Value = operation.TransferSize is ulong transferSize
                ? checked((long)Math.Min(transferSize, long.MaxValue))
                : DBNull.Value;
            command.Parameters["$operation"].Value = ToDbValue(operation.Operation);
            command.Parameters["$latencyMilliseconds"].Value = ToDbValue(operation.LatencyMilliseconds);
            command.Parameters["$matchStatus"].Value = operation.MatchStatus;
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteImageUnLoad(in ImageLoadEventInfo data)
        {
            SqliteCommand command = _writeImageUnloadCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$unloadedAtUtc"].Value = ToUtcTimestamp(data.Timestamp);
            command.Parameters["$processId"].Value = Convert.ToInt64(data.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$imageBase"].Value = ToDbValue(ToHex(data.ImageBase));
            command.ExecuteNonQuery();
        }

        public void WriteProcessStart(ProcessInfo process)
        {
            SqliteCommand command = _writeProcessStartCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$processId"].Value = Convert.ToInt64(process.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$parentProcessId"].Value = Convert.ToInt64(process.ParentId, CultureInfo.InvariantCulture);
            command.Parameters["$imageFileName"].Value = process.ImageFileName ?? string.Empty;
            command.Parameters["$commandLine"].Value = process.CommandLine ?? string.Empty;
            command.Parameters["$userSID"].Value = ToDbValue(process.UserSID);
            command.Parameters["$startedAtUtc"].Value = ToUtcTimestamp(process.TimeStamp);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
        }

        public void WriteProcessStop(ProcessInfo process, DateTime startedAt, long? cpuDurationTicks = null, double? cpuUsagePercent = null)
        {
            if ((cpuDurationTicks is null) != (cpuUsagePercent is null))
            {
                throw new ArgumentException("CPU 總時間與使用率必須同時提供。");
            }

            if (cpuDurationTicks < 0 || cpuUsagePercent < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cpuDurationTicks), "CPU 使用資料不可為負值。");
            }

            SqliteCommand command = _writeProcessStopCommand ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            command.Parameters["$endedAtUtc"].Value = ToUtcTimestamp(process.TimeStamp);
            command.Parameters["$processId"].Value = Convert.ToInt64(process.ProcessId, CultureInfo.InvariantCulture);
            command.Parameters["$startedAtUtc"].Value = ToUtcTimestamp(startedAt);
            command.Parameters["$cpuDurationTicks"].Value = ToDbValue(cpuDurationTicks);
            command.Parameters["$cpuUsagePercent"].Value = ToDbValue(cpuUsagePercent);
            command.ExecuteNonQuery();
            CommitWriteBatchIfNeeded();
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
            ResolveCpuProfileSampleModules();
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
            _writeWmiActivity11Command?.Dispose();
            _writeWmiActivity11Command = null;
            _writeEnergyEstimationEngineCommand?.Dispose();
            _writeEnergyEstimationEngineCommand = null;
            _writeEnergyEstimationQueryStatsCommand?.Dispose();
            _writeEnergyEstimationQueryStatsCommand = null;
            _writeEnergyEstimationEnergyDeltaCommand?.Dispose();
            _writeEnergyEstimationEnergyDeltaCommand = null;
            _writeEnergyEstimationCpuPowerCommand?.Dispose();
            _writeEnergyEstimationCpuPowerCommand = null;
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
            _writePowerMeterPollingEvent4Command?.Dispose();
            _writePowerMeterPollingEvent4Command = null;
            _writeKernelAcpiTemperatureNotificationCommand?.Dispose();
            _writeKernelAcpiTemperatureNotificationCommand = null;
            _writeKernelAcpiAmlMethodTraceCommand?.Dispose();
            _writeKernelAcpiAmlMethodTraceCommand = null;
            _writeKernelAcpiTemperatureChangeCommand?.Dispose();
            _writeKernelAcpiTemperatureChangeCommand = null;
            _writeKernelAcpiFrequentAmlMethodCommand?.Dispose();
            _writeKernelAcpiFrequentAmlMethodCommand = null;
            _writeDiskIoOperationCommand?.Dispose();
            _writeDiskIoOperationCommand = null;
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
                _writeWmiActivity11Command,
                _writeEnergyEstimationEngineCommand,
                _writeEnergyEstimationQueryStatsCommand,
                _writeEnergyEstimationEnergyDeltaCommand,
                _writeEnergyEstimationCpuPowerCommand,
                _writeThreadEventCommand,
                _writeCpuProfileSampleCommand,
                _writeDpcCommand,
                _writeInterruptCommand,
                _writeThreadLifetimeCommand,
                _writePowerMeterPollingEvent4Command,
                _writeKernelAcpiTemperatureNotificationCommand,
                _writeKernelAcpiAmlMethodTraceCommand,
                _writeKernelAcpiTemperatureChangeCommand,
                _writeKernelAcpiFrequentAmlMethodCommand,
                _writeDiskIoOperationCommand,
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

        private void ResolveCpuProfileSampleModules()
        {
            SqliteConnection connection = _connection ?? throw new InvalidOperationException("請先開啟 SQLite 資料庫。");
            SqliteTransaction transaction = _transaction ?? throw new InvalidOperationException("SQLite 交易尚未建立。");
            Dictionary<long, List<LoadedImage>> imagesByProcessRecord = [];
            Dictionary<long, List<LoadedImage>> imagesByProcessId = [];
            Dictionary<ulong, List<LoadedImage>> globalImagesByAddressBucket = [];

            using (SqliteCommand imageCommand = connection.CreateCommand())
            {
                imageCommand.Transaction = transaction;
                imageCommand.CommandText =
                    @"SELECT ImageLoadId, ProcessRecordId, ProcessId, ImageBase, ImageSize, FileName, LoadedAtUtc, UnloadedAtUtc
                      FROM ImageLoads
                      WHERE ImageBase IS NOT NULL
                        AND ImageSize IS NOT NULL;";

                using SqliteDataReader reader = imageCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (!TryGetHexAddress(reader, 3, out ulong imageBase) ||
                        !TryGetHexAddress(reader, 4, out ulong imageSize) ||
                        imageSize == 0)
                    {
                        continue;
                    }

                    var image = new LoadedImage(
                        reader.GetInt64(0),
                        imageBase,
                        imageSize,
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7));

                    AddLoadedImageToAddressBuckets(globalImagesByAddressBucket, image);
                    long processId = reader.GetInt64(2);
                    AddLoadedImage(imagesByProcessId, processId, image);
                    if (!reader.IsDBNull(1))
                    {
                        AddLoadedImage(imagesByProcessRecord, reader.GetInt64(1), image);
                    }
                }
            }

            using SqliteCommand updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                @"UPDATE CpuProfileSamples
                  SET ImageLoadId = $imageLoadId,
                      ModuleFileName = $moduleFileName,
                      ModuleRva = $moduleRva
                  WHERE CpuProfileSampleId = $cpuProfileSampleId;";
            updateCommand.Parameters.Add("$imageLoadId", SqliteType.Integer);
            updateCommand.Parameters.Add("$moduleFileName", SqliteType.Text);
            updateCommand.Parameters.Add("$moduleRva", SqliteType.Text);
            updateCommand.Parameters.Add("$cpuProfileSampleId", SqliteType.Integer);
            updateCommand.Prepare();

            using SqliteCommand sampleCommand = connection.CreateCommand();
            sampleCommand.Transaction = transaction;
            sampleCommand.CommandText =
                @"SELECT CpuProfileSampleId, ProcessRecordId, ProcessId, TimestampUtc, InstructionPointer
                  FROM CpuProfileSamples
                  WHERE InstructionPointer IS NOT NULL;";

            using SqliteDataReader sampleReader = sampleCommand.ExecuteReader();
            while (sampleReader.Read())
            {
                if (!TryGetHexAddress(sampleReader, 4, out ulong instructionPointer))
                {
                    continue;
                }

                string timestampUtc = sampleReader.GetString(3);
                List<LoadedImage>? images = null;
                if (!sampleReader.IsDBNull(1))
                {
                    imagesByProcessRecord.TryGetValue(sampleReader.GetInt64(1), out images);
                }

                if (images is null)
                {
                    imagesByProcessId.TryGetValue(sampleReader.GetInt64(2), out images);
                }

                if (images is null)
                {
                    LoadedImage? resolvedGlobalImage = FindUniqueLoadedImage(globalImagesByAddressBucket, timestampUtc, instructionPointer);
                    if (resolvedGlobalImage is not LoadedImage globalImage)
                    {
                        continue;
                    }

                    UpdateCpuProfileSampleModule(updateCommand, sampleReader.GetInt64(0), globalImage, instructionPointer);
                    continue;
                }

                LoadedImage? resolvedImage = FindLoadedImage(images, timestampUtc, instructionPointer);
                if (resolvedImage is not LoadedImage image)
                {
                    continue;
                }

                UpdateCpuProfileSampleModule(updateCommand, sampleReader.GetInt64(0), image, instructionPointer);
            }
        }

        private static void UpdateCpuProfileSampleModule(
            SqliteCommand updateCommand,
            long cpuProfileSampleId,
            LoadedImage image,
            ulong instructionPointer)
        {
            updateCommand.Parameters["$imageLoadId"].Value = image.ImageLoadId;
            updateCommand.Parameters["$moduleFileName"].Value = image.FileName;
            updateCommand.Parameters["$moduleRva"].Value = ToHex(instructionPointer - image.ImageBase);
            updateCommand.Parameters["$cpuProfileSampleId"].Value = cpuProfileSampleId;
            updateCommand.ExecuteNonQuery();
        }

        private static void AddLoadedImage(Dictionary<long, List<LoadedImage>> imagesByProcess, long processKey, LoadedImage image)
        {
            if (!imagesByProcess.TryGetValue(processKey, out List<LoadedImage>? images))
            {
                images = [];
                imagesByProcess.Add(processKey, images);
            }

            images.Add(image);
        }

        private static LoadedImage? FindLoadedImage(List<LoadedImage> images, string timestampUtc, ulong instructionPointer)
        {
            LoadedImage? match = null;
            foreach (LoadedImage image in images)
            {
                if (string.CompareOrdinal(image.LoadedAtUtc, timestampUtc) > 0 ||
                    (image.UnloadedAtUtc is string unloadedAtUtc && string.CompareOrdinal(unloadedAtUtc, timestampUtc) < 0) ||
                    instructionPointer < image.ImageBase ||
                    instructionPointer - image.ImageBase >= image.ImageSize)
                {
                    continue;
                }

                if (match is null || string.CompareOrdinal(image.LoadedAtUtc, match.Value.LoadedAtUtc) > 0)
                {
                    match = image;
                }
            }

            return match;
        }

        private static LoadedImage? FindUniqueLoadedImage(
            Dictionary<ulong, List<LoadedImage>> imagesByAddressBucket,
            string timestampUtc,
            ulong instructionPointer)
        {
            ulong addressBucket = instructionPointer >> AddressBucketShift;
            if (!imagesByAddressBucket.TryGetValue(addressBucket, out List<LoadedImage>? bucketImages))
            {
                return null;
            }

            LoadedImage? match = null;
            foreach (LoadedImage image in bucketImages)
            {
                if (string.CompareOrdinal(image.LoadedAtUtc, timestampUtc) > 0 ||
                    (image.UnloadedAtUtc is string unloadedAtUtc && string.CompareOrdinal(unloadedAtUtc, timestampUtc) < 0) ||
                    instructionPointer < image.ImageBase ||
                    instructionPointer - image.ImageBase >= image.ImageSize)
                {
                    continue;
                }

                if (match is not null)
                {
                    return null;
                }

                match = image;
            }

            return match;
        }

        private static void AddLoadedImageToAddressBuckets(
            Dictionary<ulong, List<LoadedImage>> imagesByAddressBucket,
            LoadedImage image)
        {
            ulong firstBucket = image.ImageBase >> AddressBucketShift;
            ulong lastAddress = checked(image.ImageBase + image.ImageSize - 1);
            ulong lastBucket = lastAddress >> AddressBucketShift;
            for (ulong bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                if (!imagesByAddressBucket.TryGetValue(bucket, out List<LoadedImage>? images))
                {
                    images = [];
                    imagesByAddressBucket.Add(bucket, images);
                }

                images.Add(image);
            }
        }

        private static bool TryGetHexAddress(SqliteDataReader reader, int ordinal, out ulong value)
        {
            value = 0;
            if (reader.IsDBNull(ordinal))
            {
                return false;
            }

            string text = reader.GetString(ordinal);
            return text.Length == 16 &&
                   ulong.TryParse(
                       text,
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out value);
        }

        private readonly record struct LoadedImage(
            long ImageLoadId,
            ulong ImageBase,
            ulong ImageSize,
            string FileName,
            string LoadedAtUtc,
            string? UnloadedAtUtc);

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

        private static SqliteCommand CreateWriteDiskIoOperationCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO DiskIoOperations
                    (ProcessRecordId, TimestampUtc, StartedAtUtc, CompletedAtUtc, EventId, Version, Opcode, ProcessId, ThreadId, CorrelationId, TransferSize, Operation, LatencyMilliseconds, MatchStatus)
                  VALUES
                    (
                        (
                            SELECT ProcessRecordId
                            FROM Processes
                            WHERE ProcessId = $processId
                              AND StartedAtUtc <= $processAttributionTimestampUtc
                              AND (EndedAtUtc IS NULL OR EndedAtUtc >= $processAttributionTimestampUtc)
                            ORDER BY StartedAtUtc DESC, ProcessRecordId DESC
                            LIMIT 1
                        ),
                        $timestampUtc, $startedAtUtc, $completedAtUtc, $eventId, $version, $opcode, $processId, $threadId, $correlationId, $transferSize, $operation, $latencyMilliseconds, $matchStatus
                    );";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$processAttributionTimestampUtc", SqliteType.Text);
            command.Parameters.Add("$startedAtUtc", SqliteType.Text);
            command.Parameters.Add("$completedAtUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$correlationId", SqliteType.Text);
            command.Parameters.Add("$transferSize", SqliteType.Integer);
            command.Parameters.Add("$operation", SqliteType.Text);
            command.Parameters.Add("$latencyMilliseconds", SqliteType.Real);
            command.Parameters.Add("$matchStatus", SqliteType.Text);
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

        private static SqliteCommand CreateWritePowerMeterPollingEvent4Command(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO PowerMeterPollingEvents_4
                    (TimestampUtc, EventId, Version, Opcode, MeterId, AbsoluteEnergy, AbsoluteTime)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $meterId, $absoluteEnergy, $absoluteTime);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$meterId", SqliteType.Text);
            command.Parameters.Add("$absoluteEnergy", SqliteType.Integer);
            command.Parameters.Add("$absoluteTime", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteKernelAcpiTemperatureNotificationCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO KernelAcpiTemperatureNotifications
                    (TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, ThermalZoneDeviceInstanceLength, ThermalZoneDeviceInstance, _TMP, _PSV, _AC0, _AC1, _AC2, _AC3, _AC4, _AC5, _AC6, _AC7, _AC8, _AC9, _HOT, _CRT)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $thermalZoneDeviceInstanceLength, $thermalZoneDeviceInstance, $tmp, $psv, $ac0, $ac1, $ac2, $ac3, $ac4, $ac5, $ac6, $ac7, $ac8, $ac9, $hot, $crt);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$thermalZoneDeviceInstanceLength", SqliteType.Integer);
            command.Parameters.Add("$thermalZoneDeviceInstance", SqliteType.Text);
            command.Parameters.Add("$tmp", SqliteType.Integer);
            command.Parameters.Add("$psv", SqliteType.Integer);
            command.Parameters.Add("$ac0", SqliteType.Integer);
            command.Parameters.Add("$ac1", SqliteType.Integer);
            command.Parameters.Add("$ac2", SqliteType.Integer);
            command.Parameters.Add("$ac3", SqliteType.Integer);
            command.Parameters.Add("$ac4", SqliteType.Integer);
            command.Parameters.Add("$ac5", SqliteType.Integer);
            command.Parameters.Add("$ac6", SqliteType.Integer);
            command.Parameters.Add("$ac7", SqliteType.Integer);
            command.Parameters.Add("$ac8", SqliteType.Integer);
            command.Parameters.Add("$ac9", SqliteType.Integer);
            command.Parameters.Add("$hot", SqliteType.Integer);
            command.Parameters.Add("$crt", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteKernelAcpiAmlMethodTraceCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO KernelAcpiAmlMethodTraces
                    (TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, AmlMethodNameLength, AmlMethodName, AmlMethodState, AmlElapsedTime)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $amlMethodNameLength, $amlMethodName, $amlMethodState, $amlElapsedTime);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$amlMethodNameLength", SqliteType.Integer);
            command.Parameters.Add("$amlMethodName", SqliteType.Text);
            command.Parameters.Add("$amlMethodState", SqliteType.Integer);
            command.Parameters.Add("$amlElapsedTime", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteKernelAcpiTemperatureChangeCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO KernelAcpiTemperatureChanges
                    (TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, ThermalZoneDeviceInstanceLength, ThermalZoneDeviceInstance, Temperature)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $thermalZoneDeviceInstanceLength, $thermalZoneDeviceInstance, $temperature);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$thermalZoneDeviceInstanceLength", SqliteType.Integer);
            command.Parameters.Add("$thermalZoneDeviceInstance", SqliteType.Text);
            command.Parameters.Add("$temperature", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteKernelAcpiFrequentAmlMethodCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO KernelAcpiFrequentAmlMethods
                    (TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, AmlMethodNameLength, AmlMethodName, Frequency)
                  VALUES
                    ($timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $amlMethodNameLength, $amlMethodName, $frequency);";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$amlMethodNameLength", SqliteType.Integer);
            command.Parameters.Add("$amlMethodName", SqliteType.Text);
            command.Parameters.Add("$frequency", SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteWmiActivityCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO WmiActivityEvents_24
                    (ProcessRecordId, TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, NamespaceName, ClientProcessId, IntervalMs, Query, GroupOperationId)
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
                        $timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $namespaceName, $clientProcessId, $intervalMs, $query, $groupOperationId
                    );";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$namespaceName", SqliteType.Text);
            command.Parameters.Add("$clientProcessId", SqliteType.Integer);
            command.Parameters.Add("$intervalMs", SqliteType.Integer);
            command.Parameters.Add("$query", SqliteType.Text);
            command.Parameters.Add("$groupOperationId", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static SqliteCommand CreateWriteWmiActivity11Command(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO WmiActivityEvents_11
                    (ProcessRecordId, TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, CorrelationId, GroupOperationId, OperationId, Operation, ClientMachine, ClientMachineFQDN, UserName, ClientProcessId, ClientProcessCreationTime, NamespaceName, IsLocal)
                  VALUES
                    (
                        (
                            SELECT ProcessRecordId
                            FROM Processes
                            WHERE ProcessId = $clientProcessId
                              AND StartedAtUtc <= $timestampUtc
                              AND (EndedAtUtc IS NULL OR EndedAtUtc >= $timestampUtc)
                            ORDER BY StartedAtUtc DESC, ProcessRecordId DESC
                            LIMIT 1
                        ),
                        $timestampUtc, $eventId, $version, $opcode, $processId, $threadId, $correlationId, $groupOperationId, $operationId, $operation, $clientMachine, $clientMachineFqdn, $userName, $clientProcessId, $clientProcessCreationTime, $namespaceName, $isLocal
                    );";
            command.Parameters.Add("$timestampUtc", SqliteType.Text);
            command.Parameters.Add("$eventId", SqliteType.Integer);
            command.Parameters.Add("$version", SqliteType.Integer);
            command.Parameters.Add("$opcode", SqliteType.Integer);
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$threadId", SqliteType.Integer);
            command.Parameters.Add("$correlationId", SqliteType.Text);
            command.Parameters.Add("$groupOperationId", SqliteType.Integer);
            command.Parameters.Add("$operationId", SqliteType.Integer);
            command.Parameters.Add("$operation", SqliteType.Text);
            command.Parameters.Add("$clientMachine", SqliteType.Text);
            command.Parameters.Add("$clientMachineFqdn", SqliteType.Text);
            command.Parameters.Add("$userName", SqliteType.Text);
            command.Parameters.Add("$clientProcessId", SqliteType.Integer);
            command.Parameters.Add("$clientProcessCreationTime", SqliteType.Integer);
            command.Parameters.Add("$namespaceName", SqliteType.Text);
            command.Parameters.Add("$isLocal", SqliteType.Integer);
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
                    (ProcessId, ParentProcessId, ImageFileName, CommandLine, UserSID, StartedAtUtc)
                  VALUES
                    ($processId, $parentProcessId, $imageFileName, $commandLine, $userSID, $startedAtUtc);";
            command.Parameters.Add("$processId", SqliteType.Integer);
            command.Parameters.Add("$parentProcessId", SqliteType.Integer);
            command.Parameters.Add("$imageFileName", SqliteType.Text);
            command.Parameters.Add("$commandLine", SqliteType.Text);
            command.Parameters.Add("$userSID", SqliteType.Text);
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

        private static void RemoveLegacyPowerMeterPollingTables(SqliteConnection connection)
        {
            List<string> legacyTableNames = [];
            using (SqliteCommand listTablesCommand = connection.CreateCommand())
            {
                listTablesCommand.CommandText =
                    @"SELECT name
                      FROM sqlite_master
                      WHERE type = 'table'
                        AND name LIKE 'PowerMeterPolling%'
                        AND name <> 'PowerMeterPollingEvents_4';";

                using SqliteDataReader reader = listTablesCommand.ExecuteReader();
                while (reader.Read())
                {
                    legacyTableNames.Add(reader.GetString(0));
                }
            }

            foreach (string tableName in legacyTableNames)
            {
                using SqliteCommand dropTableCommand = connection.CreateCommand();
                dropTableCommand.CommandText = $"DROP TABLE \"{tableName.Replace("\"", "\"\"")}\";";
                dropTableCommand.ExecuteNonQuery();
            }
        }

        private static void EnsurePowerMeterPollingEvent4EnergyIsInteger(SqliteConnection connection)
        {
            using SqliteCommand columnTypeCommand = connection.CreateCommand();
            columnTypeCommand.CommandText = "SELECT type FROM pragma_table_info('PowerMeterPollingEvents_4') WHERE name = 'AbsoluteEnergy';";
            string? columnType = columnTypeCommand.ExecuteScalar() as string;
            if (string.Equals(columnType, "INTEGER", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using SqliteCommand overflowCheckCommand = connection.CreateCommand();
            overflowCheckCommand.CommandText =
                @"SELECT COUNT(*)
                  FROM PowerMeterPollingEvents_4
                  WHERE length(AbsoluteEnergy) > 19
                     OR (length(AbsoluteEnergy) = 19 AND AbsoluteEnergy > '9223372036854775807');";
            if (Convert.ToInt64(overflowCheckCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException("PowerMeterPollingEvents_4 包含超出 Int64 範圍的 AbsoluteEnergy，無法轉換為 INTEGER。");
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand rebuildCommand = connection.CreateCommand();
            rebuildCommand.Transaction = transaction;
            rebuildCommand.CommandText =
                @"CREATE TABLE PowerMeterPollingEvents_4_WithIntegerEnergy
                  (
                      PowerMeterPollingEvent4Id INTEGER PRIMARY KEY,
                      TimestampUtc TEXT NOT NULL,
                      EventId INTEGER NOT NULL,
                      Version INTEGER NOT NULL,
                      Opcode INTEGER NOT NULL,
                      MeterId TEXT NOT NULL,
                      AbsoluteEnergy INTEGER NOT NULL,
                      AbsoluteTime TEXT NOT NULL
                  );

                  INSERT INTO PowerMeterPollingEvents_4_WithIntegerEnergy
                      (PowerMeterPollingEvent4Id, TimestampUtc, EventId, Version, Opcode, MeterId, AbsoluteEnergy, AbsoluteTime)
                  SELECT
                      PowerMeterPollingEvent4Id, TimestampUtc, EventId, Version, Opcode, MeterId, CAST(AbsoluteEnergy AS INTEGER), AbsoluteTime
                  FROM PowerMeterPollingEvents_4;

                  DROP TABLE PowerMeterPollingEvents_4;

                  ALTER TABLE PowerMeterPollingEvents_4_WithIntegerEnergy
                  RENAME TO PowerMeterPollingEvents_4;

                  CREATE INDEX IX_PowerMeterPollingEvents_4_MeterTimestamp
                  ON PowerMeterPollingEvents_4 (MeterId, TimestampUtc);";
            rebuildCommand.ExecuteNonQuery();
            transaction.Commit();
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

        private static void EnsureCpuProfileSampleColumns(SqliteConnection connection)
        {
            (string Name, string Definition)[] columns =
            {
                ("ImageLoadId", "INTEGER NULL REFERENCES ImageLoads(ImageLoadId) ON DELETE RESTRICT"),
                ("ModuleFileName", "TEXT NULL"),
                ("ModuleRva", "TEXT NULL"),
            };

            foreach ((string name, string definition) in columns)
            {
                using SqliteCommand columnExistsCommand = connection.CreateCommand();
                columnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('CpuProfileSamples') WHERE name = $name;";
                columnExistsCommand.Parameters.AddWithValue("$name", name);

                if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                using SqliteCommand addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = $"ALTER TABLE CpuProfileSamples ADD COLUMN {name} {definition};";
                addColumnCommand.ExecuteNonQuery();
            }

            using SqliteCommand createIndexCommand = connection.CreateCommand();
            createIndexCommand.CommandText =
                @"CREATE INDEX IF NOT EXISTS IX_CpuProfileSamples_ImageLoad
                  ON CpuProfileSamples (ImageLoadId, TimestampUtc);";
            createIndexCommand.ExecuteNonQuery();
        }

        private static void EnsureWmiActivityColumns(SqliteConnection connection)
        {
            using SqliteCommand legacyTableExistsCommand = connection.CreateCommand();
            legacyTableExistsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'WmiActivityEvents';";

            using SqliteCommand newTableExistsCommand = connection.CreateCommand();
            newTableExistsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'WmiActivityEvents_24';";

            bool legacyTableExists = Convert.ToInt64(legacyTableExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
            bool newTableExists = Convert.ToInt64(newTableExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;

            if (legacyTableExists && !newTableExists)
            {
                // 舊版資料表名稱為 WmiActivityEvents，最新命名為 WmiActivityEvents_24，需先更名並移除舊索引。
                using SqliteCommand renameTableCommand = connection.CreateCommand();
                renameTableCommand.CommandText =
                    @"ALTER TABLE WmiActivityEvents RENAME TO WmiActivityEvents_24;

                      DROP INDEX IF EXISTS IX_WmiActivityEvents_OperationTimestamp;
                      DROP INDEX IF EXISTS IX_WmiActivityEvents_NamespaceTimestamp;
                      DROP INDEX IF EXISTS IX_WmiActivityEvents_ProcessRecord;";
                renameTableCommand.ExecuteNonQuery();
            }

            using SqliteCommand columnExistsCommand = connection.CreateCommand();
            columnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WmiActivityEvents_24') WHERE name = 'ProcessRecordId';";

            if (Convert.ToInt64(columnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using SqliteCommand addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = "ALTER TABLE WmiActivityEvents_24 ADD COLUMN ProcessRecordId INTEGER NULL;";
                addColumnCommand.ExecuteNonQuery();
            }

            using SqliteCommand operationColumnExistsCommand = connection.CreateCommand();
            operationColumnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WmiActivityEvents_24') WHERE name = 'Operation';";

            if (Convert.ToInt64(operationColumnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                // 舊版資料表含 Operation (NOT NULL) 欄位，最新事件定義已移除該欄位，需重建資料表結構。
                using SqliteCommand rebuildCommand = connection.CreateCommand();
                rebuildCommand.CommandText =
                    @"ALTER TABLE WmiActivityEvents_24 RENAME TO WmiActivityEvents_24_Old;

                      CREATE TABLE WmiActivityEvents_24
                      (
                          WmiActivityEventId INTEGER PRIMARY KEY,
                          ProcessRecordId INTEGER NULL REFERENCES Processes(ProcessRecordId) ON DELETE RESTRICT,
                          TimestampUtc TEXT NOT NULL,
                          EventId INTEGER NOT NULL,
                          Version INTEGER NOT NULL,
                          Opcode INTEGER NOT NULL,
                          ProcessId INTEGER NOT NULL,
                          ThreadId INTEGER NOT NULL,
                          NamespaceName TEXT NOT NULL,
                          ClientProcessId INTEGER NULL,
                          IntervalMs INTEGER NULL,
                          Query TEXT NULL,
                          GroupOperationId INTEGER NULL
                      );

                      INSERT INTO WmiActivityEvents_24
                          (WmiActivityEventId, ProcessRecordId, TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, NamespaceName)
                      SELECT
                          WmiActivityEventId, ProcessRecordId, TimestampUtc, EventId, Version, Opcode, ProcessId, ThreadId, NamespaceName
                      FROM WmiActivityEvents_24_Old;

                      DROP TABLE WmiActivityEvents_24_Old;";
                rebuildCommand.ExecuteNonQuery();
            }

            (string Name, string Definition)[] wmiColumns =
            {
                ("ClientProcessId", "INTEGER NULL"),
                ("IntervalMs", "INTEGER NULL"),
                ("Query", "TEXT NULL"),
                ("GroupOperationId", "INTEGER NULL"),
            };

            foreach ((string name, string definition) in wmiColumns)
            {
                using SqliteCommand wmiColumnExistsCommand = connection.CreateCommand();
                wmiColumnExistsCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WmiActivityEvents_24') WHERE name = $name;";
                wmiColumnExistsCommand.Parameters.AddWithValue("$name", name);

                if (Convert.ToInt64(wmiColumnExistsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                using SqliteCommand addWmiColumnCommand = connection.CreateCommand();
                addWmiColumnCommand.CommandText = $"ALTER TABLE WmiActivityEvents_24 ADD COLUMN {name} {definition};";
                addWmiColumnCommand.ExecuteNonQuery();
            }

            using SqliteCommand createNamespaceIndexCommand = connection.CreateCommand();
            createNamespaceIndexCommand.CommandText =
                @"CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_24_NamespaceTimestamp
                  ON WmiActivityEvents_24 (NamespaceName, TimestampUtc);";
            createNamespaceIndexCommand.ExecuteNonQuery();

            using SqliteCommand createIndexCommand = connection.CreateCommand();
            createIndexCommand.CommandText =
                @"CREATE INDEX IF NOT EXISTS IX_WmiActivityEvents_24_ProcessRecord
                  ON WmiActivityEvents_24 (ProcessRecordId, TimestampUtc);";
            createIndexCommand.ExecuteNonQuery();
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
