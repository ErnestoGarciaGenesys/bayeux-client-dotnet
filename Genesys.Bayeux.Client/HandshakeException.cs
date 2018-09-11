using System;

namespace Genesys.Bayeux.Client
{
    [Serializable]
    public class HandshakeException : Exception
    {
        public HandshakeException(string message) : base(message)
        {
        }
    }
}