using System;

namespace Genesys.Bayeux.Client
{
    [Serializable]
    public class BayeuxRequestException : Exception
    {
        public string BayeuxError { get; private set; }

        public BayeuxRequestException(string error) : base(error)
        {
            BayeuxError = error;
        }
    }
}