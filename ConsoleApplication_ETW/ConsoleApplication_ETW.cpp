// ConsoleApplication_ETW.cpp : 此檔案包含 'main' 函式。程式會於該處開始執行及結束執行。
//


#include "ETW.h"
int main()
{
    ETW etw;
    etw.CurrentTraces();
    etw.AllProviders();
    etw.Save();
    //etw.SaveKernel();
    //etw.Open();
}

