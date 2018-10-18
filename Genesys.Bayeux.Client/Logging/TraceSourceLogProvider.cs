using System;
using System.Diagnostics;

namespace Genesys.Bayeux.Client.Logging
{
    public class TraceSourceLogProvider : ILogProvider
    {
        public IDisposable OpenMappedContext(string key, object value, bool destructure = false)
        {
            throw new NotImplementedException();
        }

        // TODO: Contexts could possibly be implemented through System.Diagnostics.CorrelationManager,
        // but it does not behave well with async await. The context seems not removed at awaits, and it
        // accumulates indefinitely across Tasks run by the same thread. This would need to be controlled,
        // maybe by making the CorrelationManager context part of ExecutionContext.
        public IDisposable OpenNestedContext(string message)
        {
            throw new NotImplementedException();
        }

        public Logger GetLogger(string name)
        {
            return new TraceSourceLogger(name).Log;
        }
    }

    // This implementation does not support string formatting.
    public class TraceSourceLogger : ILog
    {
        readonly TraceSource traceSource;

        public TraceSourceLogger(string name)
        {
            traceSource = new TraceSource(name/*, SourceLevels.All*/);
        }

        public bool Log(LogLevel logLevel, Func<string> messageFunc, Exception exception = null, params object[] formatParameters)
        {
            if (messageFunc == null)
                return true;

            if (exception == null)
                traceSource.TraceEvent(ToTraceEventType(logLevel), 0, messageFunc());
            else if (exception.StackTrace == null)
                traceSource.TraceData(ToTraceEventType(logLevel), 0, messageFunc(), exception);
            else
                traceSource.TraceData(ToTraceEventType(logLevel), 0, messageFunc(), exception, exception.StackTrace.ToString());

            return true;
        }

        TraceEventType ToTraceEventType(LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Trace: return TraceEventType.Verbose;
                case LogLevel.Debug: return TraceEventType.Verbose;
                case LogLevel.Info: return TraceEventType.Information;
                case LogLevel.Warn: return TraceEventType.Warning;
                case LogLevel.Error: return TraceEventType.Error;
                case LogLevel.Fatal: return TraceEventType.Critical;
                default: return TraceEventType.Verbose;
            }
        }
    }
}