#pragma once
#include<atlstr.h>
#include <evntrace.h>
class ETW
{
public:
	void Save(const TCHAR* filename = _T("test.etl"));
	void Open(const TCHAR* filename = _T("test.etl"));
//private:
//	static void EVENT_RECORD_CALLBACK(PEVENT_RECORD EventRecord);
};

