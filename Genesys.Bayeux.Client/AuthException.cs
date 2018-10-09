using System;
using System.Runtime.Serialization;

namespace Genesys.Bayeux.Client
{
    internal class AuthException : Exception
    {
        public string Error { get; private set; }
        public string ErrorDescription { get; private set; }
        
        public AuthException(string error, string errorDescription) : base($"{error}: {errorDescription}")
        {
            Error = error;
            ErrorDescription = errorDescription;
        }
    }
}