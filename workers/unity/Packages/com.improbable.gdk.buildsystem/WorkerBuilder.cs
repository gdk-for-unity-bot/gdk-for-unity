using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Improbable.Gdk.BuildSystem.Configuration;
using Improbable.Gdk.Core;
using Improbable.Gdk.Tools;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Improbable.Gdk.BuildSystem
{
    public static class WorkerBuilder
    {
        private static readonly string PlayerBuildDirectory =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), EditorPaths.SpatialAssemblyDirectory,
                "worker"));

        private static readonly string SpatialAssemblyDirectory =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), EditorPaths.SpatialAssemblyDirectory));

        private const string BuildWorkerTypes = "buildWorkerTypes";

        /// <summary>
        ///     Build method that is invoked by commandline
        /// </summary>
        // ReSharper disable once UnusedMember.Global
        public static void Build()
        {
            try
            {
                var args = CommandLineArgs.FromCommandLine();
                var buildEnvironmentArg = args.GetCommandLineValue("buildEnvironment", "local");
                var buildTargetsArg = args.GetCommandLineValue("buildTargets", "all");

                BuildEnvironment buildEnvironment;
                switch (buildEnvironmentArg.ToLower())
                {
                    case "cloud":
                        buildEnvironment = BuildEnvironment.Cloud;
                        break;
                    case "local":
                        buildEnvironment = BuildEnvironment.Local;
                        break;
                    default:
                        throw new BuildFailedException("Unknown build environment value: " + buildEnvironmentArg);
                }

                var buildTargets = buildTargetsArg
                    .Split(',')
                    .SkipWhile(target => target == "all")
                    .Select(target =>
                    {
                        switch (buildTargetsArg.ToLower())
                        {
                            case "android":
                                return BuildTarget.Android;
                            case "ios":
                                return BuildTarget.iOS;
                            case "winx86":
                                return BuildTarget.StandaloneWindows;
                            case "win":
                                return BuildTarget.StandaloneWindows64;
                            case "linux":
                                return BuildTarget.StandaloneLinux64;
                            case "macos":
                                return BuildTarget.StandaloneOSX;
                            default:
                                throw new BuildFailedException("Unknown build target value: " + buildTargetsArg);
                        }
                    });

                var workerTypesArg = args.GetCommandLineValue(BuildWorkerTypes, "UnityClient,UnityGameLogic");
                var wantedWorkerTypes = workerTypesArg.Split(',');

                ScriptingImplementation scriptingBackend;
                var wantedScriptingBackend = args.GetCommandLineValue("scriptingBackend", "mono");
                switch (wantedScriptingBackend)
                {
                    case "mono":
                        scriptingBackend = ScriptingImplementation.Mono2x;
                        break;
                    case "il2cpp":
                        scriptingBackend = ScriptingImplementation.IL2CPP;
                        break;
                    default:
                        throw new BuildFailedException("Unknown scripting backend value: " + wantedScriptingBackend);
                }

                var buildsSucceeded = BuildWorkers(wantedWorkerTypes, buildEnvironment, buildTargets, scriptingBackend);

                if (!buildsSucceeded)
                {
                    throw new BuildFailedException("Not all builds were completed successfully. See the log for more information.");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (e is BuildFailedException)
                {
                    throw;
                }

                throw new BuildFailedException(e);
            }
        }

        internal static void MenuBuild(BuildEnvironment environment, params string[] workerTypes)
        {
            // Delaying build by a frame to ensure the editor has re-rendered the UI to avoid odd glitches.
            EditorApplication.delayCall += () =>
            {
                try
                {
                    BuildWorkers(workerTypes, environment);
                }
                catch (Exception)
                {
                    EditorUtility.DisplayDialog("Build Failed",
                        "Build failed. Please see the Unity Console Window for information.",
                        "OK");

                    throw;
                }
            };
        }

        private static bool BuildWorkers(
            string[] workerTypes,
            BuildEnvironment buildEnvironment,
            IEnumerable<BuildTarget> buildTargets = null,
            ScriptingImplementation? scriptingBackend = null)
        {
            var activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            var activeBuildTargetGroup = BuildPipeline.GetBuildTargetGroup(activeBuildTarget);

            if (BuildConfig.GetInstance() == null)
            {
                const string errorMessage =
                    "Could not find an instance of the SpatialOS Build Configuration.\n\nPlease create one via Assets > Create > SpatialOS > SpatialOS Build Configuration.\n\nIf you already have an instance of the SpatialOS Build Configuration in your project, please open it in the Unity Inspector to force the asset to load and retry the build.";

                if (Application.isEditor)
                {
                    EditorApplication.delayCall += () =>
                    {
                        EditorUtility.DisplayDialog("Could not find SpatialOS Build Configuration",
                            errorMessage,
                            "OK");
                    };
                }

                Debug.LogError(errorMessage);
                return false;
            }

            try
            {
                LocalLaunch.BuildConfig();

                var workerResults = new Dictionary<string, bool>();
                foreach (var wantedWorkerType in workerTypes)
                {
                    var result = BuildWorkerForEnvironmentTargets(wantedWorkerType, buildEnvironment, buildTargets, scriptingBackend);
                    workerResults[wantedWorkerType] = result;
                }

                var missingWorkerTypes = string.Join(" ", workerResults.Keys.Where(k => !workerResults[k]));
                var completedWorkerTypes = string.Join(" ", workerResults.Keys.Where(k => workerResults[k]));

                if (missingWorkerTypes.Length > 0)
                {
                    Debug.LogWarning(
                        $"Completed build for {buildEnvironment} target.\n"
                        + $"Completed builds for: {completedWorkerTypes}\n"
                        + $"Skipped builds for: {missingWorkerTypes}. See above for more information.");
                    return false;
                }
                else
                {
                    Debug.Log($"Completed build for {buildEnvironment} target.");
                    return true;
                }
            }
            catch (Exception e)
            {
                throw new BuildFailedException(e);
            }
            finally
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(activeBuildTargetGroup, activeBuildTarget);
            }
        }

        private static bool BuildWorkerForEnvironmentTargets(
            string workerType,
            BuildEnvironment buildEnvironment,
            IEnumerable<BuildTarget> buildTargets,
            ScriptingImplementation? scriptingBackend = null)
        {
            var spatialOSBuildConfiguration = BuildConfig.GetInstance();
            var environmentConfig = spatialOSBuildConfiguration.GetEnvironmentConfigForWorker(workerType, buildEnvironment);

            var enabledTargets = buildTargets == null
                ? environmentConfig?.BuildTargets.Where(t => t.Enabled).ToList()
                : environmentConfig?.BuildTargets.Where(t => buildTargets.Contains(t.Target)).ToList();

            if (enabledTargets == null || enabledTargets.Count == 0)
            {
                Debug.LogWarning($"Skipping build for {workerType}.");
                return false;
            }

            if (!Directory.Exists(PlayerBuildDirectory))
            {
                Directory.CreateDirectory(PlayerBuildDirectory);
            }

            var hasBuildSucceeded = true;

            foreach (var config in enabledTargets)
            {
                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(config.Target);
                var activeScriptingBackend = PlayerSettings.GetScriptingBackend(buildTargetGroup);
                try
                {
                    if (scriptingBackend != null && config.Target != BuildTarget.iOS)
                    {
                        Debug.Log($"Setting scripting backend to {scriptingBackend.Value}");
                        PlayerSettings.SetScriptingBackend(buildTargetGroup, scriptingBackend.Value);
                    }

                    hasBuildSucceeded &= BuildWorkerForTarget(workerType, config.Target, config.Options, buildEnvironment);
                }
                catch (Exception e)
                {
                    throw new BuildFailedException(e);
                }
                finally
                {
                    PlayerSettings.SetScriptingBackend(buildTargetGroup, activeScriptingBackend);
                }
            }

            return hasBuildSucceeded;
        }

        public static void Clean()
        {
            // Delete all but the schema directory where the schema descriptor is placed.
            if (Directory.Exists(SpatialAssemblyDirectory))
            {
                var children = new DirectoryInfo(SpatialAssemblyDirectory).GetDirectories();

                foreach (var child in children)
                {
                    if (child.Name != "schema")
                    {
                        Directory.Delete(child.FullName, true);
                    }
                }
            }

            if (Directory.Exists(Common.BuildScratchDirectory))
            {
                Directory.Delete(Common.BuildScratchDirectory, true);
            }
        }

        private static bool BuildWorkerForTarget(string workerType, BuildTarget buildTarget,
            BuildOptions buildOptions, BuildEnvironment targetEnvironment)
        {
            var spatialOSBuildConfiguration = BuildConfig.GetInstance();

            if (!WorkerBuildData.BuildTargetsThatCanBeBuilt[buildTarget])
            {
                var config = spatialOSBuildConfiguration.GetEnvironmentConfigForWorker(workerType, targetEnvironment);
                var target = config.BuildTargets.First(targetConfig => targetConfig.Target == buildTarget);

                if (target.Required)
                {
                    throw new BuildFailedException(
                        $"Build failed for {workerType}. Cannot build for required {buildTarget} because build support is not installed in the Unity Editor.");
                }

                Debug.LogWarning($"Skipping {buildTarget} because build support is not installed in the Unity Editor and the build target is not marked as 'Required'.");
                return false;
            }

            Debug.Log(
                $"Building \"{buildTarget}\" for worker platform: \"{workerType}\", environment: \"{targetEnvironment}\"");


            var workerBuildData = new WorkerBuildData(workerType, buildTarget);
            var scenes = spatialOSBuildConfiguration.GetScenePathsForWorker(workerType);

            var buildPlayerOptions = new BuildPlayerOptions
            {
                options = buildOptions,
                target = buildTarget,
                scenes = scenes,
                locationPathName = workerBuildData.BuildScratchDirectory
            };

            var result = BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (result.summary.result != BuildResult.Succeeded)
            {
                if (buildTarget == BuildTarget.Android && string.IsNullOrEmpty(EditorPrefs.GetString("AndroidSdkRoot")))
                {
                    Debug.LogWarning($"Unable to build worker {workerType} for platform Android. " +
                        $"Ensure you have the Android SDK set inside the Unity Editor Preferences.");
                    return false;
                }

                throw new BuildFailedException($"Build failed for {workerType}");
            }

            if (buildTarget == BuildTarget.Android || buildTarget == BuildTarget.iOS)
            {
                // Mobile clients can only be run locally, no need to package them
                return true;
            }

            var zipPath = Path.Combine(PlayerBuildDirectory, workerBuildData.PackageName);
            var basePath = Path.Combine(Common.BuildScratchDirectory, workerBuildData.PackageName);
            Zip(zipPath, basePath, targetEnvironment == BuildEnvironment.Cloud);
            return true;
        }

        private static void Zip(string zipAbsolutePath, string basePath, bool useCompression)
        {
            using (new ShowProgressBarScope($"Package {basePath}"))
            {
                RedirectedProcess.Command(Common.SpatialBinary)
                    .InDirectory(Path.GetFullPath(Path.Combine(Application.dataPath, "..")))
                    .WithArgs("file", "zip", $"--output=\"{Path.GetFullPath(zipAbsolutePath)}\"",
                        $"--basePath=\"{Path.GetFullPath(basePath)}\"", "\"**\"",
                        $"--compression={useCompression}")
                    .Run();
            }
        }
    }
}
