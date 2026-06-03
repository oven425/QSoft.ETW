// See https://aka.ms/new-console-template for more information
using QSoft.ETW;

Console.WriteLine("Hello, World!");
ETW etw = new ETW();
//etw.EnumerateProviders();
etw.Save();