#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YuzeToolkit
{
    [EvalTool("Pipeline", "Package Manager, Test Runner, and BuildPipeline workflows.")]
    public sealed partial class PipelineTool
    {
        [EvalFunction("List packages.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listPackages()
        {
            var packages = PackageInfo.GetAllRegisteredPackages()
                .Select(package => (object?)PipelineRequestStore.SummarizePackage(package))
                .ToList();
            return EvalData.Obj(("count", packages.Count), ("packages", packages));
        }

        [EvalFunction("Add package.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation | EvalToolSafety.TriggersReload | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> addPackage(string packageId, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Package add requires confirm: true.");
            if (string.IsNullOrWhiteSpace(packageId)) throw new InvalidOperationException("Argument 'packageId' is required.");
            var request = Client.Add(packageId);
            var id = PipelineRequestStore.TrackPackageRequest("add", packageId, request);
            return PipelineRequestStore.GetPackageRequest(id);
        }

        [EvalFunction("Remove package.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.Destructive | EvalToolSafety.RequiresConfirmation | EvalToolSafety.TriggersReload | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> removePackage(string packageName, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Package removal requires confirm: true.");
            if (string.IsNullOrWhiteSpace(packageName)) throw new InvalidOperationException("Argument 'packageName' is required.");
            var request = Client.Remove(packageName);
            var id = PipelineRequestStore.TrackPackageRequest("remove", packageName, request);
            return PipelineRequestStore.GetPackageRequest(id);
        }

        [EvalFunction("Search packages.", Safety = EvalToolSafety.NetworkService | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> searchPackages(string packageName = "")
        {
            var request = Client.Search(packageName);
            var id = PipelineRequestStore.TrackPackageRequest("search", packageName, request);
            return PipelineRequestStore.GetPackageRequest(id);
        }

        [EvalFunction("Read package request status.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getPackageRequest(string id) => PipelineRequestStore.GetPackageRequest(id);

        [EvalFunction("Start tests.", Safety = EvalToolSafety.MutatesEditorState | EvalToolSafety.TriggersReload | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> runTests(string mode = "EditMode", string testName = "")
        {
            object? tests = string.IsNullOrWhiteSpace(testName) ? null : testName;
            return ToLegacyTestRun(new TestsTool().run(mode, tests: tests));
        }

        [EvalFunction("Read test run status.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getTestRun(string id)
        {
            return ToLegacyTestRun(new TestsTool().get(id, "summary", 0, UnityTestToolUtility.DefaultPageSize));
        }

        private static Dictionary<string, object?> ToLegacyTestRun(Dictionary<string, object?> value)
        {
            value["id"] = value.TryGetValue("runId", out var runId) ? runId : string.Empty;
            value["result"] = value.TryGetValue("summary", out var summary) &&
                              summary is IDictionary<string, object?> summaryObject
                ? new Dictionary<string, object?>(summaryObject, StringComparer.Ordinal)
                : summary;
            return value;
        }

        [EvalFunction("Read build settings.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes
                .Select(scene => (object?)EvalData.Obj(("path", scene.path), ("enabled", scene.enabled), ("guid", scene.guid.ToString())))
                .ToList();
            return EvalData.Obj(
                ("activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString()),
                ("selectedBuildTargetGroup", EditorUserBuildSettings.selectedBuildTargetGroup.ToString()),
                ("scenes", scenes));
        }

        [EvalFunction("Build player.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation | EvalToolSafety.TriggersReload | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> buildPlayer(string locationPathName, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Build requires confirm: true.");
            if (string.IsNullOrWhiteSpace(locationPathName)) throw new InvalidOperationException("Argument 'locationPathName' is required.");
            var report = BuildPipeline.BuildPlayer(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(), locationPathName, EditorUserBuildSettings.activeBuildTarget, BuildOptions.None);
            var summary = EvalData.Obj(
                ("result", report.summary.result.ToString()),
                ("totalErrors", report.summary.totalErrors),
                ("totalWarnings", report.summary.totalWarnings),
                ("outputPath", report.summary.outputPath)
            );
            var id = PipelineRequestStore.TrackBuild(summary);
            return PipelineRequestStore.GetBuild(id);
        }

        [EvalFunction("Read build result.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getBuild(string id) => PipelineRequestStore.GetBuild(id);

        private static class PipelineRequestStore
        {
            private static readonly Dictionary<string, TrackedPackageRequest> PackageRequests = new(StringComparer.Ordinal);
            private static readonly Dictionary<string, Dictionary<string, object?>> Builds = new(StringComparer.Ordinal);
            private static readonly object SyncRoot = new();

            public static string TrackPackageRequest(string kind, string label, Request request)
            {
                var id = Guid.NewGuid().ToString("N");
                lock (SyncRoot)
                    PackageRequests[id] = new TrackedPackageRequest(id, kind, label, request, DateTime.UtcNow);
                return id;
            }

            public static Dictionary<string, object?> GetPackageRequest(string id)
            {
                lock (SyncRoot)
                {
                    if (!PackageRequests.TryGetValue(id, out var tracked))
                        return EvalData.Obj(("found", false), ("id", id));
                    return SummarizePackageRequest(tracked);
                }
            }

            public static string TrackBuild(Dictionary<string, object?> summary)
            {
                var id = Guid.NewGuid().ToString("N");
                lock (SyncRoot)
                    Builds[id] = EvalData.Obj(("found", true), ("id", id), ("finishedAtUtc", DateTime.UtcNow.ToString("O")), ("summary", summary));
                return id;
            }

            public static Dictionary<string, object?> GetBuild(string id)
            {
                lock (SyncRoot)
                    return Builds.TryGetValue(id, out var build) ? build : EvalData.Obj(("found", false), ("id", id));
            }

            private static Dictionary<string, object?> SummarizePackageRequest(TrackedPackageRequest tracked)
            {
                var request = tracked.Request;
                object? result = null;
                if (request.IsCompleted)
                {
                    var resultProperty = request.GetType().GetProperty("Result");
                    if (resultProperty != null)
                        result = SummarizePackageResult(resultProperty.GetValue(request));
                }

                return EvalData.Obj(
                    ("found", true),
                    ("id", tracked.Id),
                    ("kind", tracked.Kind),
                    ("label", tracked.Label),
                    ("startedAtUtc", tracked.StartedAtUtc.ToString("O")),
                    ("isCompleted", request.IsCompleted),
                    ("status", request.Status.ToString()),
                    ("error", request.Error != null ? request.Error.message : string.Empty),
                    ("result", result)
                );
            }

            private static object? SummarizePackageResult(object? result)
            {
                if (result == null) return null;
                if (result is PackageInfo package) return SummarizePackage(package);
                if (result is IEnumerable<PackageInfo> packages)
                    return packages.Select(package => (object?)SummarizePackage(package)).ToList();
                return result.ToString();
            }

            public static Dictionary<string, object?> SummarizePackage(PackageInfo package) =>
                EvalData.Obj(
                    ("name", package.name),
                    ("displayName", package.displayName),
                    ("version", package.version),
                    ("source", package.source.ToString()),
                    ("assetPath", package.assetPath)
                );

            private sealed class TrackedPackageRequest
            {
                public TrackedPackageRequest(string id, string kind, string label, Request request, DateTime startedAtUtc)
                {
                    Id = id;
                    Kind = kind;
                    Label = label;
                    Request = request;
                    StartedAtUtc = startedAtUtc;
                }

                public string Id { get; }
                public string Kind { get; }
                public string Label { get; }
                public Request Request { get; }
                public DateTime StartedAtUtc { get; }
            }

        }
    }
}
