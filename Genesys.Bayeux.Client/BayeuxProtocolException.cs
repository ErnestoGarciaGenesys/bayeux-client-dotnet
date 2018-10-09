using System;
using System.Runtime.Serialization;

namespace Genesys.Bayeux.Client
{
    public class BayeuxProtocolException : Exception
    {
        public BayeuxProtocolException(string message) : base(message)
        {
        }
    }
}