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

        [ImportingConstructor]
        public SynchronizedWait(IProfileService profileService) : base() {
            this.profileService = profileService;

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
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
            client.RegisterSync(nameof(SynchronizedWait));
        }

        public override void Teardown() {
            client.UnregisterSync(nameof(SynchronizedWait));
        }

        private ISyncServiceClient client {
            get => SyncServiceClient.Instance;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var waitTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SynchronizationPlugin.DitherWaitTimeout), 300));

                Logger.Debug("Waiting for synchronization");
                progress?.Report(new ApplicationStatus() { Status = "Waiting for synchronization" });

                await client.AnnounceToSync(nameof(SynchronizedWait), true, token);

                var isLeader = await client.WaitForSyncStart(nameof(SynchronizedWait), token, waitTimeout);

                progress?.Report(new ApplicationStatus() { Status = "All Synchronized" });

                if (isLeader) {
                    try {
                        await client.SetSyncInProgress(nameof(SynchronizedWait), token);
                        await client.SetSyncComplete(nameof(SynchronizedWait), token);

                        progress?.Report(new ApplicationStatus() { Status = "Sync is complete" });
                    } catch (RpcException e) {
                        if (e.StatusCode == StatusCode.Cancelled) {
                            Logger.Debug("The sync was cancelled - marking sync as complete");
                            await client.SetSyncComplete(nameof(SynchronizedWait), new CancellationToken());
                        }
                    } catch (OperationCanceledException) {
                        Logger.Debug("The sync was cancelled - marking sync as complete");
                        await client.SetSyncComplete(nameof(SynchronizedWait), new CancellationToken());
                    }
                } else {
                    Logger.Debug("Waiting for leader to dither");
                    progress?.Report(new ApplicationStatus() { Status = "Waiting for leader to Sync" });
                    await client.WaitForSyncComplete(nameof(SynchronizedWait), token, waitTimeout);
                }


            } catch (RpcException e) {
                if (e.StatusCode == StatusCode.Cancelled) {
                    throw new OperationCanceledException();
                } else {
                    throw;
                }
            } finally {
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override string ToString() {
            return $"Instruction: {nameof(SynchronizedWait)}";
        }
    }
}
