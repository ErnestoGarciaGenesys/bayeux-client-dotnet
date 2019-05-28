using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    internal interface IBayeuxTransport : IDisposable
    {
        void SetEventPublisher(Action<IEnumerable<JObject>> eventPublisher);
        Task Open(CancellationToken cancellationToken);
        Task<JObject> Request(IEnumerable<object> requests, CancellationToken cancellationToken);
    }
}