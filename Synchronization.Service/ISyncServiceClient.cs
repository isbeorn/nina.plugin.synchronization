using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Service {
    public interface ISyncServiceClient {
        void RegisterSync(string source);
        void UnregisterSync(string source);
        Task Register(string source);
        Task Unregister(string source);
        Task<bool> WaitForSyncStart(string source, CancellationToken ct, TimeSpan timeout);
        Task WaitForSyncComplete(string source, CancellationToken ct, TimeSpan timeout);
        Task SetSyncInProgress(string source, CancellationToken ct);
        Task SetSyncComplete(string source, CancellationToken ct);
        Task AnnounceToSync(string source, bool canLead, CancellationToken ct);
        Task WithdrawFromSync(string source, CancellationToken ct);
        Task<string> Ping(CancellationToken ct);
    }
}
