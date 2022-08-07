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
        private static object lockObj = new object();
        private bool heartbeatrunning = false;

        private SyncServiceClient() : base(new NamedPipeChannel(".", "NINA.Synchronization.Service.Sync", new NamedPipeChannelOptions() { ConnectionTimeout = 300000 })) {
        }

        /// <summary>
        /// Register the client against the sync service
        /// </summary>
        public void RegisterSync(string source) {
            base.Register(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            _ = StartHeartbeat();
        }

        public override Empty Register(ClientIdRequest request, CallOptions options) {
            _ = StartHeartbeat();
            return base.Register(request, options);
        }

        /// <summary>
        /// Remove the client from the sync service
        /// </summary>
        public void UnregisterSync(string source) {
            base.Unregister(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            StopHeartbeat();
        }

        /// <summary>
        /// Register the client against the sync service
        /// </summary>
        public async Task Register(string source) {
            await base.RegisterAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            _ = StartHeartbeat();
        }

        /// <summary>
        /// Remove the client from the sync service
        /// </summary>
        public async Task Unregister(string source) {
            await base.UnregisterAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            StopHeartbeat();
        }

        /// <summary>
        /// Wait until the server sends that all clients are synced up
        /// </summary>
        /// <returns></returns>
        public async Task<bool> WaitForSyncStart(string source, CancellationToken ct, TimeSpan timeout) {
            var result = await base.WaitForSyncStartAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(timeout.TotalSeconds), cancellationToken: ct);
            if (string.IsNullOrEmpty(result.LeaderId)) { throw new Exception($"No instance could lead the synchronized {source}!"); }
            return result.LeaderId == id.ToString();
        }

        /// <summary>
        /// Wait until the server sends that the sync has been completed by the leader
        /// </summary>
        /// <returns></returns>
        public async Task WaitForSyncComplete(string source, CancellationToken ct, TimeSpan timeout) {
            await base.WaitForSyncCompletedAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(timeout.TotalSeconds), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has started the sync
        /// </summary>
        /// <returns></returns>
        public async Task SetSyncInProgress(string source, CancellationToken ct) {
            await base.SetSyncInProgressAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has finished the sync
        /// </summary>
        /// <returns></returns>
        public async Task SetSyncComplete(string source, CancellationToken ct) {
            await base.SetSyncCompletedAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task AnnounceToSync(string source, bool canLead, CancellationToken ct) {
            await base.AnnounceToSyncAsync(new AnnounceToSyncRequest() { Clientid = id.ToString(), Source = source, Canlead = canLead }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task<string> Ping(CancellationToken ct) {
            var resp = await base.PingAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            return resp.Reply;
        }

        private Task StartHeartbeat() {
            lock (lockObj) {
                if (!heartbeatrunning) {
                    heartbeatrunning = true;
                    Logger.Info($"Starting heartbeat for {id}");
                    return Task.Run(async () => {
                        using (heartbeatCts = new CancellationTokenSource()) {
                            var token = heartbeatCts.Token;
                            while (!token.IsCancellationRequested) {
                                try {
                                    await Task.Delay(1000, token);
                                    var resp = await SyncServiceClient.Instance.Ping(token);
                                } catch (OperationCanceledException) {
                                    Logger.Info($"Stopping heartbeat for {id}");
                                } catch (Exception ex) {
                                    Logger.Error("An error occurred while pinging the server", ex);
                                }
                            }
                        }
                    });
                } else {
                    heartbeatrunning = false;
                    return Task.CompletedTask;
                }
            }
        }

        private void StopHeartbeat() {
            try {
                heartbeatCts?.Cancel();
            } catch (Exception) { }
            lock (lockObj) {
                heartbeatrunning = false;
            }
        }
    }
}
