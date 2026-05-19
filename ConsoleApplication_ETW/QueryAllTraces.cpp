#include <windows.h>
#include <evntrace.h>
#include <vector>

const unsigned MAX_SESSION_NAME_LEN = 1024;
const unsigned MAX_LOGFILE_PATH_LEN = 1024;
const unsigned PropertiesSize =
sizeof(EVENT_TRACE_PROPERTIES) +
(MAX_SESSION_NAME_LEN * sizeof(CHAR)) +
(MAX_LOGFILE_PATH_LEN * sizeof(CHAR));

int QuerytAll()
{
    ULONG status;
    std::vector<EVENT_TRACE_PROPERTIES*> sessions; // Array of pointers to property structures
    std::vector<BYTE> buffer;                      // Buffer that contains all the property structures
    ULONG sessionCount;                            // Actual number of sessions started on the computer

    // The size of the session name and log file name used by the
    // controllers are not known, therefore create a properties structure that allows
    // for the maximum size of both.

    try
    {
        sessionCount = 64; // Start with room for 64 sessions.
        do
        {
            sessions.resize(sessionCount);
            buffer.resize(PropertiesSize * sessionCount);

            for (size_t i = 0; i != sessions.size(); i += 1)
            {
                sessions[i] = (EVENT_TRACE_PROPERTIES*)&buffer[i * PropertiesSize];
                sessions[i]->Wnode.BufferSize = PropertiesSize;
                sessions[i]->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
                sessions[i]->LogFileNameOffset = sizeof(EVENT_TRACE_PROPERTIES) + (MAX_SESSION_NAME_LEN * sizeof(CHAR));
            }

            status = QueryAllTracesA(&sessions[0], sessionCount, &sessionCount);
        } while (status == ERROR_MORE_DATA);

        if (status != ERROR_SUCCESS)
        {
            printf("Error calling QueryAllTraces: %u\n", status);
        }
        else
        {
            printf("Actual session count: %u.\n\n", sessionCount);

            for (ULONG i = 0; i < sessionCount; i++)
            {
                WCHAR sessionGuid[50];
                (void)StringFromGUID2(sessions[i]->Wnode.Guid, sessionGuid, ARRAYSIZE(sessionGuid));

                printf(
                    "Session GUID: %ls\n"
                    "Session ID: %llu\n"
                    "Session name: %s\n"
                    "Log file: %s\n"
                    "min buffers: %u\n"
                    "max buffers: %u\n"
                    "buffers: %u\n"
                    "buffers written: %u\n"
                    "buffers lost: %u\n"
                    "events lost: %u\n"
                    "\n",
                    sessionGuid,
                    sessions[i]->Wnode.HistoricalContext,
                    (PCSTR)((LPCBYTE)sessions[i] + sessions[i]->LoggerNameOffset),
                    (PCSTR)((LPCBYTE)sessions[i] + sessions[i]->LogFileNameOffset),
                    sessions[i]->MinimumBuffers,
                    sessions[i]->MaximumBuffers,
                    sessions[i]->NumberOfBuffers,
                    sessions[i]->BuffersWritten,
                    sessions[i]->LogBuffersLost,
                    sessions[i]->EventsLost);
				if (wcscmp(sessionGuid, L"{AE44CB98-BD11-4069-8093-770EC9258A12}") == 0)
				{
                    auto hr = ControlTraceA((TRACEHANDLE)NULL, (PCSTR)((LPCBYTE)sessions[i] + sessions[i]->LoggerNameOffset), sessions[i], EVENT_TRACE_CONTROL_STOP);
				}
            }
        }
    }
    catch (std::bad_alloc const&)
    {
        printf("Error allocating memory for properties.\n");
        status = ERROR_OUTOFMEMORY;
    }

    return status;
}