


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
