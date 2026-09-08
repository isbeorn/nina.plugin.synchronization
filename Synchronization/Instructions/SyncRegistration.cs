using Synchronization.Service;

namespace Synchronization.Instructions {
    internal sealed class SyncRegistration {
        private readonly ISyncServiceClient client;
        private readonly string source;
        private readonly object lockObj = new object();
        private bool initialized;
        private bool registered;
        private int executions;

        public SyncRegistration(ISyncServiceClient client, string source) {
            this.client = client;
            this.source = source;
        }

        public void Initialize() {
            lock (lockObj) {
                initialized = true;
                UpdateRegistration();
            }
        }

        public void Teardown() {
            lock (lockObj) {
                initialized = false;
                UpdateRegistration();
            }
        }

        public void BeginExecution() {
            lock (lockObj) {
                executions++;
                try {
                    UpdateRegistration();
                } catch {
                    executions--;
                    throw;
                }
            }
        }

        public void EndExecution() {
            lock (lockObj) {
                executions--;
                UpdateRegistration();
            }
        }

        private void UpdateRegistration() {
            // Clones own no registration until initialized. A running instruction keeps its
            // registration even if a container detaches it before Execute has finished.
            var required = initialized || executions > 0;
            if (required && !registered) {
                client.RegisterSync(source);
                registered = true;
            } else if (!required && registered) {
                client.UnregisterSync(source);
                registered = false;
            }
        }
    }
}
