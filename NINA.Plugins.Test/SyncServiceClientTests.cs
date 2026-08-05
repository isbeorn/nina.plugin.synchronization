using Google.Protobuf;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using NINA.Synchronization.Service.Sync;
using NUnit.Framework;
using Synchronization.Service;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace NINA.Plugins.Test {
    [TestFixture]
    [NonParallelizable]
    public class SyncServiceClientTests {
        [Test]
        public void RuntimeDependencies_MatchNina3_3NightlyVersions() {
            AssertProductVersion(typeof(NamedPipeServer).Assembly, "3.1.0");
            AssertProductVersion(typeof(CallOptions).Assembly, "2.80.0");
            AssertProductVersion(typeof(IMessage).Assembly, "3.35.1");
        }

        [Test]
        public void StartHeartbeat_WhenNoRegistrationsExist_DoesNotRunHeartbeat() {
            var client = SyncServiceClient.Instance;

            try {
                ClearRegistrations(client);

                InvokeStartHeartbeat(client);

                Assert.That(IsHeartbeatRunning(client), Is.False);
            } finally {
                InvokeStopHeartbeat(client);
            }
        }

        [Test]
        public void UnregisterSync_KeepsHeartbeatRunningUntilAllRegistrationsAreRemoved() {
            var pipe = new NamedPipeServer("NINA.Synchronization.Service.Sync");
            SyncService.BindService(pipe.ServiceBinder, SyncServiceServer.Instance);
            pipe.Start();

            var client = SyncServiceClient.Instance;
            var sourceA = $"{TestContext.CurrentContext.Test.Name}-{Guid.NewGuid():N}-A";
            var sourceB = $"{TestContext.CurrentContext.Test.Name}-{Guid.NewGuid():N}-B";

            try {
                client.RegisterSync(sourceA);
                client.RegisterSync(sourceB);

                client.UnregisterSync(sourceA);

                Assert.That(IsHeartbeatRunning(client), Is.True);

                client.UnregisterSync(sourceB);

                Assert.That(IsHeartbeatRunning(client), Is.False);
            } finally {
                try {
                    client.UnregisterSync(sourceB);
                } catch {
                }

                pipe.Kill();
                pipe.Dispose();
            }
        }

        private static bool IsHeartbeatRunning(SyncServiceClient client) {
            var field = typeof(SyncServiceClient).GetField("heartbeatrunning", BindingFlags.Instance | BindingFlags.NonPublic);
            return (bool)field.GetValue(client);
        }

        private static void ClearRegistrations(SyncServiceClient client) {
            InvokeStopHeartbeat(client);
            var field = typeof(SyncServiceClient).GetField("registrationsBySource", BindingFlags.Instance | BindingFlags.NonPublic);
            var registrations = (Dictionary<string, int>)field.GetValue(client);
            registrations.Clear();
        }

        private static void InvokeStartHeartbeat(SyncServiceClient client) {
            var method = typeof(SyncServiceClient).GetMethod("StartHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(client, Array.Empty<object>());
        }

        private static void InvokeStopHeartbeat(SyncServiceClient client) {
            var method = typeof(SyncServiceClient).GetMethod("StopHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(client, Array.Empty<object>());
        }

        private static void AssertProductVersion(Assembly assembly, string expectedVersion) {
            var productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;

            Assert.That(productVersion, Does.StartWith(expectedVersion), $"Unexpected product version for {assembly.GetName().Name}.");
        }
    }
}
