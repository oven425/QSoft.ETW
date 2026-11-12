# QSoft.ETW
this is a library, like System.Diagnostics.Tracing, but can NAOT

## System impact experiment

[ConsoleApp_SystemImpact](ConsoleApp_SystemImpact/README.md) captures a three-minute ETL,
collects events through reader callbacks into in-memory Lists, and scores processes and apps
(frequency-domain wake periodicity confirmed in the time domain, CPU, wakes, DPC/ISR, WMI,
thread and CSwitch evidence, issues and fixes) plus an explicitly heuristic 0-100 system score.
No SQLite or reader plugins are used.

## USB and webcam capture

`WithUsbMonitoring()` enables the USB 3.0 stack providers (USBXHCI, UCX, USBHUB3) plus the
USB 2.0 USBPORT/USBHUB providers, with device/hub rundown on start and stop. Pass
`UsbTransferTraceLevel.Headers` (or `PartialData`/`FullData`) to also log individual USB transfers.
`WithCameraMonitoring()` adds Microsoft-Windows-USBVideo and Microsoft-Windows-MF-FrameServer and
logs transfer headers by default.

When reading, `EtlFileReader.Usb` raises one `UsbEventInfo` per event from these seven providers, with
the payload decoded through the manifest. Struct members are named `Struct.Member` with the `fid_` prefix
removed. Struct arrays such as isochronous packets are reduced to `.Count`, `.Member.Sum` and
`.Member.NonZeroCount`. `Fields` is reused, so it is only valid inside the callback. `TaskName` and
`OpcodeName` are localized display names; identify events by `EventId`.

```csharp
var reader = new EtlFileReader();
var pending = reader.UseUsbData();
reader.ProcessFile("camera.etl");
IUsbDataSource usb = pending.Result;
foreach (UsbEndpointTransferSummary s in usb.EndpointSummaries) { /* bytes, MB/s, failures per endpoint */ }
```

`UseUsbData()` collects controllers, devices (VID/PID, speed, port), endpoints, URB transfers (bytes,
latency, NTSTATUS, isochronous packet errors), D0 entry/exit, port status, errors, USBVideo frames,
FrameServer samples and capture statistics, and maps each camera symbolic link to its USB device.
Transfers come from the USB 3.0 stack only; USBPORT/USBHUB are captured but not aggregated.

[ConsoleApp_UsbCameraTest](ConsoleApp_UsbCameraTest/Program.cs) records a trace while opening the first
color camera through `MediaCapture`, and writes `ground-truth.json` (open/stream times and frames received)
for comparison with the ETL. On a UVC webcam, USBVideo events 208/209 and FrameServer events 8/9/60 occur
once per delivered frame. `--format SUBTYPE[:WxH[@FPS]]` selects the capture format (`--list-formats` lists
them), and `--analyze FILE.etl [--fields]` prints the `UseUsbData()` result (and one decoded sample per event
type).

`PartialData`/`FullData` did not add transfer contents on Windows 11 26200: UCX emitted the same header-only
events as `Headers` (also with logman), so frame pixels cannot be recovered from the ETL. Format, frame size
and timing are available: the HP HD Camera streams native MJPG at 1280x720 and native YUY2 at 640x480
(614,400 bytes per frame).

## Cross-machine symbols

Opt in to post-capture symbol metadata merging with `WithSymbolMetadataMerge()`. It enables
image-load rundown and, after stopping, uses `KernelTraceControl.dll` to add image/PDB identities
and provider metadata to the ETL. The DLL can be supplied explicitly or found in the application
directory / Windows Performance Toolkit installation. If it is unavailable or merging fails, the
original ETL is retained and `TraceSession.SymbolMetadataMergeResult` reports the outcome.

`EtlFileReader.ImagePdbIdentity` exposes the merged PDB GUID/Age. The interrupt-handling plugin
uses those identities to locate exact PDBs through `DbgHelpSymbolResolutionOptions`, including on
a different machine:

```csharp
var symbols = DbgHelpSymbolResolutionOptions.CreateWithMicrosoftSymbolServer(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QSoft", "Symbols"));
var interruptData = reader.UseInterruptHandlingData(symbols);
reader.ProcessFile(etlPath);
```

For symbol-server downloads, deploy matching-architecture `dbghelp.dll` and `symsrv.dll`
from the Windows SDK Debugging Tools alongside the analysis executable. Merge requires
`KernelTraceControl.dll` on the capture machine; it is not needed to read a merged ETL.
Repeated rundown records with the same PDB identity are supported even when the image and
PDB names differ (for example, `ntoskrnl.exe` / `ntkrnlmp.pdb`). Conflicting identities at a
reused image base are not assigned by this name-independent fallback.
Merge does not embed PDB files: third-party or private symbols still require access to
their matching PDBs or symbol server.