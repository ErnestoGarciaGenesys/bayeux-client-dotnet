using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Genesys.Bayeux.Client.Util
{
    class BooleanLatch
    {
        int closed = 0;

        public bool AlreadyRun()
        {
            var oldClosed = Interlocked.Exchange(ref closed, 1);
            return oldClosed == 1;
        }
    }
}
