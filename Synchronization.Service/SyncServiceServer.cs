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
            registeredClients = new Dictionary<string, SortedDictionary<string, DateTime>>();
            clientsWaitingForSync = new Dictionary<string, SortedDictionary<string, bool>>();
            syncInProgress = false;
            status = "idle";
        }

        private Dictionary<string, SortedDictionary<string, DateTime>> registeredClients { get; }
        private Dictionary<string, SortedDictionary<string, bool>> clientsWaitingForSync { get; }
        private bool syncInProgress;

        private object lockobj = new object();
        private string status;

        private bool IsRegistered(string id, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                if (registeredClients[source].ContainsKey(id)) {
                    return true;
                }
                return false;
            }
        }

        private void AddClient(string id, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                if (registeredClients[source].ContainsKey(id)) {
                    registeredClients[source][id] = DateTime.UtcNow;
                } else { 
                    registeredClients[source].Add(id, DateTime.UtcNow);
                }
            }
        }

        private void RemoveClient(string id, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                registeredClients[source].Remove(id);
            }
        }

        private void UpdateClient(string id) {
            lock (lockobj) {
                foreach(var clientsBySource in registeredClients.Values) {
                    if (clientsBySource.ContainsKey(id)) {
                        clientsBySource[id] = DateTime.UtcNow;
                    }
                }
                
            }
        }

        private string ElectSyncLeader(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                return clientsWaitingForSync[source].Where(x => x.Value == true && registeredClients[source].Where(r => r.Key == x.Key && r.Value > DateTime.UtcNow.AddSeconds(-10)).Select(y => y.Key) != null).Select(kvp => kvp.Key).FirstOrDefault();
            }
        }

        private void AddClientWaitingForSync(string id, bool canLead, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                if (!registeredClients[source].ContainsKey(id)) {
                    // In case a client missed to register or the server restarted in between add the client to the registered clients again
                    registeredClients[source].Add(id, DateTime.UtcNow);
                }

                if(!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                if (clientsWaitingForSync[source].ContainsKey(id)) {
                    clientsWaitingForSync[source][id] = canLead;
                } else {
                    clientsWaitingForSync[source].Add(id, canLead);
                }
            }
        }

        private int NumberOfClientsWaitingForSync(string source) {
            lock (lockobj) {
                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                return clientsWaitingForSync[source].Count;
            }
        }

        private int NumberOfTotalClients(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                return registeredClients[source].Where(x => x.Value > DateTime.UtcNow.AddSeconds(-10)).Select(x => x.Key).Count(); 
            }
        }

        private void ClearClientWaitingForSync(string source) {
            lock (lockobj) {
                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                clientsWaitingForSync[source].Clear();
            }
        }

        public override async Task<Empty> Register(ClientIdRequest request, ServerCallContext context) {
            if (!IsRegistered(request.Clientid, request.Source)) {
                Logger.Info($"Client {request.Clientid} registered for sync");
                AddClient(request.Clientid, request.Source);
            }
            return new Empty();
        }

        public override async Task<Empty> Unregister(ClientIdRequest request, ServerCallContext context) {
            if (IsRegistered(request.Clientid, request.Source)) {
                Logger.Info($"Client {request.Clientid} unregistered sync");
                RemoveClient(request.Clientid, request.Source);
            }
            return new Empty();
        }

        public override async Task<Empty> AnnounceToSync(AnnounceToSyncRequest request, ServerCallContext context) {
            if (IsRegistered(request.Clientid, request.Source)) {
                Logger.Debug($"Client {request.Clientid} is announcing to sync");
                AddClientWaitingForSync(request.Clientid, request.Canlead, request.Source);
            }

            var clientsForSync = NumberOfClientsWaitingForSync(request.Source);
            var totalClients = NumberOfTotalClients(request.Source);
            Status = $"{clientsForSync}/{totalClients} clients waiting for {request.Source}";
            syncInProgress = true;
            return new Empty();
        }

        public override async Task<LeaderReply> WaitForSyncStart(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is waiting to sync");

            while (syncInProgress && ClientsAreWaitingForSync(request.Source)) {
                await Task.Delay(1000);
            }

            lock(lockobj) {
                syncLeader = ElectSyncLeader(request.Source);
                Logger.Debug($"Client {syncLeader} is leading sync");
                if(string.IsNullOrEmpty(syncLeader)) {
                    Status = $"No instance could lead the {request.Source} sync!";
                    syncInProgress = false;
                    syncLeader = string.Empty;
                    ClearClientWaitingForSync(request.Source);
                }
            }

            return new LeaderReply() { LeaderId = syncLeader };
        }

        private bool ClientsAreWaitingForSync(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                var reg = registeredClients[source].Where(x => x.Value > DateTime.UtcNow.AddSeconds(-10)).Select(x => x.Key).ToList();
                return (reg.Intersect(clientsWaitingForSync[source].Keys).Count() < reg.Count);
            }
        }

        public override async Task<Empty> SetSyncInProgress(ClientIdRequest request, ServerCallContext context) {
            Status = $"{request.Source} sync in progress";
            syncLeader = request.Clientid;
            return new Empty();
        }

        public override async Task<Empty> WaitForSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is announcing to want to sync ${request.Source}");

            while (syncInProgress && SyncLeaderIsAlive(request.Source)) {
                await Task.Delay(1000);
            }

            return new Empty();
        }

        public bool SyncLeaderIsAlive(string source) {
            lock(lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, DateTime>();
                }
                if (registeredClients[source].ContainsKey(syncLeader) && registeredClients[source][syncLeader] > DateTime.UtcNow.AddSeconds(-10)) {
                    return true;
                } else {
                    // The sync leader is dead
                    syncInProgress = false;
                    syncLeader = string.Empty;
                    ClearClientWaitingForSync(source);
                    return false;
                }
                
            }
        }

        public override async Task<Empty> SetSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is setting sync to be complete");
            syncInProgress = false;
            syncLeader = string.Empty;
            ClearClientWaitingForSync(request.Source);

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