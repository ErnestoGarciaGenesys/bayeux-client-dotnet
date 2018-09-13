using System;

namespace Genesys.Bayeux.Client
{
    [Serializable]
    public class BayeuxRequestFailedException : Exception
    {
        public BayeuxRequestFailedException(string error) : base(error)
        {
        }
    }
}