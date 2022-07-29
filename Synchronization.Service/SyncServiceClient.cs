using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using NINA.Core.Utility;
using NINA.Synchronization.Service.Sync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Service {
    public class SyncServiceClient : SyncService.SyncServiceClient, ISyncServiceClient {

        private static readonly Lazy<SyncServiceClient> lazy = new Lazy<SyncServiceClient>(() => new SyncServiceClient());
        private CancellationTokenSource heartbeatCts;
        public static SyncServiceClient Instance { get => lazy.Value; }

        private Guid id = Guid.NewGuid();

        private SyncServiceClient() : base(new NamedPipeChannel(".", "NINA.Synchronization.Service.Sync", new NamedPipeChannelOptions() { ConnectionTimeout = 300000 })) {
        }

        /// <summary>
        /// Register the client against the sync service
        /// </summary>
        public void RegisterSync() {
            base.Register(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            _ = StartHeartbeat();
        }

        public override Empty Register(ClientIdRequest request, CallOptions options) {
            _ = StartHeartbeat();
            return base.Register(request, options);
        }

        /// <summary>
        /// Remove the client from the sync service
        /// </summary>
        public void UnregisterSync() {
            base.Unregister(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            StopHeartbeat();
        }

        /// <summary>
        /// Register the client against the sync service
        /// </summary>
        public async Task Register() {
            await base.RegisterAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            _ = StartHeartbeat();
        }

        /// <summary>
        /// Remove the client from the sync service
        /// </summary>
        public async Task Unregister() {
            await base.UnregisterAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            StopHeartbeat();
        }

        /// <summary>
        /// Wait until the server sends that all clients are synced up
        /// </summary>
        /// <returns></returns>
        public async Task<bool> WaitForSyncStart(CancellationToken ct, TimeSpan timeout) {
            var result = await base.WaitForSyncStartAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(timeout.TotalSeconds), cancellationToken: ct);
            if(string.IsNullOrEmpty(result.LeaderId)) { throw new Exception("No instance could lead the sync! Make sure at least one instance is connected to a guider!"); }
            return result.LeaderId == id.ToString();
        }

        /// <summary>
        /// Wait until the server sends that the sync has been completed by the leader
        /// </summary>
        /// <returns></returns>
        public async Task WaitForSyncComplete(CancellationToken ct, TimeSpan timeout) {
            await base.WaitForSyncCompletedAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(timeout.TotalSeconds), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has started the sync
        /// </summary>
        /// <returns></returns>
        public async Task SetSyncInProgress(CancellationToken ct) {
            await base.SetSyncInProgressAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has finished the sync
        /// </summary>
        /// <returns></returns>
        public async Task SetSyncComplete(CancellationToken ct) {
            await base.SetSyncCompletedAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task AnnounceToSync(bool canLead, CancellationToken ct) {
            await base.AnnounceToSyncAsync(new AnnounceToSyncRequest() { Clientid = id.ToString(), Canlead = canLead }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task<string> Ping(CancellationToken ct) {
            var resp =  await base.PingAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            return resp.Reply;
        }

        private  Task StartHeartbeat() {
            return Task.Run(async () => {
                using (heartbeatCts = new CancellationTokenSource()) {
                    while (!heartbeatCts.IsCancellationRequested) {
                        try {
                            await Task.Delay(1000, heartbeatCts.Token);
                            var resp = await SyncServiceClient.Instance.Ping(heartbeatCts.Token);
                        } catch (OperationCanceledException) {
                            Logger.Info("Stopping heartbeat");
                        } catch (Exception ex) {
                            Logger.Error("An error occurred while pinging the server", ex);
                        }
                    }
                }
            });
        }

        private void StopHeartbeat() {
            try {
                heartbeatCts?.Cancel();
            } catch (Exception) { }
        }
    }
}
