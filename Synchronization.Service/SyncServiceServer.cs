using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NINA.Core.Utility;
using NINA.Synchronization.Service.Sync;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Synchronization.Service {
    class ClientSource {
        public ClientSource(string id) {
            Id = id;
            DateTime = DateTime.UtcNow;
            Registrations = 1;
        }

        public string Id { get; }
        public DateTime DateTime { get; set; }
        public int Registrations { get; set; }

    }

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
        public static readonly string IdleString = "Idle";
        private static readonly Lazy<SyncServiceServer> lazy = new Lazy<SyncServiceServer>(() => new SyncServiceServer());
        public static SyncServiceServer Instance { get => lazy.Value; }

        private Dictionary<string, string> statusBySource = new Dictionary<string, string>();


        private Dictionary<string, SortedDictionary<string, ClientSource>> registeredClients { get; }
        private Dictionary<string, SortedDictionary<string, bool>> clientsWaitingForSync { get; }
        private ConcurrentDictionary<string, bool> syncInProgress;
        private ConcurrentDictionary<string, string> syncLeader;


        private void SetStatus(string source, string message) {
            lock (lockobj) {
                if (statusBySource.ContainsKey(source) && string.IsNullOrWhiteSpace(message)) {
                    statusBySource.Remove(source);
                    return;
                }

                if (statusBySource.ContainsKey(source)) {
                    statusBySource[source] = message;
                } else {
                    statusBySource.Add(source, message);
                }
            }
        }

        public string GetStatus() {
            lock (lockobj) {
                if (statusBySource.Keys.Count > 0) {
                    return string.Join(" | ", statusBySource.Values);
                } else {
                    return IdleString;
                }
            }
        }

        private SyncServiceServer() {
            registeredClients = new Dictionary<string, SortedDictionary<string, ClientSource>>();
            clientsWaitingForSync = new Dictionary<string, SortedDictionary<string, bool>>();
            statusBySource = new Dictionary<string, string>();
            syncInProgress = new ConcurrentDictionary<string, bool>();
            syncLeader = new ConcurrentDictionary<string, string>();
        }

        private object lockobj = new object();


        private void AddClient(string id, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (registeredClients[source].ContainsKey(id)) {
                    registeredClients[source][id].Registrations += 1; 
                    Logger.Info($"Client {id} registered for sync with {source} {registeredClients[source][id].Registrations}x");
                } else {
                    registeredClients[source].Add(id, new ClientSource(id));
                    Logger.Info($"Client {id} registered for sync with {source}");
                }
            }
        }

        private void RemoveClient(string id, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {                    
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (registeredClients[source].ContainsKey(id)) {
                    var client = registeredClients[source][id];
                    client.Registrations -= 1;
                    if(client.Registrations == 0) {
                        registeredClients[source].Remove(id);
                        Logger.Info($"Client {id} unregistered sync with {source}");
                    } else {
                        Logger.Info($"Client {id} unregistered once sync with {source} but is still registered {client.Registrations}x");
                    }
                }
            }
        }

        private void UpdateClient(string id) {
            lock (lockobj) {
                foreach (var clientsBySource in registeredClients.Values) {
                    if (clientsBySource.ContainsKey(id)) {
                        clientsBySource[id].DateTime = DateTime.UtcNow;
                    }
                }

            }
        }

        private string ElectSyncLeader(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                return clientsWaitingForSync[source].Where(x => x.Value == true && registeredClients[source].Where(r => r.Key == x.Key && r.Value.DateTime > DateTime.UtcNow.AddSeconds(-30)).Select(y => y.Key) != null).Select(kvp => kvp.Key).FirstOrDefault();
            }
        }

        private void AddClientWaitingForSync(string id, bool canLead, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (!registeredClients[source].ContainsKey(id)) {
                    // In case a client missed to register or the server restarted in between add the client to the registered clients again
                    registeredClients[source].Add(id, new ClientSource(id));
                }

                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                if (clientsWaitingForSync[source].ContainsKey(id)) {
                    clientsWaitingForSync[source][id] = canLead;
                } else {
                    clientsWaitingForSync[source].Add(id, canLead);
                }
                Logger.Debug($"Add client {id} that canlead={canLead} to waiting for sync from {source}.");
            }
        }

        private void RemoveClientWaitingForSync(string id, string source) {
            lock(lockobj) {
                if (clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source].Remove(id);
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
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                return registeredClients[source].Where(x => x.Value.DateTime > DateTime.UtcNow.AddSeconds(-30)).Select(x => x.Key).Count();
            }
        }

        public override async Task<Empty> Register(ClientIdRequest request, ServerCallContext context) {
            
            AddClient(request.Clientid, request.Source);
            return new Empty();
        }

        public override async Task<Empty> Unregister(ClientIdRequest request, ServerCallContext context) {
            RemoveClient(request.Clientid, request.Source);
            return new Empty();
        }

        public override async Task<Empty> AnnounceToSync(AnnounceToSyncRequest request, ServerCallContext context) {
            lock (lockobj) {
                Logger.Info($"Client {request.Clientid} is announcing to sync");

                AddClientWaitingForSync(request.Clientid, request.Canlead, request.Source);

                var clientsForSync = NumberOfClientsWaitingForSync(request.Source);
                var totalClients = NumberOfTotalClients(request.Source);
                var statusInfo = $"{clientsForSync}/{totalClients} clients waiting for {request.Source}";
                Logger.Debug(statusInfo);
                SetStatus(request.Source, statusInfo);
                syncInProgress[request.Source] = true;
                return new Empty();
            }
        }

        public override async Task<LeaderReply> WaitForSyncStart(ClientIdRequest request, ServerCallContext context) {
            Logger.Info($"Client {request.Clientid} is waiting to sync");

            while (syncInProgress[request.Source] && ClientsAreWaitingForSync(request.Source)) {
                await Task.Delay(10);
            }

            lock (lockobj) {
                // If the syncLeader is not empty it was already elected            
                if (!syncLeader.ContainsKey(request.Source) || string.IsNullOrWhiteSpace(syncLeader[request.Source])) {
                    syncLeader[request.Source] = ElectSyncLeader(request.Source);
                    Logger.Debug($"Client {syncLeader[request.Source]} is leading sync");
                    if (string.IsNullOrEmpty(syncLeader[request.Source])) {
                        Logger.Error($"No instance could lead the {request.Source} sync! {Environment.NewLine}Registered Clients: {Environment.NewLine}{string.Join(Environment.NewLine, registeredClients[request.Source])}{Environment.NewLine}Clients Waiting For Sync:{Environment.NewLine}{string.Join(Environment.NewLine, clientsWaitingForSync[request.Source])}");
                        SetStatus(request.Source, $"No instance could lead the {request.Source} sync!");
                        syncInProgress[request.Source] = false;
                        syncLeader[request.Source] = string.Empty;
                    }
                }

                return new LeaderReply() { LeaderId = syncLeader[request.Source] };
            }
        }

        private bool ClientsAreWaitingForSync(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                var reg = registeredClients[source].Where(x => x.Value.DateTime > DateTime.UtcNow.AddSeconds(-30)).Select(x => x.Key).ToList();
                return (reg.Intersect(clientsWaitingForSync[source].Keys).Count() < reg.Count);
            }
        }

        public override async Task<Empty> SetSyncInProgress(ClientIdRequest request, ServerCallContext context) {
            Logger.Info($"Client {request.Clientid} marking sync as in progress");
            SetStatus(request.Source, $"{request.Source} sync in progress");
            lock (lockobj) {
                syncLeader[request.Source] = request.Clientid;
            }
            return new Empty();
        }

        public override async Task<Empty> WaitForSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            Logger.Info($"Client {request.Clientid} is waiting for sync completion {request.Source}");

            while (syncInProgress[request.Source] && SyncLeaderIsAlive(request.Source)) {
                await Task.Delay(10);
            }

            RemoveClientWaitingForSync(request.Clientid, request.Source);

            return new Empty();
        }

        public bool SyncLeaderIsAlive(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (registeredClients[source].ContainsKey(syncLeader[source]) && registeredClients[source][syncLeader[source]].DateTime > DateTime.UtcNow.AddSeconds(-30)) {
                    return true;
                } else {
                    // If the syncLeader is empty it was already cleaned
                    if (syncInProgress[source] && syncLeader.ContainsKey(source) && !string.IsNullOrWhiteSpace(syncLeader[source])) {
                        // The sync leader is dead
                        Logger.Warning("The sync leader did not respond in the last 30 seconds.");
                        syncInProgress[source] = false;
                        syncLeader[source] = string.Empty;
                    }
                    return false;
                }

            }
        }

        public override async Task<Empty> SetSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            lock (lockobj) {
                Logger.Info($"Client {request.Clientid} is marking sync to be complete");

                syncInProgress[request.Source] = false;
                syncLeader[request.Source] = string.Empty;
                RemoveClientWaitingForSync(request.Clientid, request.Source);

                SetStatus(request.Source, string.Empty);

                return new Empty();
            }
        }

        public override async Task<Empty> WithdrawFromSync(ClientIdRequest request, ServerCallContext context) {
            lock(lockobj) { 
                RemoveClientWaitingForSync(request.Clientid, request.Source);
                return new Empty();
            }
        }

        public override async Task<PingReply> Ping(ClientIdRequest request, ServerCallContext context) {
            Logger.Trace($"Client {request.Clientid} is pinging the server");
            UpdateClient(request.Clientid);

            return new PingReply() { Reply = "Pong" };
        }
    }
}