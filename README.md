# .NET Bayeux Client
[![NuGet Badge](https://buildstats.info/nuget/Genesys.Bayeux.Client)](https://www.nuget.org/packages/Genesys.Bayeux.Client)

[Bayeux](https://docs.cometd.org/current/reference/#_bayeux) is a protocol that enables web-friendly message transport over HTTP, WebSockets, etc.

This library provides a client for receiving events from a Bayeux server, through the HTTP long-polling transport, or WebSockets. It provides convenient async methods.

## Quick start

This is an example of usage:

~~~cs
var httpClient = new HttpClient();
var bayeuxClient = new BayeuxClient(
    new HttpLongPollingTransportOptions()
    {
        HttpClient = httpClient,
        Uri = "http://localhost:8080/bayeux/",
    });

bayeuxClient.EventReceived += (e, args) =>
    Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

bayeuxClient.ConnectionStateChanged += (e, args) =>
	Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

bayeuxClient.AddSubscriptions("/**");

await bayeuxClient.Start();
~~~

## Logging

Logger name is `Genesys.Bayeux.Client`.

Logging levels used are:
- `Debug`: Contents of messages.
- `Info`: Messages about connection availabity and other significant infrequent messages.
- `Warn`: Recoverable errors. For example, connection errors with reconnection strategy.
- `Error`: Unexpected errors.


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

## Features

### Start modes

Different applications need different ways to start a Bayeux connection.

Some applications may want to wait and check if the first connection succeeds. This is typical for interactive user applications. For this case, use method `Start(CancellationToken)`.

Other applications, like the ones running in the background, may continue to run with an failed first connection, as long as reconnections are tried. For this case, use method `StartInBackground()`.

### Connection and subscription recovery

Reconnections and automatic channel re-subscriptions are implemented.

Reconnection delays can be provided as a parameter of the `BayeuxClient` constructors.

### Subscription modes

Usually, applications can't recover from a failed subscribed channel, and they can't control when the connection is available anyway. These applications use the `AddSubscription` and `RemoveSubscription` methods.

Picky applications that want to make sure that subscriptions are successful use the `Subscribe` and `Unsubscribe` methods.

### WebSockets

WebSocket transport is supported:

~~~cs
var bayeuxClient = new BayeuxClient(
    new WebSocketTransportOptions()
    {
        Uri = new Uri("ws://localhost:8080/bayeux/"),
    });
~~~

### Customizing HTTP POSTs

The `IHttpPost` interface is offered for clients that want to provide their own HTTP POST implementation. This is also useful to implement retries for servers that may occasionally fail, in need of a session refresh.

### `TaskScheduler` for event publishing

It is important to publish events in the proper threading context, for not running into obscure multithreading issues.

By default, a proper scheduler will be chosen, based on the current `SynchronizationContext`. This will do the right thing for Windows Forms or WPF applications. If there is no current `SynchronizationContext`, a `TaskScheduler` with ordered execution of events will be created and used.

The client can also provide a `TaskScheduler` as a parameter to `BayeuxClient` constructors.