using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NINA.Core.Utility;
using NINA.Synchronization.Service.Dither;
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
    public class DitherServiceServer : DitherService.DitherServiceBase {
        private static readonly Lazy<DitherServiceServer> lazy = new Lazy<DitherServiceServer>(() => new DitherServiceServer());
        public static DitherServiceServer Instance { get => lazy.Value; }

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

        private string ditherLeader;

        private DitherServiceServer() {
            registeredClients = new SortedDictionary<string, DateTime>();
            clientsWaitingForSync = new SortedDictionary<string, bool>();
            ditherInProgress = false;
            status = "idle";
        }

        private SortedDictionary<string, DateTime> registeredClients { get; }
        private SortedDictionary<string, bool> clientsWaitingForSync { get; }
        private bool ditherInProgress;

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

        private string ElectDitherLeader() {
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
                Logger.Info($"Client {request.Clientid} registered for sync dither");
                AddClient(request.Clientid);
            }
            return new Empty();
        }

        public override async Task<Empty> Unregister(ClientIdRequest request, ServerCallContext context) {
            if (IsRegistered(request.Clientid)) {
                Logger.Info($"Client {request.Clientid} unregistered sync dither");
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
            ditherInProgress = true;
            return new Empty();
        }

        public override async Task<LeaderReply> WaitForSync(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is waiting to sync");

            while (ditherInProgress && ClientsAreWaitingForSync()) {
                await Task.Delay(1000);
            }

            lock(lockobj) {
                ditherLeader = ElectDitherLeader();
                Logger.Debug($"Client {ditherLeader} is leading sync dither");
                if(string.IsNullOrEmpty(ditherLeader)) {
                    Status = "No instance could lead the dither! Make sure at least one instance is connected to a guider!";
                    ditherInProgress = false;
                    ditherLeader = string.Empty;
                    ClearClientWaitingForSync();
                }
            }

            return new LeaderReply() { LeaderId = ditherLeader };
        }

        private bool ClientsAreWaitingForSync() {
            lock (lockobj) {
                var reg = registeredClients.Where(x => x.Value > DateTime.UtcNow.AddSeconds(-10)).Select(x => x.Key).ToList();
                return (reg.Intersect(clientsWaitingForSync.Keys).Count() < reg.Count);
            }
        }

        public override async Task<Empty> SetDitherInProgress(ClientIdRequest request, ServerCallContext context) {
            Status = $"Dither in progress";
            ditherLeader = request.Clientid;
            return new Empty();
        }

        public override async Task<Empty> WaitForDither(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is announcing to want to dither");

            while (ditherInProgress && DitherLeaderIsAlive()) {
                await Task.Delay(1000);
            }

            return new Empty();
        }

        public bool DitherLeaderIsAlive() {
            lock(lockobj) {
                if(registeredClients.ContainsKey(ditherLeader) && registeredClients[ditherLeader] > DateTime.UtcNow.AddSeconds(-10)) {
                    return true;
                } else {
                    // The dither leader is dead
                    ditherInProgress = false;
                    ditherLeader = string.Empty;
                    ClearClientWaitingForSync();
                    return false;
                }
                
            }
        }

        public override async Task<Empty> SetDitherCompleted(ClientIdRequest request, ServerCallContext context) {
            Logger.Debug($"Client {request.Clientid} is setting dither to be complete");
            ditherInProgress = false;
            ditherLeader = string.Empty;
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