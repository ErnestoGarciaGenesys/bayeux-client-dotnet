using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    internal interface IBayeuxTransport : IDisposable
    {
        Task Open(CancellationToken cancellationToken);
        Task<JObject> Request(IEnumerable<object> requests, CancellationToken cancellationToken);
    }
}