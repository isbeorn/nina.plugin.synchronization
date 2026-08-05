using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NINA.Plugins.Test {
    [TestFixture]
    [NonParallelizable]
    public class NinaPluginProjectTests {
        [Test]
        public void GitHubActionBuild_DisablesLocalDeployment() {
            var repoRoot = FindRepoRoot();
            var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "github-action.yaml");

            var workflow = File.ReadAllText(workflowPath);

            Assert.That(workflow, Does.Contain("-p:DeployToNinaLocal=false"));
        }

        [Test]
        public void GitHubActionBuild_UsesNet10OutputPaths() {
            var repoRoot = FindRepoRoot();
            var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "github-action.yaml");

            var workflow = File.ReadAllText(workflowPath);

            Assert.That(workflow, Does.Contain("net10.0-windows"));
            Assert.That(workflow, Does.Not.Contain("net8.0-windows"));
        }

        [Test]
        public void DeployLocalPlugin_WhenDeployFlagIsNotProvided_DeploysToConfiguredPluginDirectory() {
            var repoRoot = FindRepoRoot();
            var tempRoot = Path.Combine(repoRoot, "NINA.Plugins.Test", "TestOutput", Guid.NewGuid().ToString("N"));
            var fakeBuildOutput = Path.Combine(tempRoot, "build");
            var pluginInstall = Path.Combine(tempRoot, "plugin");
            var fakePluginDll = Path.Combine(fakeBuildOutput, "Synchronization.dll");

            Directory.CreateDirectory(fakeBuildOutput);
            WriteDeploymentFiles(fakeBuildOutput);

            try {
                var projectPath = Path.Combine(repoRoot, "Synchronization", "NINA.Plugins.Synchronization.csproj");
                var result = RunDotnet(
                    repoRoot,
                    "msbuild",
                    projectPath,
                    "/t:DeployLocalPlugin",
                    $"/p:NinaLocalPluginDir={pluginInstall}",
                    $"/p:TargetPath={fakePluginDll}",
                    $"/p:TargetDir={fakeBuildOutput}{Path.DirectorySeparatorChar}",
                    "/v:minimal");

                Assert.That(result.ExitCode, Is.Zero, result.Output);
                Assert.That(File.Exists(Path.Combine(pluginInstall, "Synchronization.dll")), Is.True, result.Output);
                Assert.That(File.Exists(Path.Combine(pluginInstall, "NINA.Plugins.Synchronization.Service.dll")), Is.True, result.Output);
            } finally {
                if (Directory.Exists(tempRoot)) {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void DeployLocalPlugin_WhenDeploymentFileIsMissing_DoesNotDeleteExistingInstall() {
            var repoRoot = FindRepoRoot();
            var tempRoot = Path.Combine(repoRoot, "NINA.Plugins.Test", "TestOutput", Guid.NewGuid().ToString("N"));
            var fakeBuildOutput = Path.Combine(tempRoot, "build");
            var existingInstall = Path.Combine(tempRoot, "existing");
            var sentinelFile = Path.Combine(existingInstall, "sentinel.txt");
            var fakePluginDll = Path.Combine(fakeBuildOutput, "Synchronization.dll");

            Directory.CreateDirectory(fakeBuildOutput);
            Directory.CreateDirectory(existingInstall);
            File.WriteAllText(fakePluginDll, string.Empty);
            File.WriteAllText(sentinelFile, "existing install should survive failed deploy");

            try {
                var projectPath = Path.Combine(repoRoot, "Synchronization", "NINA.Plugins.Synchronization.csproj");
                var result = RunDotnet(
                    repoRoot,
                    "msbuild",
                    projectPath,
                    "/t:DeployLocalPlugin",
                    "/p:DeployToNinaLocal=true",
                    $"/p:NinaLocalPluginDir={existingInstall}",
                    $"/p:TargetPath={fakePluginDll}",
                    $"/p:TargetDir={fakeBuildOutput}{Path.DirectorySeparatorChar}",
                    "/v:minimal");

                Assert.That(result.ExitCode, Is.Not.Zero, result.Output);
                Assert.That(File.Exists(sentinelFile), Is.True, result.Output);
            } finally {
                if (Directory.Exists(tempRoot)) {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        private static string FindRepoRoot() {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null) {
                if (File.Exists(Path.Combine(directory.FullName, "Synchronization.sln"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root from the test directory.");
        }

        private static void WriteDeploymentFiles(string directory) {
            File.WriteAllText(Path.Combine(directory, "Synchronization.dll"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "NINA.Plugins.Synchronization.Service.dll"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "Grpc.Core.Api.dll"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "GrpcDotNetNamedPipes.dll"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "Google.Protobuf.dll"), string.Empty);
        }

        private static ProcessResult RunDotnet(string workingDirectory, params string[] arguments) {
            var startInfo = new ProcessStartInfo {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments) {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            var output = new StringBuilder();
            output.Append(process.StandardOutput.ReadToEnd());
            output.Append(process.StandardError.ReadToEnd());
            process.WaitForExit();

            return new ProcessResult(process.ExitCode, output.ToString());
        }

        private sealed record ProcessResult(int ExitCode, string Output);
    }
}
