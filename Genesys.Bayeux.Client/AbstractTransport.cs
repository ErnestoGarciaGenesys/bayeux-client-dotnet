using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Genesys.Bayeux.Client
{
    abstract class AbstractTransport : IBayeuxTransport
    {
        public virtual Task Init(CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public abstract Task<JObject> Request(IEnumerable<object> request, CancellationToken cancellationToken);

        #region IDisposable Support
        bool disposed = false;

        protected virtual void Dispose(bool disposing) { }

        void DisposeImpl(bool disposing)
        {
            if (!disposed)
            {
                Dispose(disposing);
                disposed = true;
            }
        }

         ~AbstractTransport()
         {
             DisposeImpl(false);
         }

        public void Dispose()
        {
            DisposeImpl(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
