using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Service {
    public interface ISyncServiceClient {
        void RegisterSync();
        void UnregisterSync();
        Task Register();
        Task Unregister();
        Task<bool> WaitForSyncStart(CancellationToken ct, TimeSpan timeout);
        Task WaitForSyncComplete(CancellationToken ct, TimeSpan timeout);
        Task SetSyncInProgress(CancellationToken ct);
        Task SetSyncComplete(CancellationToken ct);
        Task AnnounceToSync(bool canLead, CancellationToken ct);
        Task<string> Ping(CancellationToken ct);
    }
}
