using System;
using System.Runtime.Serialization;

namespace Genesys.Bayeux.Client
{
    [Serializable]
    internal class BayeuxTransportException : Exception
    {
        public bool TransportClosed { get; private set; }

        public BayeuxTransportException(string message, Exception innerException, bool transportClosed)
            : base(innerException.Message, innerException)
        {
            this.TransportClosed = transportClosed;
        }
    }
}