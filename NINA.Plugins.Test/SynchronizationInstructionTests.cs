using Grpc.Core;
using GrpcDotNetNamedPipes;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Synchronization.Service.Sync;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Model;
using NUnit.Framework;
using Synchronization.Instructions;
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
    public class SynchronizationInstructionTests {
        private const string WaitSource = nameof(SynchronizedWait);
        private const string DitherSource = nameof(SynchronizedDither);
        private const string PeerId = "00000000-0000-0000-0000-000000000000";
        private SyncServiceServer server;
        private NamedPipeServer pipe;
        private SyncService.SyncServiceClient peer;
        private Mock<IProfileService> profile;
        private Mock<IGuiderMediator> guider;
        private Mock<IImageHistoryVM> history;

        [OneTimeSetUp]
        public void StartServer() {
            server = (SyncServiceServer)Activator.CreateInstance(typeof(SyncServiceServer), nonPublic: true);
            pipe = new NamedPipeServer("NINA.Synchronization.Service.Sync");
            SyncService.BindService(pipe.ServiceBinder, server);
            pipe.Start();
            peer = new SyncService.SyncServiceClient(new NamedPipeChannel(".", "NINA.Synchronization.Service.Sync",
                new NamedPipeChannelOptions { ConnectionTimeout = 2000 }));
        }

        [SetUp]
        public void SetUp() {
            profile = new Mock<IProfileService>();
            profile.SetupGet(x => x.ActiveProfile.PluginSettings).Returns(new PluginSettings());
            guider = new Mock<IGuiderMediator>();
            guider.Setup(x => x.GetInfo()).Returns(new GuiderInfo { Connected = true });
            history = new Mock<IImageHistoryVM>();
            history.SetupGet(x => x.ImageHistory).Returns(new List<ImageHistoryPoint>());
        }

        [TearDown]
        public void TearDown() {
            // Also clean up registrations leaked by the unfixed implementation during reproduction.
            foreach (var registration in Registrations().ToArray()) {
                for (var i = 0; i < registration.Value; i++) {
                    SyncServiceClient.Instance.UnregisterSync(registration.Key);
                }
            }
            peer.Unregister(Request(WaitSource), Options());
            peer.Unregister(Request(DitherSource), Options());
        }

        [OneTimeTearDown]
        public async Task StopServer() {
            pipe?.Kill();
            pipe?.Dispose();
            // Kill cancels the transport's listener threads without waiting for their pipe handles to close.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntil(() => !global::Synchronization.SynchronizationPlugin.NamedPipeExist("NINA.Synchronization.Service.Sync"), cts.Token);
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void Initialize_RepeatedCallsOwnOnlyOneRegistration(string source) {
            var item = CreateItem(source);
            item.Initialize();
            item.Initialize();
            Assert.That(RegistrationCount(source), Is.EqualTo(1));
            item.Teardown();
            Assert.That(RegistrationCount(source), Is.Zero);
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void Teardown_UninitializedItemDoesNotUnregisterAnotherItem(string source) {
            var active = CreateItem(source);
            active.Initialize();
            var unused = CreateItem(source);
            unused.Teardown();
            unused.Teardown();
            Assert.That(RegistrationCount(source), Is.EqualTo(1));
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public void ParentNotifications_AndContainerCloningKeepRegistrationsBalanced(string source) {
            var item = CreateItem(source);
            var container = AttachToRunningContainer(item);
            container.AfterParentChanged();
            container.AfterParentChanged();
            Assert.That(RegistrationCount(source), Is.EqualTo(1), "Repeated parent notifications");

            var clone = (SequentialContainer)container.Clone();
            Assert.That(RegistrationCount(source), Is.EqualTo(1), "Cloning into an unattached container");
            clone.AttachNewParent(container.Parent);
            Assert.That(RegistrationCount(source), Is.EqualTo(2), "Attaching a separate live clone");
            clone.AttachNewParent(null);
            Assert.That(RegistrationCount(source), Is.EqualTo(1), "Detaching only the clone");
            container.AttachNewParent(null);
            Assert.That(RegistrationCount(source), Is.Zero);
            container.AttachNewParent(null);
            Assert.That(RegistrationCount(source), Is.Zero, "Repeated teardown");
            AttachToRunningContainer(item);
            Assert.That(RegistrationCount(source), Is.EqualTo(1), "Restart after teardown");
        }

        [TestCase(WaitSource, false)]
        [TestCase(DitherSource, false)]
        [TestCase(WaitSource, true)]
        [TestCase(DitherSource, true)]
        public async Task ActiveWait_SurvivesContainerCloningOrDetach(string source, bool detach) {
            await peer.RegisterAsync(Request(source), Options());
            var item = CreateItem(source);
            var container = AttachToRunningContainer(item);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = Execute(item, cts.Token);
            try {
                await WaitUntil(() => server.GetStatus() == $"1/2 clients waiting for {source}", cts.Token);
                if (detach) {
                    container.AttachNewParent(null);
                } else {
                    _ = container.Clone();
                }
                Assert.That(RegistrationCount(source), Is.EqualTo(1), "An active instruction must keep its own registration.");
                Assert.That(execution.IsCompleted, Is.False, "The peer has not arrived yet.");
                await CompletePeerRound(source);
                await execution;
                Assert.That(RegistrationCount(source), Is.EqualTo(detach ? 0 : 1));
            } finally {
                cts.Cancel();
                try { await execution; } catch { }
            }
        }

        [TestCase(WaitSource)]
        [TestCase(DitherSource)]
        public async Task Execute_WithoutInitializationRegistersForItsLifetime(string source) {
            await peer.RegisterAsync(Request(source), Options());
            var item = CreateItem(source);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = Execute(item, cts.Token);
            try {
                await WaitUntil(() => server.GetStatus() == $"1/2 clients waiting for {source}", cts.Token);
                Assert.That(RegistrationCount(source), Is.EqualTo(1), "Execution must also run the client's heartbeat.");
                await CompletePeerRound(source);
                await execution;
                Assert.That(RegistrationCount(source), Is.Zero);
            } finally {
                cts.Cancel();
                try { await execution; } catch { }
            }
        }

        [TestCase(WaitSource, false)]
        [TestCase(DitherSource, false)]
        [TestCase(WaitSource, true)]
        [TestCase(DitherSource, true)]
        public async Task CanceledOrTimedOutExecution_ReleasesRegistrationAndCanRetry(string source, bool timeout) {
            var pluginId = new Guid("c60db3b2-9a08-4b24-8938-bfb01e770eb9");
            profile.Object.ActiveProfile.PluginSettings.SetValue(pluginId, "DitherWaitTimeout", 1);
            await peer.RegisterAsync(Request(source), Options());
            var item = CreateItem(source);
            item.Initialize();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = Execute(item, cts.Token);
            await WaitUntil(() => server.GetStatus() == $"1/2 clients waiting for {source}", cts.Token);
            item.Teardown();
            Assert.That(RegistrationCount(source), Is.EqualTo(1));
            if (timeout) {
                var error = Assert.ThrowsAsync<RpcException>(async () => await execution);
                Assert.That(error.StatusCode, Is.EqualTo(StatusCode.DeadlineExceeded));
            } else {
                cts.Cancel();
                Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await execution);
            }
            Assert.That(RegistrationCount(source), Is.Zero);
            Assert.That(server.GetStatus(), Is.EqualTo(SyncServiceServer.IdleString));

            using var retryCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var retry = Execute(item, retryCts.Token);
            try {
                await WaitUntil(() => server.GetStatus() == $"1/2 clients waiting for {source}", retryCts.Token);
                await CompletePeerRound(source);
                await retry;
                Assert.That(RegistrationCount(source), Is.Zero);
            } finally {
                retryCts.Cancel();
                try { await retry; } catch { }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Dither_OnlyTheGuiderConnectedClientPerformsTheDither(bool localGuiderConnected) {
            await peer.RegisterAsync(Request(DitherSource), Options());
            var item = CreateItem(DitherSource);
            guider.Setup(x => x.GetInfo()).Returns(new GuiderInfo { Connected = localGuiderConnected });
            guider.Setup(x => x.Dither(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = Execute(item, cts.Token);
            try {
                await WaitUntil(() => server.GetStatus() == $"1/2 clients waiting for {DitherSource}", cts.Token);
                await peer.AnnounceToSyncAsync(new AnnounceToSyncRequest {
                    Clientid = PeerId, Source = DitherSource, Canlead = !localGuiderConnected
                }, Options());
                var leader = await peer.WaitForSyncStartAsync(Request(DitherSource), Options());
                if (localGuiderConnected) {
                    Assert.That(leader.LeaderId, Is.Not.EqualTo(PeerId));
                    await peer.WaitForSyncCompletedAsync(Request(DitherSource), Options());
                } else {
                    Assert.That(leader.LeaderId, Is.EqualTo(PeerId));
                    Assert.That(execution.IsCompleted, Is.False);
                    await peer.SetSyncInProgressAsync(Request(DitherSource), Options());
                    await peer.SetSyncCompletedAsync(Request(DitherSource), Options());
                }
                await execution;
                guider.Verify(x => x.Dither(It.IsAny<CancellationToken>()), Times.Exactly(localGuiderConnected ? 1 : 0));
                Assert.That(RegistrationCount(DitherSource), Is.Zero);
            } finally {
                cts.Cancel();
                try { await execution; } catch { }
            }
        }

        [Test]
        public async Task CanceledDitherLeader_ReleasesFollowerBeforeUnregistering() {
            await peer.RegisterAsync(Request(DitherSource), Options());
            var item = CreateItem(DitherSource);
            var ditherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            guider.Setup(x => x.Dither(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) => {
                ditherStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = Execute(item, cts.Token);
            try {
                await WaitUntil(() => server.GetStatus() == $"1/2 clients waiting for {DitherSource}", cts.Token);
                await peer.AnnounceToSyncAsync(new AnnounceToSyncRequest {
                    Clientid = PeerId, Source = DitherSource, Canlead = false
                }, Options());
                await peer.WaitForSyncStartAsync(Request(DitherSource), Options());
                using var follower = peer.WaitForSyncCompletedAsync(Request(DitherSource), Options());
                await ditherStarted.Task.WaitAsync(cts.Token);
                item.Teardown();
                cts.Cancel();
                Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await execution);
                await follower;
                Assert.That(RegistrationCount(DitherSource), Is.Zero);
                Assert.That(server.GetStatus(), Is.EqualTo(SyncServiceServer.IdleString));
            } finally {
                cts.Cancel();
                try { await execution; } catch { }
            }
        }

        private ISequenceEntity CreateItem(string source) {
            return source == WaitSource
                ? new SynchronizedWait(profile.Object)
                : new SynchronizedDither(guider.Object, history.Object, profile.Object);
        }

        private static SequentialContainer AttachToRunningContainer(ISequenceEntity item) {
            var container = new SequentialContainer();
            if (item is ISequenceItem instruction) {
                container.Add(instruction);
            } else {
                container.Add((ISequenceTrigger)item);
            }
            var root = new Mock<ISequenceRootContainer>();
            root.SetupGet(x => x.Status).Returns(SequenceEntityStatus.RUNNING);
            container.AttachNewParent(root.Object);
            return container;
        }

        private static Task Execute(ISequenceEntity item, CancellationToken token) {
            return item is SynchronizedWait wait
                ? wait.Execute(null, token)
                : ((SynchronizedDither)item).Execute(null, null, token);
        }

        private async Task CompletePeerRound(string source) {
            await peer.AnnounceToSyncAsync(new AnnounceToSyncRequest { Clientid = PeerId, Source = source, Canlead = true }, Options());
            var leader = await peer.WaitForSyncStartAsync(Request(source), Options());
            Assert.That(leader.LeaderId, Is.EqualTo(PeerId));
            await peer.SetSyncInProgressAsync(Request(source), Options());
            await peer.SetSyncCompletedAsync(Request(source), Options());
        }

        private static async Task WaitUntil(Func<bool> condition, CancellationToken token) {
            while (!condition()) {
                await Task.Delay(10, token);
            }
        }

        private static Dictionary<string, int> Registrations() {
            var field = typeof(SyncServiceClient).GetField("registrationsBySource", BindingFlags.Instance | BindingFlags.NonPublic);
            return (Dictionary<string, int>)field.GetValue(SyncServiceClient.Instance);
        }

        private static int RegistrationCount(string source) => Registrations().TryGetValue(source, out var count) ? count : 0;
        private static ClientIdRequest Request(string source) => new ClientIdRequest { Clientid = PeerId, Source = source };
        private static CallOptions Options() => new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5));
    }
}
