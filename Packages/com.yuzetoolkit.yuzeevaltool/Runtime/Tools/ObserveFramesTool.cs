#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YuzeToolkit
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("ObserveFrames",
        "Bounded cross-frame sampling of public Component or static C# fields and properties in Editor or Player.")]
    public sealed partial class ObserveFramesTool
    {
        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Start a bounded observation session and capture an initial sample. probes is an array of {name, kind:'component', target, type, member, index?} or {name, kind:'static', type, member}. Optional until is {probe, op, value?}; op supports eq, ne, gt, gte, lt, lte, truthy, and falsy.",
            Safety = EvalToolSafety.ReadOnly | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> start(
            [EvalParameter("Array of public component/static field or readable-property probe objects.")]
            object probes,
            [EvalParameter("Maximum observed Editor updates or Player frames; 1..36000.")]
            int maxFrames = 300,
            [EvalParameter("Capture once per this many observed frames; 1..maxFrames.")]
            int intervalFrames = 1,
            [EvalParameter("Maximum retained samples including the initial sample; 1..10000.")]
            int maxSamples = 1000,
            [EvalParameter("Optional completion condition {probe, op, value?}.")]
            object? until = null,
            [EvalParameter("Optional diagnostic label, truncated to 128 characters.")]
            string label = "")
        {
            return ObserveFramesStore.Start(probes, maxFrames, intervalFrames, maxSamples, until, label);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Get one observation session and a bounded page of samples. offset starts at zero; limit is clamped to 1..500.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> get(
            [EvalParameter("Observation session id returned by start.")] string id,
            [EvalParameter("Zero-based sample offset.")] int offset = 0,
            [EvalParameter("Page size, clamped to 1..500.")] int limit = 100)
        {
            return ObserveFramesStore.Get(id, offset, limit);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("List observation session summaries, newest first.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> list(
            [EvalParameter("Optional running, completed, cancelled, or faulted filter.")] string status = "",
            [EvalParameter("Maximum returned summaries, clamped to 1..64.")] int limit = 20)
        {
            return ObserveFramesStore.List(status, limit);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Cancel a running observation session and keep its captured samples.", Safety = EvalToolSafety.MutatesRuntimeState)]
        public Dictionary<string, object?> cancel(
            [EvalParameter("Observation session id returned by start.")] string id)
        {
            return ObserveFramesStore.Cancel(id);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Release one observation session and its in-memory samples.", Safety = EvalToolSafety.MutatesRuntimeState)]
        public Dictionary<string, object?> release(
            [EvalParameter("Observation session id whose retained samples should be released.")] string id)
        {
            return ObserveFramesStore.Release(id);
        }
    }

    internal static class ObserveFramesStore
    {
        private const int MaxActiveSessions = 8;
        private const int MaxRetainedSessions = 64;
        private const int MaxProbeCount = 32;
        private const int MaxFrameCount = 36000;
        private const int MaxSampleCount = 10000;
        private const int MaxPageSize = 500;
        private const int MaxLabelLength = 128;
        private const int MaxTextLength = 4096;
        private const int MaxFormattedDepth = 4;
        private const int MaxCollectionItems = 128;
        private const int MaxFormattedValueCharacters = 32768;
        private const int MaxSessionStorageCharacters = 8 * 1024 * 1024;

        private static readonly Dictionary<string, ObservationSession> Sessions =
            new(StringComparer.Ordinal);

#if UNITY_EDITOR
        private static bool _editorUpdateRegistered;
        private static bool _wasPlaying;
        private static int _lastUnityFrame = -1;
#else
        private static ObserveFramesHost? _host;
#endif

        public static Dictionary<string, object?> Start(object probesValue, int maxFrames, int intervalFrames,
            int maxSamples, object? untilValue, string label)
        {
            var probeValues = EvalData.AsArray(probesValue);
            if (probeValues == null || probeValues.Count == 0)
                throw new InvalidOperationException("Argument 'probes' must be a non-empty array.");
            if (probeValues.Count > MaxProbeCount)
                throw new InvalidOperationException($"A session supports at most {MaxProbeCount} probes.");

            ValidateRange(maxFrames, 1, MaxFrameCount, "maxFrames");
            ValidateRange(intervalFrames, 1, maxFrames, "intervalFrames");
            ValidateRange(maxSamples, 1, MaxSampleCount, "maxSamples");
            label = Truncate(label?.Trim() ?? string.Empty, MaxLabelLength);

            MakeRoomForSession();
            if (Sessions.Values.Count(session => session.Status == ObservationStatus.Running) >= MaxActiveSessions)
                throw new InvalidOperationException(
                    $"At most {MaxActiveSessions} observation sessions may run at once. Cancel or wait for one to finish.");

            var probes = ParseProbes(probeValues);
            var until = ParseCondition(untilValue, probes);
            var session = new ObservationSession(
                Guid.NewGuid().ToString("N"),
                label,
                probes,
                until,
                maxFrames,
                intervalFrames,
                maxSamples);

            Sessions.Add(session.Id, session);
            Capture(session);
            if (session.Status == ObservationStatus.Running)
                EnsureScheduler();

            return ToResult(session, 0, Math.Min(20, maxSamples));
        }

        public static Dictionary<string, object?> Get(string id, int offset, int limit)
        {
            var session = RequireSession(id);
            return ToResult(session, Math.Max(0, offset), Clamp(limit, 1, MaxPageSize));
        }

        public static Dictionary<string, object?> List(string status, int limit)
        {
            var normalizedStatus = string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();
            if (normalizedStatus.Length > 0 && !ObservationStatus.IsKnown(normalizedStatus))
                throw new InvalidOperationException(
                    "Argument 'status' must be empty, running, completed, cancelled, or faulted.");

            limit = Clamp(limit, 1, MaxRetainedSessions);
            var sessions = Sessions.Values
                .Where(session => normalizedStatus.Length == 0 || session.Status == normalizedStatus)
                .OrderByDescending(session => session.CreatedAtUtc)
                .Take(limit)
                .Select(session => (object?)ToSummary(session))
                .ToList();

            return EvalData.Obj(
                ("count", sessions.Count),
                ("retainedCount", Sessions.Count),
                ("activeCount", Sessions.Values.Count(session => session.Status == ObservationStatus.Running)),
                ("sessions", sessions));
        }

        public static Dictionary<string, object?> Cancel(string id)
        {
            var session = RequireSession(id);
            if (session.Status == ObservationStatus.Running)
            {
                session.Status = ObservationStatus.Cancelled;
                session.CompletionReason = "cancelled";
                session.CompletedAtUtc = DateTime.UtcNow;
            }

            StopSchedulerWhenIdle();
            return ToResult(session, Math.Max(0, session.Samples.Count - 20), 20);
        }

        public static Dictionary<string, object?> Release(string id)
        {
            var session = RequireSession(id);
            Sessions.Remove(session.Id);
            if (session.Status == ObservationStatus.Running)
            {
                session.Status = ObservationStatus.Cancelled;
                session.CompletionReason = "released";
                session.CompletedAtUtc = DateTime.UtcNow;
            }

            StopSchedulerWhenIdle();
            return EvalData.Obj(
                ("released", true),
                ("id", session.Id),
                ("sampleCount", session.Samples.Count));
        }

        internal static void Tick()
        {
#if UNITY_EDITOR
            var isPlaying = Application.isPlaying;
            if (isPlaying)
            {
                var unityFrame = Time.frameCount;
                if (_wasPlaying && unityFrame == _lastUnityFrame)
                    return;
                _lastUnityFrame = unityFrame;
            }
            else
            {
                _lastUnityFrame = -1;
            }

            _wasPlaying = isPlaying;
#endif

            var running = Sessions.Values
                .Where(session => session.Status == ObservationStatus.Running)
                .ToArray();
            foreach (var session in running)
            {
                try
                {
                    Advance(session);
                }
                catch (Exception ex)
                {
                    session.Status = ObservationStatus.Faulted;
                    session.CompletionReason = "internal-error";
                    session.Error = GetSafeExceptionSummary(ex);
                    session.CompletedAtUtc = DateTime.UtcNow;
                }
            }

            StopSchedulerWhenIdle();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForRuntimeStart()
        {
            StopScheduler();
            Sessions.Clear();
        }

        private static List<ObservationProbe> ParseProbes(List<object?> values)
        {
            var probes = new List<ObservationProbe>(values.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Count; i++)
            {
                var obj = EvalData.AsObject(values[i]);
                if (obj == null)
                    throw new InvalidOperationException($"probes[{i}] must be an object.");

                var name = (EvalData.GetString(obj, "name") ?? $"probe{i}").Trim();
                if (name.Length == 0)
                    name = $"probe{i}";
                name = Truncate(name, 128);
                if (!names.Add(name))
                    throw new InvalidOperationException($"Probe name '{name}' is duplicated.");

                var kind = (EvalData.GetString(obj, "kind") ?? "component").Trim().ToLowerInvariant();
                var typeName = (EvalData.GetString(obj, "type") ?? string.Empty).Trim();
                var memberName = (EvalData.GetString(obj, "member") ?? string.Empty).Trim();
                if (typeName.Length == 0 || memberName.Length == 0)
                    throw new InvalidOperationException($"Probe '{name}' requires non-empty 'type' and 'member'.");

                if (kind == "component")
                {
                    if (!obj.TryGetValue("target", out var target) || target == null)
                        throw new InvalidOperationException($"Component probe '{name}' requires 'target'.");
                    var index = Math.Max(0, EvalData.GetInt(obj, "index", 0));
                    var component = ComponentsTool.ResolveComponent(target, typeName, index, out var error);
                    if (component == null)
                        throw new InvalidOperationException($"Probe '{name}': {error}");
                    var member = FindReadableMember(component.GetType(), memberName, false);
                    if (member == null)
                        throw new InvalidOperationException(
                            $"Probe '{name}': public readable instance field or property '{memberName}' was not found on '{component.GetType().FullName}'.");

                    probes.Add(new ObservationProbe(
                        name,
                        kind,
                        typeName,
                        memberName,
                        index,
                        target,
                        member,
                        EvalData.Obj(
                            ("path", ToolUtilities.GetPath(component.gameObject)),
                            ("instanceId", component.gameObject.GetInstanceID()))));
                    continue;
                }

                if (kind == "static")
                {
                    var type = ToolUtilities.FindType(typeName);
                    if (type == null)
                        throw new InvalidOperationException($"Probe '{name}': type '{typeName}' was not found.");
                    var member = FindReadableMember(type, memberName, true);
                    if (member == null)
                        throw new InvalidOperationException(
                            $"Probe '{name}': public readable static field or property '{memberName}' was not found on '{type.FullName}'.");

                    probes.Add(new ObservationProbe(
                        name,
                        kind,
                        type.FullName ?? type.Name,
                        memberName,
                        0,
                        null,
                        member,
                        null));
                    continue;
                }

                throw new InvalidOperationException(
                    $"Probe '{name}' kind must be 'component' or 'static', not '{kind}'.");
            }

            return probes;
        }

        private static ObservationCondition? ParseCondition(object? value, IReadOnlyCollection<ObservationProbe> probes)
        {
            if (value == null)
                return null;
            var obj = EvalData.AsObject(value);
            if (obj == null)
                throw new InvalidOperationException("Argument 'until' must be an object when provided.");

            var probe = (EvalData.GetString(obj, "probe") ?? string.Empty).Trim();
            var op = NormalizeOperator((EvalData.GetString(obj, "op") ?? string.Empty).Trim());
            if (probe.Length == 0 || probes.All(candidate => candidate.Name != probe))
                throw new InvalidOperationException("until.probe must name one of the configured probes.");
            if (op.Length == 0)
                throw new InvalidOperationException(
                    "until.op must be eq, ne, gt, gte, lt, lte, truthy, or falsy.");
            if (op is not ("truthy" or "falsy") && !obj.ContainsKey("value"))
                throw new InvalidOperationException($"until.value is required for operator '{op}'.");

            obj.TryGetValue("value", out var expected);
            return new ObservationCondition(probe, op, expected);
        }

        private static string NormalizeOperator(string op)
        {
            return op.ToLowerInvariant() switch
            {
                "eq" or "==" or "equals" => "eq",
                "ne" or "!=" or "notequals" => "ne",
                "gt" or ">" => "gt",
                "gte" or ">=" => "gte",
                "lt" or "<" => "lt",
                "lte" or "<=" => "lte",
                "truthy" => "truthy",
                "falsy" => "falsy",
                _ => string.Empty
            };
        }

        private static MemberInfo? FindReadableMember(Type type, string memberName, bool isStatic)
        {
            var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                        (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var field = type.GetField(memberName, flags);
            if (field != null && field.IsStatic == isStatic)
                return field;

            var property = type.GetProperty(memberName, flags);
            if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                return null;
            var getter = property.GetGetMethod(false);
            return getter != null && getter.IsStatic == isStatic ? property : null;
        }

        private static void Advance(ObservationSession session)
        {
            session.ElapsedFrames++;
            var reachedFrameLimit = session.ElapsedFrames >= session.MaxFrames;
            if (session.ElapsedFrames % session.IntervalFrames == 0 || reachedFrameLimit)
                Capture(session);

            if (session.Status == ObservationStatus.Running && reachedFrameLimit)
                Complete(session, "frame-limit");
        }

        private static void Capture(ObservationSession session)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var errors = new Dictionary<string, object?>(StringComparer.Ordinal);
            var rawValues = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var probe in session.Probes)
            {
                try
                {
                    var raw = ReadProbe(probe);
                    rawValues[probe.Name] = raw;
                    values[probe.Name] = FormatProbeValue(raw);
                }
                catch (Exception ex)
                {
                    rawValues[probe.Name] = null;
                    values[probe.Name] = null;
                    errors[probe.Name] = GetSafeExceptionSummary(ex);
                }
            }

            var sample = EvalData.Obj(
                ("sequence", session.Samples.Count),
                ("elapsedFrames", session.ElapsedFrames),
                ("unityFrame", Time.frameCount),
                ("realtimeSinceStartup", Time.realtimeSinceStartup),
                ("isPlaying", Application.isPlaying),
                ("values", values),
                ("errors", errors));
            var sampleCharacters = EvalJson.Stringify(sample).Length;
            if (session.StoredCharacters + sampleCharacters > MaxSessionStorageCharacters)
            {
                Complete(session, "storage-limit");
                return;
            }
            session.Samples.Add(sample);
            session.StoredCharacters += sampleCharacters;

            if (session.Until != null && errors.All(pair => pair.Key != session.Until.Probe) &&
                rawValues.TryGetValue(session.Until.Probe, out var actual) &&
                Matches(session.Until, actual))
            {
                Complete(session, "condition-met");
                return;
            }

            if (session.Samples.Count >= session.MaxSamples)
                Complete(session, "sample-limit");
        }

        private static object? ReadProbe(ObservationProbe probe)
        {
            object? target = null;
            MemberInfo member = probe.Member;
            if (probe.Kind == "component")
            {
                var component = ComponentsTool.ResolveComponent(probe.Target!, probe.TypeName, probe.Index,
                    out var error);
                if (component == null)
                    throw new InvalidOperationException(error);
                target = component;
                if (member.DeclaringType == null || !member.DeclaringType.IsInstanceOfType(component))
                {
                    member = FindReadableMember(component.GetType(), probe.MemberName, false) ??
                             throw new InvalidOperationException(
                                 $"Public readable member '{probe.MemberName}' is no longer available on '{component.GetType().FullName}'.");
                }
            }

            return member switch
            {
                FieldInfo field => field.GetValue(target),
                PropertyInfo property => property.GetValue(target),
                _ => throw new InvalidOperationException($"Unsupported member '{probe.MemberName}'.")
            };
        }

        private static object? FormatProbeValue(object? value)
        {
            var budget = new SafeFormatBudget(MaxFormattedValueCharacters, MaxCollectionItems);
            var formatted = FormatKnownValue(value, 0, budget);
            var serialized = EvalJson.Stringify(formatted);
            if (serialized.Length <= MaxFormattedValueCharacters)
                return formatted;
            return EvalData.Obj(
                ("type", value?.GetType().FullName ?? "null"),
                ("truncated", true),
                ("reason", $"Formatted value exceeds {MaxFormattedValueCharacters} characters."));
        }

        private static object? FormatKnownValue(object? value, int depth, SafeFormatBudget budget)
        {
            if (value == null)
            {
                budget.ConsumeCharacters(4);
                return null;
            }

            switch (value)
            {
                case string text:
                    return budget.TakeString(text, MaxTextLength);
                case char character:
                    return budget.TakeString(character.ToString(), 1);
                case bool:
                case byte:
                case sbyte:
                case short:
                case ushort:
                case int:
                case uint:
                case long:
                case ulong:
                case float:
                case double:
                case decimal:
                    budget.ConsumeCharacters(32);
                    return value;
                case Enum enumValue:
                    return budget.TakeString(Enum.Format(enumValue.GetType(), enumValue, "G"), MaxTextLength);
                case DateTime dateTime:
                    return budget.TakeString(dateTime.ToString("O", CultureInfo.InvariantCulture), 64);
                case DateTimeOffset dateTimeOffset:
                    return budget.TakeString(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture), 64);
                case TimeSpan timeSpan:
                    return budget.TakeString(timeSpan.ToString("c", CultureInfo.InvariantCulture), 64);
                case Guid guid:
                    return budget.TakeString(guid.ToString("D"), 36);
                case Vector2 vector2:
                    return Components("Vector2", budget, ("x", vector2.x), ("y", vector2.y));
                case Vector3 vector3:
                    return Components("Vector3", budget, ("x", vector3.x), ("y", vector3.y), ("z", vector3.z));
                case Vector4 vector4:
                    return Components("Vector4", budget, ("x", vector4.x), ("y", vector4.y),
                        ("z", vector4.z), ("w", vector4.w));
                case Vector2Int vector2Int:
                    return Components("Vector2Int", budget, ("x", vector2Int.x), ("y", vector2Int.y));
                case Vector3Int vector3Int:
                    return Components("Vector3Int", budget, ("x", vector3Int.x), ("y", vector3Int.y),
                        ("z", vector3Int.z));
                case Quaternion quaternion:
                    return Components("Quaternion", budget, ("x", quaternion.x), ("y", quaternion.y),
                        ("z", quaternion.z), ("w", quaternion.w));
                case Color color:
                    return Components("Color", budget, ("r", color.r), ("g", color.g),
                        ("b", color.b), ("a", color.a));
                case Color32 color32:
                    return Components("Color32", budget, ("r", color32.r), ("g", color32.g),
                        ("b", color32.b), ("a", color32.a));
                case Rect rect:
                    return Components("Rect", budget, ("x", rect.x), ("y", rect.y),
                        ("width", rect.width), ("height", rect.height));
                case RectInt rectInt:
                    return Components("RectInt", budget, ("x", rectInt.x), ("y", rectInt.y),
                        ("width", rectInt.width), ("height", rectInt.height));
                case Bounds bounds:
                    return EvalData.Obj(
                        ("type", "Bounds"),
                        ("center", FormatKnownValue(bounds.center, depth + 1, budget)),
                        ("size", FormatKnownValue(bounds.size, depth + 1, budget)));
                case BoundsInt boundsInt:
                    return EvalData.Obj(
                        ("type", "BoundsInt"),
                        ("position", FormatKnownValue(boundsInt.position, depth + 1, budget)),
                        ("size", FormatKnownValue(boundsInt.size, depth + 1, budget)));
                case LayerMask layerMask:
                    budget.ConsumeCharacters(16);
                    return EvalData.Obj(("type", "LayerMask"), ("value", layerMask.value));
                case UnityEngine.Object unityObject:
                    return FormatUnityObject(unityObject, budget);
            }

            if (depth >= MaxFormattedDepth)
                return TypeSummary(value, budget, "depthLimit");

            var type = value.GetType();
            if (value is Array array)
                return FormatArray(array, depth, budget);
            if (IsExactGenericType(type, typeof(List<>)) && value is IList list)
                return FormatList(list, type, depth, budget);
            if (IsExactGenericType(type, typeof(Dictionary<,>)) && value is IDictionary dictionary)
                return FormatDictionary(dictionary, type, depth, budget);

            return TypeSummary(value, budget, "unsupportedCustomType");
        }

        private static Dictionary<string, object?> Components(
            string type,
            SafeFormatBudget budget,
            params (string Name, object Value)[] values)
        {
            budget.ConsumeCharacters(type.Length + values.Length * 24);
            var result = EvalData.Obj(("type", type));
            foreach (var pair in values) result[pair.Name] = pair.Value;
            return result;
        }

        private static Dictionary<string, object?> FormatUnityObject(
            UnityEngine.Object unityObject,
            SafeFormatBudget budget)
        {
            if (unityObject == null)
                return EvalData.Obj(("type", "UnityEngine.Object"), ("status", "destroyed"));
            var typeName = unityObject.GetType().FullName ?? unityObject.GetType().Name;
            return EvalData.Obj(
                ("type", budget.TakeString(typeName, 512)),
                ("name", budget.TakeString(unityObject.name ?? string.Empty, MaxTextLength)),
                ("instanceId", unityObject.GetInstanceID()),
                ("status", "resolved"));
        }

        private static Dictionary<string, object?> FormatArray(
            Array array,
            int depth,
            SafeFormatBudget budget)
        {
            var typeName = array.GetType().FullName ?? array.GetType().Name;
            if (array.Rank != 1)
                return TypeSummary(array, budget, "multiDimensionalArrayNotExpanded", array.Length);
            var items = new List<object?>();
            var index = 0;
            while (index < array.Length && budget.TryTakeEntry())
            {
                items.Add(FormatKnownValue(array.GetValue(index), depth + 1, budget));
                index++;
            }
            return EvalData.Obj(
                ("type", budget.TakeString(typeName, 512)),
                ("count", array.Length),
                ("items", items),
                ("truncated", index < array.Length));
        }

        private static Dictionary<string, object?> FormatList(
            IList list,
            Type type,
            int depth,
            SafeFormatBudget budget)
        {
            var items = new List<object?>();
            var index = 0;
            while (index < list.Count && budget.TryTakeEntry())
            {
                items.Add(FormatKnownValue(list[index], depth + 1, budget));
                index++;
            }
            return EvalData.Obj(
                ("type", budget.TakeString(type.FullName ?? type.Name, 512)),
                ("count", list.Count),
                ("items", items),
                ("truncated", index < list.Count));
        }

        private static Dictionary<string, object?> FormatDictionary(
            IDictionary dictionary,
            Type type,
            int depth,
            SafeFormatBudget budget)
        {
            var entries = new List<object?>();
            var enumerator = dictionary.GetEnumerator();
            while (budget.TryTakeEntry() && enumerator.MoveNext())
            {
                entries.Add(EvalData.Obj(
                    ("key", FormatKnownValue(enumerator.Key, depth + 1, budget)),
                    ("value", FormatKnownValue(enumerator.Value, depth + 1, budget))));
            }
            return EvalData.Obj(
                ("type", budget.TakeString(type.FullName ?? type.Name, 512)),
                ("count", dictionary.Count),
                ("entries", entries),
                ("truncated", entries.Count < dictionary.Count));
        }

        private static Dictionary<string, object?> TypeSummary(
            object value,
            SafeFormatBudget budget,
            string reason,
            int? count = null)
        {
            var type = value.GetType();
            return EvalData.Obj(
                ("type", budget.TakeString(type.FullName ?? type.Name, 512)),
                ("count", count),
                ("truncated", true),
                ("reason", reason));
        }

        private static bool IsExactGenericType(Type type, Type genericTypeDefinition) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == genericTypeDefinition;

        private static bool Matches(ObservationCondition condition, object? actual)
        {
            switch (condition.Operator)
            {
                case "truthy":
                    return IsTruthy(actual);
                case "falsy":
                    return !IsTruthy(actual);
                case "eq":
                    return ValuesEqual(actual, condition.Expected);
                case "ne":
                    return !ValuesEqual(actual, condition.Expected);
            }

            if (!TryToDouble(actual, out var actualNumber) ||
                !TryToDouble(condition.Expected, out var expectedNumber))
                return false;
            return condition.Operator switch
            {
                "gt" => actualNumber > expectedNumber,
                "gte" => actualNumber >= expectedNumber,
                "lt" => actualNumber < expectedNumber,
                "lte" => actualNumber <= expectedNumber,
                _ => false
            };
        }

        private static bool ValuesEqual(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;
            if (TryToDouble(left, out var leftNumber) && TryToDouble(right, out var rightNumber))
                return Math.Abs(leftNumber - rightNumber) <= 0.0000001d;
            if (left is Enum leftEnum)
            {
                if (right is string enumName)
                    return string.Equals(
                        Enum.Format(leftEnum.GetType(), leftEnum, "G"),
                        enumName,
                        StringComparison.OrdinalIgnoreCase);
                return right is Enum rightEnum && leftEnum.GetType() == rightEnum.GetType() &&
                       leftEnum.Equals(rightEnum);
            }
            if (left is string leftText && right is string rightText)
                return string.Equals(leftText, rightText, StringComparison.Ordinal);
            if (left is bool leftBoolean && right is bool rightBoolean)
                return leftBoolean == rightBoolean;
            if (left is char leftCharacter && right is char rightCharacter)
                return leftCharacter == rightCharacter;
            if (left is Guid leftGuid && right is string rightGuidText &&
                Guid.TryParse(rightGuidText, out var rightGuid))
                return leftGuid == rightGuid;
            if (left is DateTime leftDateTime && right is string rightDateTimeText &&
                DateTime.TryParse(rightDateTimeText, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var rightDateTime))
                return leftDateTime == rightDateTime;
            if (left is UnityEngine.Object leftObject)
            {
                if (right is UnityEngine.Object rightObject)
                    return leftObject == rightObject;
                if (right is int rightInstanceId)
                    return leftObject != null && leftObject.GetInstanceID() == rightInstanceId;
                if (right is long rightLongInstanceId && rightLongInstanceId is >= int.MinValue and <= int.MaxValue)
                    return leftObject != null && leftObject.GetInstanceID() == (int)rightLongInstanceId;
            }
            return false;
        }

        private static bool TryToDouble(object? value, out double number)
        {
            switch (value)
            {
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return true;
                case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed):
                    number = parsed;
                    return true;
                default:
                    number = 0d;
                    return false;
            }
        }

        private static bool IsTruthy(object? value)
        {
            if (value == null)
                return false;
            if (value is bool boolean)
                return boolean;
            if (TryToDouble(value, out var number))
                return Math.Abs(number) > double.Epsilon;
            if (value is string text)
                return text.Length > 0;
            return true;
        }

        private static Dictionary<string, object?> ToResult(ObservationSession session, int offset, int limit)
        {
            offset = Math.Min(offset, session.Samples.Count);
            var samples = session.Samples
                .Skip(offset)
                .Take(limit)
                .Cast<object?>()
                .ToList();
            var nextOffset = offset + samples.Count;
            return EvalData.Obj(
                ("session", ToSummary(session)),
                ("offset", offset),
                ("returnedCount", samples.Count),
                ("nextOffset", nextOffset),
                ("hasMore", nextOffset < session.Samples.Count),
                ("samples", samples));
        }

        private static Dictionary<string, object?> ToSummary(ObservationSession session)
        {
            var probes = session.Probes.Select(probe => (object?)EvalData.Obj(
                ("name", probe.Name),
                ("kind", probe.Kind),
                ("type", probe.TypeName),
                ("member", probe.MemberName),
                ("index", probe.Kind == "component" ? probe.Index : null),
                ("target", probe.TargetSummary))).ToList();
            var until = session.Until == null
                ? null
                : EvalData.Obj(
                    ("probe", session.Until.Probe),
                    ("op", session.Until.Operator),
                    ("value", FormatProbeValue(session.Until.Expected)));

            return EvalData.Obj(
                ("id", session.Id),
                ("label", session.Label),
                ("status", session.Status),
                ("completionReason", session.CompletionReason),
                ("error", session.Error),
                ("createdAtUtc", session.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("completedAtUtc", session.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture)),
                ("elapsedFrames", session.ElapsedFrames),
                ("maxFrames", session.MaxFrames),
                ("intervalFrames", session.IntervalFrames),
                ("maxSamples", session.MaxSamples),
                ("sampleCount", session.Samples.Count),
                ("storedCharacters", session.StoredCharacters),
                ("storageCharacterLimit", MaxSessionStorageCharacters),
                ("probes", probes),
                ("until", until));
        }

        private static ObservationSession RequireSession(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !Sessions.TryGetValue(id.Trim(), out var session))
                throw new InvalidOperationException($"Observation session '{id}' was not found.");
            return session;
        }

        private static void Complete(ObservationSession session, string reason)
        {
            if (session.Status != ObservationStatus.Running)
                return;
            session.Status = ObservationStatus.Completed;
            session.CompletionReason = reason;
            session.CompletedAtUtc = DateTime.UtcNow;
        }

        private static void MakeRoomForSession()
        {
            while (Sessions.Count >= MaxRetainedSessions)
            {
                var oldestTerminal = Sessions.Values
                    .Where(session => session.Status != ObservationStatus.Running)
                    .OrderBy(session => session.CreatedAtUtc)
                    .FirstOrDefault();
                if (oldestTerminal == null)
                    throw new InvalidOperationException(
                        $"All {MaxRetainedSessions} retained observation sessions are still running.");
                Sessions.Remove(oldestTerminal.Id);
            }
        }

        private static void EnsureScheduler()
        {
#if UNITY_EDITOR
            if (_editorUpdateRegistered)
                return;
            _wasPlaying = Application.isPlaying;
            _lastUnityFrame = _wasPlaying ? Time.frameCount : -1;
            EditorApplication.update += Tick;
            _editorUpdateRegistered = true;
#else
            if (_host != null)
                return;
            var go = new GameObject("Yuze Eval Tool ObserveFrames")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<ObserveFramesHost>();
#endif
        }

        private static void StopSchedulerWhenIdle()
        {
            if (Sessions.Values.Any(session => session.Status == ObservationStatus.Running))
                return;
            StopScheduler();
        }

        private static void StopScheduler()
        {
#if UNITY_EDITOR
            if (!_editorUpdateRegistered)
                return;
            EditorApplication.update -= Tick;
            _editorUpdateRegistered = false;
            _lastUnityFrame = -1;
#else
            if (_host == null)
                return;
            var go = _host.gameObject;
            _host = null;
            if (go != null)
                UnityEngine.Object.Destroy(go);
#endif
        }

        private static string Truncate(string value, int maxLength)
        {
            if (value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "…";
        }

        private static string GetSafeExceptionSummary(Exception exception)
        {
            var actual = exception is TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException!
                : exception;
            var type = actual.GetType();
            var typeName = type.FullName ?? type.Name;
            if (type.Assembly == typeof(Exception).Assembly || type == typeof(UnityException))
                return Truncate(typeName + ": " + actual.Message, MaxTextLength);
            return Truncate(typeName + ": message omitted for custom exception type", MaxTextLength);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static void ValidateRange(int value, int minimum, int maximum, string argumentName)
        {
            if (value < minimum || value > maximum)
                throw new InvalidOperationException(
                    $"Argument '{argumentName}' must be between {minimum} and {maximum}.");
        }

        private sealed class SafeFormatBudget
        {
            private int _remainingCharacters;
            private int _remainingEntries;

            public SafeFormatBudget(int remainingCharacters, int remainingEntries)
            {
                _remainingCharacters = Math.Max(0, remainingCharacters);
                _remainingEntries = Math.Max(0, remainingEntries);
            }

            public void ConsumeCharacters(int count)
            {
                _remainingCharacters = Math.Max(0, _remainingCharacters - Math.Max(0, count));
            }

            public string TakeString(string value, int perStringLimit)
            {
                var maximum = Math.Min(Math.Max(0, perStringLimit), _remainingCharacters);
                if (maximum == 0) return string.Empty;
                if (value.Length <= maximum)
                {
                    _remainingCharacters -= value.Length;
                    return value;
                }

                var suffix = maximum > 1 ? "…" : string.Empty;
                var prefixLength = Math.Max(0, maximum - suffix.Length);
                _remainingCharacters -= maximum;
                return value.Substring(0, prefixLength) + suffix;
            }

            public bool TryTakeEntry()
            {
                if (_remainingEntries <= 0 || _remainingCharacters <= 0)
                    return false;
                _remainingEntries--;
                ConsumeCharacters(16);
                return true;
            }
        }

        private sealed class ObservationSession
        {
            public ObservationSession(string id, string label, List<ObservationProbe> probes,
                ObservationCondition? until, int maxFrames, int intervalFrames, int maxSamples)
            {
                Id = id;
                Label = label;
                Probes = probes;
                Until = until;
                MaxFrames = maxFrames;
                IntervalFrames = intervalFrames;
                MaxSamples = maxSamples;
            }

            public string Id { get; }
            public string Label { get; }
            public List<ObservationProbe> Probes { get; }
            public ObservationCondition? Until { get; }
            public int MaxFrames { get; }
            public int IntervalFrames { get; }
            public int MaxSamples { get; }
            public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
            public DateTime? CompletedAtUtc { get; set; }
            public int ElapsedFrames { get; set; }
            public string Status { get; set; } = ObservationStatus.Running;
            public string CompletionReason { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public int StoredCharacters { get; set; }
            public List<Dictionary<string, object?>> Samples { get; } = new();
        }

        private sealed class ObservationProbe
        {
            public ObservationProbe(string name, string kind, string typeName, string memberName, int index,
                object? target, MemberInfo member, object? targetSummary)
            {
                Name = name;
                Kind = kind;
                TypeName = typeName;
                MemberName = memberName;
                Index = index;
                Target = target;
                Member = member;
                TargetSummary = targetSummary;
            }

            public string Name { get; }
            public string Kind { get; }
            public string TypeName { get; }
            public string MemberName { get; }
            public int Index { get; }
            public object? Target { get; }
            public MemberInfo Member { get; }
            public object? TargetSummary { get; }
        }

        private sealed class ObservationCondition
        {
            public ObservationCondition(string probe, string op, object? expected)
            {
                Probe = probe;
                Operator = op;
                Expected = expected;
            }

            public string Probe { get; }
            public string Operator { get; }
            public object? Expected { get; }
        }

        private static class ObservationStatus
        {
            public const string Running = "running";
            public const string Completed = "completed";
            public const string Cancelled = "cancelled";
            public const string Faulted = "faulted";

            public static bool IsKnown(string value) =>
                value is Running or Completed or Cancelled or Faulted;
        }
    }

    [UnityEngine.Scripting.Preserve]
    internal sealed class ObserveFramesHost : MonoBehaviour
    {
        private void Update()
        {
            ObserveFramesStore.Tick();
        }
    }
}
