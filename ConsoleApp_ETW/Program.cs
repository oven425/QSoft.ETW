// See https://aka.ms/new-console-template for more information
using QSoft.ETW;

Console.WriteLine("Hello, World!");
//ETW etw = new ETW();
////etw.QueryAllTraces().Where(x => x.SessionName == "MySession");
////etw.EnumerateProviders();
//etw.Stop();
//etw.Start();
//await Task.Delay(TimeSpan.FromSeconds(30));
//etw.Stop();

//etw.Open("test.etl");


var builder = new SessionBuilder()
    .WithLogFile("123.etl");

var session = builder.Build();
session.Start();
await Task.Delay(5000);
session.Stop();