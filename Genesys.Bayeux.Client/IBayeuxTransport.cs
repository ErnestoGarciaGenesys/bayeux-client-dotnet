using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    interface IBayeuxTransport : IDisposable
    {
        Task Init(CancellationToken cancellationToken);

        Task<JObject> Request(IEnumerable<object> requests, CancellationToken cancellationToken);
    }
}