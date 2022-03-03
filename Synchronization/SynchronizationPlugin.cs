using GrpcDotNetNamedPipes;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using Synchronization.Service;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization {

    [Export(typeof(IPluginManifest))]
    public class SynchronizationPlugin : PluginBase {

        [ImportingConstructor]
        public SynchronizationPlugin(IProfileService profileService, IApplicationStatusMediator statusMediator) {
            mutexid = $"Global\\{this.Identifier}";
            this.statusMediator = statusMediator;

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private NamedPipeServer pipe;
        private CancellationTokenSource cts;
        private bool isServer = false;

        private string mutexid;
        private IApplicationStatusMediator statusMediator;
        private PluginOptionsAccessor pluginSettings;

        private async Task StartServerIfNotStarted() {
            var allowEveryoneRule = new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow);
            var securitySettings = new MutexSecurity();
            securitySettings.AddAccessRule(allowEveryoneRule);

            // Ensure that only one server will be spawned when multiple application instances are started
            // Ref: https://docs.microsoft.com/en-us/dotnet/api/system.threading.mutex?redirectedfrom=MSDN&view=net-5.0
            using (var mutex = new Mutex(false, mutexid, out var createNew, securitySettings)) {
                var hasHandle = false;
                try {
                    try {
                        // Wait for 5 seconds to receive the mutex
                        hasHandle = mutex.WaitOne(5000, false);
                        if (hasHandle == false) {
                            throw new TimeoutException("Timeout waiting for exclusive access");
                        }

                        try {
                            var pipeName = "NINA.Synchronization.Service.Dither";

                            if (!NamedPipeExist(pipeName)) {
                                var user = WindowsIdentity.GetCurrent().User;
                                var security = new PipeSecurity();
                                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
                                security.SetOwner(user);
                                security.SetGroup(user);

                                pipe = new NamedPipeServer(pipeName, new NamedPipeServerOptions() { PipeSecurity = security });
                                NINA.Synchronization.Service.Dither.DitherService.BindService(pipe.ServiceBinder, DitherServiceServer.Instance);
                                pipe.Start();
                                isServer = true;
                                Logger.Info($"Started synchronization plugin server on pipe {pipeName}");
                            }
                        } catch (Exception ex) {
                            Logger.Error("Failed to start synchronization plugin server ", ex);
                        }
                    } catch (AbandonedMutexException) {
                        hasHandle = true;
                    }
                } finally {
                    if (hasHandle) {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public override async Task Initialize() {
            await StartServerIfNotStarted();

            if (isServer) {
                _ = StartServerHeartbeat();
            }
        }

        private Task StartServerHeartbeat() {
            return Task.Run(async () => {
                var symbols = new string[] { "▖", "▘", "▝", "▗" };
                int roller = 0;
                using (cts = new CancellationTokenSource()) {
                    while (!cts.IsCancellationRequested) {
                        try {
                            await Task.Delay(1000, cts.Token);

                            statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = $"{DitherServiceServer.Instance.Status} {(DitherServiceServer.Instance.Status == "idle" || DitherServiceServer.Instance.Status == "No instance could lead the dither! Make sure at least one instance is connected to a guider!" ? string.Empty : symbols[roller++ % 4])}", Source = "Sync Service" });
                        } catch (OperationCanceledException) {
                            Logger.Info("Stopping server heartbeat");

                            statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = "Synchronization server shutting down", Source = "Sync Service" });
                        } catch (Exception ex) {
                            Logger.Error("An error occurred while pinging the server", ex);
                            statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = "Synchronization server encountered an error", Source = "Sync Service" });
                        }
                    }
                }
                statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = "", Source = "Sync Service" });
            });
        }

        public override async Task Teardown() {
            StopHeartbeat();

            try {
                if (pipe != null) {
                    Logger.Info("Shutting down server");
                    pipe.Kill();
                    pipe.Dispose();
                    pipe = null;
                }
            } catch (Exception ex) {
                Logger.Error("Failed to shutdown pipe", ex);
            }
        }

        private void StopHeartbeat() {
            try {
                cts?.Cancel();
            } catch (Exception) {
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool WaitNamedPipe(string name, int timeout);

        public static bool NamedPipeExist(string pipeName) {
            try {
                int timeout = 0;
                string normalizedPath = System.IO.Path.GetFullPath(
                    string.Format(@"\\.\pipe\{0}", pipeName));
                bool exists = WaitNamedPipe(normalizedPath, timeout);
                if (!exists) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 0) {
                        // pipe does not exist
                        return false;
                    } else if (error == 2) {
                        // win32 error code for file not found
                        return false;
                        // all other errors indicate other issues
                    }
                }
                return true;
            } catch (Exception) {
                // assume it doesn't exist
                return false;
            }
        }
        public int DitherWaitTimeout {
            get => pluginSettings.GetValueInt32(nameof(DitherWaitTimeout), 300);
            set {
                pluginSettings.SetValueInt32(nameof(DitherWaitTimeout), value);
                RaisePropertyChanged();
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /* static class Program {
         [STAThread]
         static void Main() {
             AsyncContext.Run(() => MainAsync());
         }

         static async void MainAsync() {
             new SynchronizationPlugin();

             var client = DitherServiceClient.Instance;
             client.RegisterSync();

             await client.AnnounceToSync();

             await client.WaitForSync();

             var isLeader = await client.IsLeader();

             await client.SetDitherCompleted();

             client.UnregisterSync();
         }
     }*/
}