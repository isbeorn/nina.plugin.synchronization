using Grpc.Core;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using Synchronization.Service;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Instructions {
    [ExportMetadata("Name", "Synchronized Wait")]
    [ExportMetadata("Description", "An instruction to coordinate a synchronization wait between multiple instances of N.I.N.A. - each instance needs to place this instruction into its sequence. This can be used to sync up all instances for a new target.")]
    [ExportMetadata("Icon", "SyncWaitSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Utility")]
    [Export(typeof(ISequenceItem))]
    internal class SynchronizedWait : SequenceItem {
        private IProfileService profileService;
        private PluginOptionsAccessor pluginSettings;
        private readonly SyncRegistration registration;

        [ImportingConstructor]
        public SynchronizedWait(IProfileService profileService) : base() {
            this.profileService = profileService;

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
            registration = new SyncRegistration(client, nameof(SynchronizedWait));
        }
        private SynchronizedWait(SynchronizedWait cloneMe) : this(cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SynchronizedWait(this) {
            };
        }

        public override void AfterParentChanged() {
            var root = ItemUtility.GetRootContainer(this.Parent);
            if (root?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                Initialize();
            } else {
                Teardown();
            }
        }

        public override void Initialize() {
            try {
                registration.Initialize();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        public override void Teardown() {
            try {
                registration.Teardown();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private ISyncServiceClient client {
            get => SyncServiceClient.Instance;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var source = nameof(SynchronizedWait);
            var announced = false;
            var syncCompleted = false;

            token.ThrowIfCancellationRequested();
            registration.BeginExecution();
            try {
                var waitTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SynchronizationPlugin.DitherWaitTimeout), 300));

                Logger.Info("Waiting for synchronization");
                progress?.Report(new ApplicationStatus() { Status = "Waiting for synchronization" });

                await Task.Delay(200, token);
                announced = true;
                await client.AnnounceToSync(source, true, token);

                var isLeader = await client.WaitForSyncStart(source, token, waitTimeout);

                Logger.Info("All Synchronized");
                progress?.Report(new ApplicationStatus() { Status = "All Synchronized" });

                if (isLeader) {
                    try {
                        Logger.Info("This instance leads the sync");
                        await client.SetSyncInProgress(source, token);
                        await client.SetSyncComplete(source, token);
                        syncCompleted = true;

                        Logger.Info("Marking sync as complete");
                        progress?.Report(new ApplicationStatus() { Status = "Sync is complete" });
                    } catch (RpcException e) {
                        if (e.StatusCode == StatusCode.Cancelled) {
                            Logger.Info("The sync was cancelled - marking sync as complete");
                            await client.SetSyncComplete(source, CancellationToken.None);
                            syncCompleted = true;
                            throw new OperationCanceledException("The synchronized wait was cancelled", e, token);
                        }

                        throw;
                    } catch (OperationCanceledException) {
                        Logger.Info("The sync was cancelled - marking sync as complete");
                        await client.SetSyncComplete(source, CancellationToken.None);
                        syncCompleted = true;
                        throw;
                    }
                } else {
                    Logger.Info("Waiting for leader to sync");
                    progress?.Report(new ApplicationStatus() { Status = "Waiting for leader to sync" });
                    await client.WaitForSyncComplete(source, token, waitTimeout);
                    syncCompleted = true;
                }


            } catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled) {
                Logger.Info("The sync was cancelled");
                throw new OperationCanceledException("The synchronized wait was cancelled", e, token);
            } catch (OperationCanceledException) {
                Logger.Info("The sync was cancelled");
                throw;
            } finally {
                if (announced && !syncCompleted) {
                    try {
                        await client.WithdrawFromSync(source, CancellationToken.None);
                    } catch (Exception ex) {
                        Logger.Error("Failed to withdraw from synchronized wait", ex);
                    }
                }
                try {
                    registration.EndExecution();
                } catch (Exception ex) {
                    Logger.Error("Failed to release synchronized wait registration", ex);
                }
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override string ToString() {
            return $"Instruction: {nameof(SynchronizedWait)}";
        }
    }
}
