using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    internal interface IBayeuxTransport : IDisposable
    {
        Task OpenAsync(CancellationToken cancellationToken);
        Task<JObject> RequestAsync(IEnumerable<object> requests, CancellationToken cancellationToken);
    }
}