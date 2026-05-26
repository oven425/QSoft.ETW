#pragma once
#include "KernelTrace.h"
#include <tchar.h>
class ETW
{
public:
	void Save(const TCHAR* filename = _T("test.etl"));
	void SaveKernel();
	void Open(const TCHAR* filename = _T("test.etl"));
private:
	KernelTrace m_KenerlTrace;
};

