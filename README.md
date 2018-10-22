# .NET Bayeux Client
[![NuGet Badge](https://buildstats.info/nuget/Genesys.Bayeux.Client)](https://www.nuget.org/packages/Genesys.Bayeux.Client)

[Bayeux](https://docs.cometd.org/current/reference/#_bayeux) is a protocol that enables web-friendly message transport over HTTP, WebSockets, etc.

This library provides a Bayeux client for .NET clients to receive events from a Bayeux server, through the HTTP long-polling transport.

## Usage

This is an example of usage:

~~~cs
var httpClient = new HttpClient();
var bayeuxClient = new BayeuxClient(httpClient, BaseURL + "/notifications");

bayeuxClient.EventReceived += (e, args) =>
  Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

bayeuxClient.ConnectionStateChanged += (e, args) =>
  Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

await bayeuxClient.Start();
await bayeuxClient.Subscribe("/updates");
~~~

## Logging

Logger name is `Genesys.Bayeux.Client`.

This library uses [LibLog](https://github.com/damianh/LibLog) for logging. It will automatically adapt to
[NLog](http://nlog-project.org/),
[Log4Net](https://logging.apache.org/log4net/),
[Serilog](http://serilog.net/) and 
[Loupe](http://www.gibraltarsoftware.com/Loupe).

You may also provide your own custom logger implementation by calling:

~~~cs
Genesys.Bayeux.Client.Logging.LogProvider.SetCurrentLogProvider(<My ILogProvider implementation>);
~~~

If none of these are available,  System.Diagnostics.TraceSource will be used. (See [how to configure tracing](https://docs.microsoft.com/en-us/dotnet/framework/network-programming/how-to-configure-network-tracing)).
