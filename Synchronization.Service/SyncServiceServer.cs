using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NINA.Core.Utility;
using NINA.Synchronization.Service.Sync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Synchronization.Service {
    /// <summary>
    /// Protocol:
    /// ClientA, ClientB, ClientC
    /// 
    /// Register when wanting to sync
    /// 
    /// -> AnnounceToSync 
    /// -> WaitForSync 
    /// -> IsLeader? (Response from WaitForSync)
    ///     -> YES
    ///         -> SetDitherInProgress 
    ///         -> SetDitherCompleted 
    ///     -> NO
    ///         -> WaitForDither 
    ///         
    /// 
    /// 
    /// Unregister when finishing the sync block
    /// 
    /// Ping is required to determine keepalives
    /// </summary>
    public class SyncServiceServer : SyncService.SyncServiceBase {
        private static readonly Lazy<SyncServiceServer> lazy = new Lazy<SyncServiceServer>(() => new SyncServiceServer());
        public static SyncServiceServer Instance { get => lazy.Value; }

        public string Status { 
            get {
                lock(lockobj) {
                    return this.status;
                }
            } 
            private set {
                lock (lockobj) {
                    this.status = value;
                }
            }
        }

        private string syncLeader;

        private SyncServiceServer() {
            registeredClients = new SortedDictionary<string, DateTime>();
            clientsWaitingForSync = new SortedDictionary<string, bool>();
            syncInProgress = false;
            status = "idle";
        }

        private SortedDictionary<string, DateTime> registeredClients { get; }
        private SortedDictionary<string, bool> clientsWaitingForSync { get; }
        private bool syncInProgress;

        private object lockobj = new object();
        private string status;

        private bool IsRegistered(string id) {
            lock (lockobj) {
                if (registeredClients.ContainsKey(id)) {
                    return true;
                }
                return false;
            }
        }

        private void AddClient(string id) {
            lock (lockobj) {
                registeredClients.Add(id, DateTime.UtcNow);
            }
        }

        private void RemoveClient(string id) {
            lock (lockobj) {
                registeredClients.Remove(id);
            }
        }

        private void UpdateClient(string id) {
            lock (lockobj) {
                if (registeredClients.ContainsKey(id)) {
                    registeredClients[id] = DateTime.UtcNow;
                }
            }
        }

        private string ElectSyncLeader() {
            lock (lockobj) {
                return clientsWaitingForSync.Where(x => x.Value == true && registeredClients.Where(r => r.Key == x.Key && r.Value > DateTime.UtcNow.AddSeconds(-10)).Select(y => y.Key) != null).Select(kvp => kvp.Key).FirstOrDefault();
            }
        }

        private void AddClientWaitingForSync(string id, bool canLead) {
            lock (lockobj) {
                if(!registeredClients.ContainsKey(id)) {
                    // In case a client missed to register or the server restarted in between add the client to the registered clients again
                    registeredClients.Add(id, DateTime.UtcNow);
                }
                clientsWaitingForSync.Add(id, canLead);
            }
        }

        private int NumberOfClientsWaitingForSync() {
            lock (lockobj) {
                return clientsWaitingForSync.Count;
            }
        }

        private int NumberOfTotalClients() {
            lock (lockobj) {
                return registeredClients.Where(x => x.Value > DateTime.UtcNow.AddSeconds(-10)).Select(x => x.Key).Count(); 
            }
        }

        private void ClearClientWaitingForSync() {
            lock (lockobj) {
                clientsWaitingForSync.Clear();
            }
        }

        public override async Task<Empty> Register(ClientIdRequest request, ServerCallContext context) {
            if (!IsRegistered(request.Clientid)) {
                Logger.Info($"Client {request.Clientid} registered for sync");
                AddClient(request.Clientid);
            }
            return new Empty();
        }

        public override async Task<Empty> Unregister(ClientIdRequest request, ServerCallContext context) {
            if (IsRegistered(request.Clientid)) {
                Logger.Info($"Client {request.Clientid} unregistered sync");
                RemoveClient(request.Clientid);
            }
            return new Empty();
        }

        public override async Task<Empty> AnnounceToSync(AnnounceToSyncRequest request, ServerCallContext context) {
            if (IsRegistered(request.Clientid)) {
                Logger.Debug($"Client {request.Clientid} is announcing to sync");
                AddClientWaitingForSync(request.Clientid, request.Canlead);
            }

            var clientsForSync = NumberOfClientsWaitingForSync();
            var totalClients = NumberOfTotalClients();
            Status = $"{clientsForSync}/{totalClients} clients waiting";
            syncInProgress = true;
            return new Empty();
        }

        public override async Task<LeaderReply> WaitForSyncStart(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is waiting to sync");

            while (syncInProgress && ClientsAreWaitingForSync()) {
                await Task.Delay(1000);
            }

            lock(lockobj) {
                syncLeader = ElectSyncLeader();
                Logger.Debug($"Client {syncLeader} is leading sync");
                if(string.IsNullOrEmpty(syncLeader)) {
                    Status = "No instance could lead the sync!";
                    syncInProgress = false;
                    syncLeader = string.Empty;
                    ClearClientWaitingForSync();
                }
            }

            return new LeaderReply() { LeaderId = syncLeader };
        }

        private bool ClientsAreWaitingForSync() {
            lock (lockobj) {
                var reg = registeredClients.Where(x => x.Value > DateTime.UtcNow.AddSeconds(-10)).Select(x => x.Key).ToList();
                return (reg.Intersect(clientsWaitingForSync.Keys).Count() < reg.Count);
            }
        }

        public override async Task<Empty> SetSyncInProgress(ClientIdRequest request, ServerCallContext context) {
            Status = $"Sync in progress";
            syncLeader = request.Clientid;
            return new Empty();
        }

        public override async Task<Empty> WaitForSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is announcing to want to sync");

            while (syncInProgress && SyncLeaderIsAlive()) {
                await Task.Delay(1000);
            }

            return new Empty();
        }

        public bool SyncLeaderIsAlive() {
            lock(lockobj) {
                if(registeredClients.ContainsKey(syncLeader) && registeredClients[syncLeader] > DateTime.UtcNow.AddSeconds(-10)) {
                    return true;
                } else {
                    // The sync leader is dead
                    syncInProgress = false;
                    syncLeader = string.Empty;
                    ClearClientWaitingForSync();
                    return false;
                }
                
            }
        }

        public override async Task<Empty> SetSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is setting sync to be complete");
            syncInProgress = false;
            syncLeader = string.Empty;
            ClearClientWaitingForSync();

            Status = $"idle";

            return new Empty();
        }

        public override async Task<PingReply> Ping(ClientIdRequest request, ServerCallContext context) {
            Logger.Trace($"Client {request.Clientid} is pinging the server");
            UpdateClient(request.Clientid);

            return new PingReply() { Reply = "Pong" };
        }
    }
}