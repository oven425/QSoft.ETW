// See https://aka.ms/new-console-template for more information
using QSoft.ETW;

Console.WriteLine("Hello, World!");
ETW etw = new ETW();
etw.QueryAllTraces().Where(x=>x.SessionName == "MySession");
//etw.EnumerateProviders();
etw.Stop();
etw.Start();
await Task.Delay(10000);
etw.Stop();