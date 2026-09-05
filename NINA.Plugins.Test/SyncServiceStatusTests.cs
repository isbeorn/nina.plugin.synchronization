using Grpc.Core;
using GrpcDotNetNamedPipes;
using NINA.Synchronization.Service.Sync;
using NUnit.Framework;
using Synchronization.Service;
using System;
using System.Collections.Generic;

namespace NINA.Plugins.Test {
    [TestFixture]
    [NonParallelizable]
    public class SyncServiceStatusTests {
        private const string WaitSource = "SynchronizedWait";
        private const string DitherSource = "SynchronizedDither";
        private SyncServiceServer server;
        private NamedPipeServer pipe;
        private SyncService.SyncServiceClient client;

        [SetUp]
        public void SetUp() {
            // Isolate status from the singleton used by the existing coordination tests.
            server = (SyncServiceServer)Activator.CreateInstance(typeof(SyncServiceServer), nonPublic: true);
            var pipeName = $"NINA.Synchronization.StatusTest.{Guid.NewGuid():N}";
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

        [Test]
        public void GetStatus_AfterBothSequencesRegister_RemainsIdle() {
            RegisterSequence("master");
            RegisterSequence("slave");

            Assert.That(server.GetStatus(), Is.EqualTo(SyncServiceServer.IdleString));
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void GetStatus_AfterUnregisterWithoutAnnouncing_RemainsIdle(string source) {
            Register("master", source);
            Register("slave", source);

            Unregister("slave", source);

            Assert.That(server.GetStatus(), Is.EqualTo(SyncServiceServer.IdleString));
        }

        [TestCase(WaitSource, 6)]
        [TestCase(DitherSource, 2)]
        public void Unregister_WithOtherRegistrationsRemaining_PreservesWaitingCount(string source, int registrations) {
            Register("master", source);
            Register("slave", source, registrations);

            Unregister("slave", source, registrations - 1);
            Announce("master", source);

            Assert.That(server.GetStatus(), Is.EqualTo($"1/2 clients waiting for {source}"));
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void GetStatus_AfterRepeatedIdleRestarts_RemainsIdle(string source) {
            var statuses = new List<string>();
            for (var cycle = 0; cycle < 3; cycle++) {
                Register("slave", source);
                statuses.Add(server.GetStatus());
                Unregister("slave", source);
                statuses.Add(server.GetStatus());
            }

            Assert.That(statuses, Is.All.EqualTo(SyncServiceServer.IdleString));
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void GetStatus_AfterRepeatedWithdrawal_RemainsIdle(string source) {
            Register("master", source);
            Register("slave", source);
            Announce("slave", source);

            client.WithdrawFromSync(Request("slave", source), CallOptions());
            Assert.That(server.GetStatus(), Is.EqualTo(SyncServiceServer.IdleString), "First withdrawal");

            client.WithdrawFromSync(Request("slave", source), CallOptions());
            Assert.That(server.GetStatus(), Is.EqualTo(SyncServiceServer.IdleString), "Repeated withdrawal");
        }

        [TestCase(WaitSource, DitherSource)]
        [TestCase(DitherSource, WaitSource)]
        public void GetStatus_WhenOtherSourceUnregisters_PreservesActiveStatus(string activeSource, string idleSource) {
            Register("master", activeSource);
            Register("slave", activeSource);
            Announce("master", activeSource);
            var activeStatus = server.GetStatus();

            Register("slave", idleSource);
            Unregister("slave", idleSource);

            Assert.That(server.GetStatus(), Is.EqualTo(activeStatus));
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void Synchronization_OnFirstAttempt_CompletesWithoutRestart(string source) {
            CompleteSync(source);
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void GetStatus_AfterCompletedSynchronization_ReturnsIdle(string source) {
            CompleteSync(source);

            Assert.That(server.GetStatus(), Is.EqualTo(SyncServiceServer.IdleString));
        }

        [TestCase("master")]
        [TestCase("slave")]
        public void GetStatus_AfterSequenceStopAndRestart_RemainsIdle(string restartedClient) {
            // Match the six wait instructions and two dither triggers in the reported logs.
            RegisterSequence("master");
            RegisterSequence("slave");

            Unregister(restartedClient, WaitSource, 6);
            Unregister(restartedClient, DitherSource, 2);
            var stoppedStatus = server.GetStatus();
            RegisterSequence(restartedClient);
            var restartedStatus = server.GetStatus();

            Assert.That(new[] { stoppedStatus, restartedStatus }, Is.All.EqualTo(SyncServiceServer.IdleString));
        }

        private void CompleteSync(string source) {
            Register("master", source);
            Register("slave", source);
            Announce("master", source);
            Announce("slave", source, canLead: source == WaitSource);

            var leader = client.WaitForSyncStart(Request("master", source), CallOptions());
            Assert.That(leader.LeaderId, Is.EqualTo("master"));
            client.SetSyncInProgress(Request("master", source), CallOptions());
            client.SetSyncCompleted(Request("master", source), CallOptions());

            var follower = client.WaitForSyncStart(Request("slave", source), CallOptions());
            Assert.That(follower.LeaderId, Is.EqualTo("master"));
            client.WaitForSyncCompleted(Request("slave", source), CallOptions());
        }

        private void RegisterSequence(string id) {
            Register(id, WaitSource, 6);
            Register(id, DitherSource, 2);
        }

        private void Register(string id, string source, int count = 1) {
            for (var i = 0; i < count; i++) {
                client.Register(Request(id, source), CallOptions());
            }
        }

        private void Unregister(string id, string source, int count = 1) {
            for (var i = 0; i < count; i++) {
                client.Unregister(Request(id, source), CallOptions());
            }
        }

        private void Announce(string id, string source, bool canLead = true) {
            client.AnnounceToSync(new AnnounceToSyncRequest {
                Clientid = id,
                Source = source,
                Canlead = canLead
            }, CallOptions());
        }

        private static ClientIdRequest Request(string id, string source) {
            return new ClientIdRequest { Clientid = id, Source = source };
        }

        private static CallOptions CallOptions() {
            return new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5));
        }
    }
}
