using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Service {
    public interface IDitherServiceClient {
        void RegisterSync();
        void UnregisterSync();
        Task Register();
        Task Unregister();
        Task<bool> WaitForSync(CancellationToken ct);
        Task WaitForDither(CancellationToken ct);
        Task SetDitherInProgress(CancellationToken ct);
        Task SetDitherCompleted(CancellationToken ct);
        Task AnnounceToSync(bool canLead, CancellationToken ct);
        Task<string> Ping(CancellationToken ct);
    }
}
