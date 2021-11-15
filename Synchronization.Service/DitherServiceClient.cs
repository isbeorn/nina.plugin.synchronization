using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using NINA.Synchronization.Service.Dither;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Service {
    public class DitherServiceClient : DitherService.DitherServiceClient, IDitherServiceClient {

        private static readonly Lazy<DitherServiceClient> lazy = new Lazy<DitherServiceClient>(() => new DitherServiceClient());
        public static DitherServiceClient Instance { get => lazy.Value; }

        private Guid id = Guid.NewGuid();

        private DitherServiceClient() : base(new NamedPipeChannel(".", "NINA.Synchronization.Service.Dither", new NamedPipeChannelOptions() { ConnectionTimeout = 300000 })) {
        }

        /// <summary>
        /// Register the client against the dither service
        /// </summary>
        public void RegisterSync() {
            base.Register(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
        }

        public override Empty Register(ClientIdRequest request, CallOptions options) {
            return base.Register(request, options);
        }

        /// <summary>
        /// Remove the client from the dither service
        /// </summary>
        public void UnregisterSync() {
             base.Unregister(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
        }

        /// <summary>
        /// Register the client against the dither service
        /// </summary>
        public async Task Register() {
            await base.RegisterAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
        }

        /// <summary>
        /// Remove the client from the dither service
        /// </summary>
        public async Task Unregister() {
            await base.UnregisterAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5));
        }

        /// <summary>
        /// Wait until the server sends that all clients are synced up
        /// </summary>
        /// <returns></returns>
        public async Task<bool> WaitForSync(CancellationToken ct) {
            var result = await base.WaitForSyncAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(300), cancellationToken: ct);
            if(string.IsNullOrEmpty(result.LeaderId)) { throw new Exception("No instance could lead the dither! Make sure at least one instance is connected to a guider!"); }
            return result.LeaderId == id.ToString();
        }

        /// <summary>
        /// Wait until the server sends that the dither has been completed by the leader
        /// </summary>
        /// <returns></returns>
        public async Task WaitForDither(CancellationToken ct) {
            await base.WaitForDitherAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(300), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has started the dither
        /// </summary>
        /// <returns></returns>
        public async Task SetDitherInProgress(CancellationToken ct) {
            await base.SetDitherInProgressAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has finished the dither
        /// </summary>
        /// <returns></returns>
        public async Task SetDitherCompleted(CancellationToken ct) {
            await base.SetDitherCompletedAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task AnnounceToSync(bool canLead, CancellationToken ct) {
            await base.AnnounceToSyncAsync(new AnnounceToSyncRequest() { Clientid = id.ToString(), Canlead = canLead }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task<string> Ping(CancellationToken ct) {
            var resp =  await base.PingAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            return resp.Reply;
        }
    }
}
