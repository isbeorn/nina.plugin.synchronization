using Grpc.Core;
using Newtonsoft.Json;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces.ViewModel;
using Synchronization.Service;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Instructions {

    [ExportMetadata("Name", "Synchronized Dither")]
    [ExportMetadata("Description", "An instruction to coordinate a dither between multiple instances of N.I.N.A. - each instance needs to place this trigger into its sequence.")]
    [ExportMetadata("Icon", "SyncDitherSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Guider")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SynchronizedDither : SequenceTrigger, IValidatable {
        private IGuiderMediator guiderMediator;
        private IImageHistoryVM history;
        private IProfileService profileService;

        [ImportingConstructor]
        public SynchronizedDither(IGuiderMediator guiderMediator, IImageHistoryVM history, IProfileService profileService) : base() {
            this.guiderMediator = guiderMediator;
            this.history = history;
            this.profileService = profileService;
            AfterExposures = 1;
            TriggerRunner.Add(new Dither(guiderMediator, profileService));

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private SynchronizedDither(SynchronizedDither cloneMe) : this(cloneMe.guiderMediator, cloneMe.history, cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SynchronizedDither(this) {
                AfterExposures = AfterExposures,
                TriggerRunner = (SequentialContainer)TriggerRunner.Clone()
            };
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = ImmutableList.CreateRange(value);
                RaisePropertyChanged();
            }
        }

        public override string ToString() {
            return $"Trigger: {nameof(SynchronizedDither)}, AfterExposures: {AfterExposures}";
        }

        public bool Validate() {
            var i = new List<string>();            

            Issues = i;
            return i.Count == 0;
        }

        private int lastTriggerId = 0;
        private int afterExposures;

        [JsonProperty]
        public int AfterExposures {
            get => afterExposures;
            set {
                afterExposures = value;
                RaisePropertyChanged();
            }
        }

        public override void AfterParentChanged() {
            if (ItemUtility.IsInRootContainer(Parent) && this.Parent.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                SequenceBlockInitialize();
            } else {
                SequenceBlockTeardown();
            }
        }

        private ISyncServiceClient client {
            get => SyncServiceClient.Instance;
        }

        public override void SequenceBlockInitialize() {
            try {
                client.RegisterSync(nameof(SynchronizedDither));
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        public override void SequenceBlockTeardown() {
            try {
                client.UnregisterSync(nameof(SynchronizedDither));
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private PluginOptionsAccessor pluginSettings;

        public int ProgressExposures {
            get => AfterExposures > 0 ? history.ImageHistory.Count % AfterExposures : 0;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                if (AfterExposures > 0) {
                    try {
                        var waitTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SynchronizationPlugin.DitherWaitTimeout), 300));
                        lastTriggerId = history.ImageHistory.Count;
                        Logger.Debug("Waiting for synchronization");
                        progress?.Report(new ApplicationStatus() { Status = "Waiting for synchronization" });
                        var info = guiderMediator.GetInfo();
                        await client.AnnounceToSync(nameof(SynchronizedDither), info.Connected, token);
                        var isLeader = await client.WaitForSyncStart(nameof(SynchronizedDither), token, waitTimeout);

                        progress?.Report(new ApplicationStatus() { Status = "All Synchronized" });
                        if (isLeader) {
                            try {
                                Logger.Debug("This instance leads the dither");
                                await client.SetSyncInProgress(nameof(SynchronizedDither), token);
                                progress?.Report(new ApplicationStatus() { Status = "This instance leads the dither" });
                                await TriggerRunner.Run(progress, token);
                                Logger.Debug("Marking dither as complete");
                                await client.SetSyncComplete(nameof(SynchronizedDither), token);
                            } catch (RpcException e) {
                                if (e.StatusCode == StatusCode.Cancelled) {
                                    Logger.Debug("The dither was cancelled - marking dither as complete");
                                    await client.SetSyncComplete(nameof(SynchronizedDither), new CancellationToken());
                                }
                            } catch (OperationCanceledException) {
                                Logger.Debug("The dither was cancelled - marking dither as complete");
                                await client.SetSyncComplete(nameof(SynchronizedDither), new CancellationToken());                                
                            }

                            progress?.Report(new ApplicationStatus() { Status = "Dither is complete" });
                        } else {
                            Logger.Debug("Waiting for leader to dither");
                            progress?.Report(new ApplicationStatus() { Status = "Waiting for leader to dither" });
                            await client.WaitForSyncComplete(nameof(SynchronizedDither), token, waitTimeout);
                        }
                    } catch (RpcException e) {
                        if (e.StatusCode == StatusCode.Cancelled) {
                            throw new OperationCanceledException();
                        } else {
                            throw;
                        }
                    }
                } else {
                    return;
                }
            } catch (OperationCanceledException) {
                await client.WithdrawFromSync(nameof(SynchronizedDither), new CancellationToken());
            } finally {
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (previousItem == null && nextItem == null) { return false; }

            RaisePropertyChanged(nameof(ProgressExposures));
            var shouldTrigger = lastTriggerId < history.ImageHistory.Count && history.ImageHistory.Count > 0 && ProgressExposures == 0;

            if (shouldTrigger) {
                if (ItemUtility.IsTooCloseToMeridianFlip(Parent, TriggerRunner.GetItemsSnapshot().First().GetEstimatedDuration())) {
                    Logger.Warning("Dither should be triggered, however the meridian flip is too close to be executed");
                    shouldTrigger = false;
                }
            }

            return shouldTrigger;
        }
    }
}