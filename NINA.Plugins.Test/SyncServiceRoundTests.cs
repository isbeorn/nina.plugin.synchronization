using Grpc.Core;
using GrpcDotNetNamedPipes;
using NINA.Synchronization.Service.Sync;
using NUnit.Framework;
using Synchronization.Service;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.Test {
    [TestFixture]
    [NonParallelizable]
    public class SyncServiceRoundTests {
        private const string WaitSource = "SynchronizedWait";
        private const string DitherSource = "SynchronizedDither";
        private SyncServiceServer server;
        private NamedPipeServer pipe;
        private SyncService.SyncServiceClient client;

        [SetUp]
        public void SetUp() {
            server = (SyncServiceServer)Activator.CreateInstance(typeof(SyncServiceServer), nonPublic: true);
            var pipeName = "NINA.SyncRounds." + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServer(pipeName);
            SyncService.BindService(pipe.ServiceBinder, server);
            pipe.Start();
            client = new SyncService.SyncServiceClient(new NamedPipeChannel(".", pipeName,
                new NamedPipeChannelOptions { ConnectionTimeout = 2000 }));
        }

        [TearDown]
        public void TearDown() {
            pipe?.Kill();
            pipe?.Dispose();
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public async Task Reannouncement_WithUnconsumedCompletion_WaitsForTheNextRound(string source) {
            await ReadyRound(source);
            await client.SetSyncCompletedAsync(Request("a", source), Options());
            // Reproduce the state left by the loop-exit race: the follower has not consumed its completion.
            await client.AnnounceToSyncAsync(Announcement("b", source), Options());
            using var waiting = client.WaitForSyncStartAsync(Request("b", source), Options());
            var first = await Task.WhenAny(waiting.ResponseAsync, Task.Delay(100));
            await client.AnnounceToSyncAsync(Announcement("a", source), Options());
            var leader = await waiting;
            Assert.That(first, Is.Not.EqualTo(waiting.ResponseAsync), "Previous completion released the next barrier.");
            Assert.That(leader.LeaderId, Is.EqualTo("a"));
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public async Task Completion_WhileFollowerIsWaiting_DoesNotReleaseTheNextRound(string source) {
            await ReadyRound(source);
            using var waiting = client.WaitForSyncCompletedAsync(Request("b", source), Options());
            await client.SetSyncCompletedAsync(Request("a", source), Options());
            // A fast leader can announce again before the slow follower has observed completion.
            await client.AnnounceToSyncAsync(Announcement("a", source), Options());
            await waiting;
            using var next = client.WaitForSyncStartAsync(Request("a", source), Options());
            var first = await Task.WhenAny(next.ResponseAsync, Task.Delay(100));
            await client.AnnounceToSyncAsync(Announcement("b", source), Options());
            await next;
            Assert.That(first, Is.Not.EqualTo(next.ResponseAsync), "The next round must still wait for the follower.");
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public async Task Completion_RacingTheWaiter_AlwaysConsumesTheCompletionMarker(string source) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var context = TestServerCallContext.Create(cts.Token);
            await server.Register(Request("a", source), context);
            await server.Register(Request("b", source), context);
            for (var round = 0; round < 200; round++) {
                await server.AnnounceToSync(Announcement("a", source), context);
                await server.AnnounceToSync(Announcement("b", source), context);
                await server.WaitForSyncStart(Request("a", source), context);
                await server.WaitForSyncStart(Request("b", source), context);
                await server.SetSyncInProgress(Request("a", source), context);

                await Task.WhenAll(
                    Task.Run(() => server.WaitForSyncCompleted(Request("b", source), context)),
                    Task.Run(() => server.SetSyncCompleted(Request("a", source), context)));

                // Inspect only after both calls have completed, before reannouncement can clear a leaked marker.
                var property = typeof(SyncServiceServer).GetProperty("completedSyncLeaderByClient", BindingFlags.Instance | BindingFlags.NonPublic);
                var completions = (Dictionary<string, Dictionary<string, string>>)property.GetValue(server);
                Assert.That(completions.TryGetValue(source, out var clients) && clients.ContainsKey("b"), Is.False,
                    $"The completion waiter leaked its marker on round {round}.");
            }
        }

        [TestCase(WaitSource, false)]
        [TestCase(DitherSource, false)]
        [TestCase(WaitSource, true)]
        [TestCase(DitherSource, true)]
        public async Task FailedWait_WithdrawsBeforeRetrying(string source, bool deadline) {
            await client.RegisterAsync(Request("a", source), Options());
            await client.RegisterAsync(Request("b", source), Options());
            await client.AnnounceToSyncAsync(Announcement("b", source), Options());
            using var cts = new CancellationTokenSource();
            using var waiting = client.WaitForSyncStartAsync(Request("b", source),
                new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(deadline ? 100 : 5000), cancellationToken: cts.Token));
            if (!deadline) {
                cts.Cancel();
            }
            var error = Assert.ThrowsAsync<RpcException>(async () => await waiting);
            Assert.That(error.StatusCode, Is.EqualTo(deadline ? StatusCode.DeadlineExceeded : StatusCode.Cancelled));
            // Match the instruction's finally block before retrying the same client and source.
            await client.WithdrawFromSyncAsync(Request("b", source), Options());
            await client.AnnounceToSyncAsync(Announcement("b", source), Options());
            await client.AnnounceToSyncAsync(Announcement("a", source), Options());
            var leader = await client.WaitForSyncStartAsync(Request("a", source), Options());
            Assert.That(leader.LeaderId, Is.EqualTo("a"));
            await client.SetSyncCompletedAsync(Request("a", source), Options());
            Assert.That((await client.WaitForSyncStartAsync(Request("b", source), Options())).LeaderId, Is.EqualTo("a"));
            await client.WaitForSyncCompletedAsync(Request("b", source), Options());
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public async Task FourClients_Complete100ConsecutiveRoundsWithoutSkipping(string source) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ids = new[] { "a", "b", "c", "d" };
            var arrivals = new int[100];
            var completedRound = -1;
            foreach (var id in ids) {
                await client.RegisterAsync(Request(id, source), cancellationToken: cts.Token);
            }
            await Task.WhenAll(ids.Select(async id => {
                for (var round = 0; round < arrivals.Length; round++) {
                    if (id == "d" && round % 3 == 0) {
                        await Task.Delay(1, cts.Token);
                    }
                    Interlocked.Increment(ref arrivals[round]);
                    await client.AnnounceToSyncAsync(Announcement(id, source), cancellationToken: cts.Token);
                    var leader = await client.WaitForSyncStartAsync(Request(id, source), cancellationToken: cts.Token);
                    Assert.That(Volatile.Read(ref arrivals[round]), Is.EqualTo(4), $"Client {id} skipped barrier {round}.");
                    Assert.That(leader.LeaderId, Is.EqualTo("a"));
                    if (id == "a") {
                        await client.SetSyncInProgressAsync(Request(id, source), cancellationToken: cts.Token);
                        if (round % 2 == 0) {
                            await Task.Delay(1, cts.Token);
                        }
                        Volatile.Write(ref completedRound, round);
                        await client.SetSyncCompletedAsync(Request(id, source), cancellationToken: cts.Token);
                    } else {
                        await client.WaitForSyncCompletedAsync(Request(id, source), cancellationToken: cts.Token);
                        Assert.That(Volatile.Read(ref completedRound), Is.GreaterThanOrEqualTo(round),
                            $"Client {id} left barrier {round} before leader completion.");
                    }
                }
            }));
        }

        private async Task ReadyRound(string source) {
            foreach (var id in new[] { "a", "b" }) {
                await client.RegisterAsync(Request(id, source), Options());
                await client.AnnounceToSyncAsync(Announcement(id, source), Options());
            }
            Assert.That((await client.WaitForSyncStartAsync(Request("a", source), Options())).LeaderId, Is.EqualTo("a"));
            Assert.That((await client.WaitForSyncStartAsync(Request("b", source), Options())).LeaderId, Is.EqualTo("a"));
            await client.SetSyncInProgressAsync(Request("a", source), Options());
        }

        private static ClientIdRequest Request(string id, string source) => new ClientIdRequest { Clientid = id, Source = source };
        private static AnnounceToSyncRequest Announcement(string id, string source) =>
            new AnnounceToSyncRequest { Clientid = id, Source = source, Canlead = id == "a" || source == WaitSource };
        private static CallOptions Options() => new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5));
    }
}
