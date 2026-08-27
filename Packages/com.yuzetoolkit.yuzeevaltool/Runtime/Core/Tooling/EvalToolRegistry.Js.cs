#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Puerts;

namespace YuzeToolkit
{
    public static partial class EvalToolRegistry
    {
        private static readonly object JsMetadataSyncRoot = new();
        private static readonly Dictionary<string, EvalToolDescriptor> JsDescriptorCache = new(StringComparer.Ordinal);
        private static ScriptEnv? JsMetadataEnv;
        private const int JsMetadataTimeoutMilliseconds = 100;
        private const int JsMetadataMaxTicks = 16;

        public static bool TryRegisterJsTool(string modulePath)
        {
            if (TryRegisterJsTool(modulePath, out var error)) return true;
            LogSys.LogError(error);
            return false;
        }

        public static bool TryRegisterJsTool(string modulePath, out string error)
        {
            if (!TryReadJsMetadata(modulePath, out var name, out var description))
            {
                error = $"JS eval tool module '{modulePath}' could not be imported or does not export non-empty name and description metadata.";
                return false;
            }

            return TryRegisterJsTool(modulePath, name, description, out error);
        }

        public static bool TryRegisterJsTool(string modulePath, string name, string description)
        {
            if (TryRegisterJsTool(modulePath, name, description, out var error)) return true;
            LogSys.LogError(error);
            return false;
        }

        public static bool TryRegisterJsTool(string modulePath, string name, string description, out string error)
        {
            if (string.IsNullOrWhiteSpace(modulePath) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(description))
            {
                error = "JS eval tool registration requires non-empty modulePath, name, and description values.";
                return false;
            }

            var loader = EvalScriptLoader.Loader ?? new Puerts.DefaultLoader();
            if (!loader.FileExists(modulePath))
            {
                error = $"JS eval tool module '{modulePath}' does not exist in the configured script loader.";
                return false;
            }

            try
            {
                ValidateToolSegment(name);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }

            lock (SyncRoot)
            {
                if (CSharpRoots.ContainsKey(name) || JsRoots.ContainsKey(name))
                {
                    error = $"Eval root tool '{name}' is already registered.";
                    return false;
                }
            }

            IReadOnlyList<EvalToolDescriptor> descriptors;
            try
            {
                if (!TryReadJsDescriptor(modulePath, name, name, out var rootDescriptor))
                {
                    error = $"JS eval tool module '{modulePath}' could not produce a valid descriptor for root '{name}'.";
                    return false;
                }

                descriptors = ReadAndValidateJsDescriptorTree(modulePath, name, rootDescriptor);
                if (!string.Equals(rootDescriptor.Description, description, StringComparison.Ordinal))
                {
                    error = $"JS eval tool '{name}' description does not match the registered description.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"JS eval tool module '{modulePath}' failed descriptor validation: {ex.Message}";
                return false;
            }

            lock (SyncRoot)
            {
                if (CSharpRoots.ContainsKey(name) || JsRoots.ContainsKey(name))
                {
                    error = $"Eval root tool '{name}' was registered concurrently.";
                    return false;
                }
                JsRoots.Add(name, new JsToolRegistration(modulePath, name, description));
                EnsureKnownNoLock(name);
            }

            ClearGeneratedModuleCache();
            foreach (var descriptor in descriptors)
                CacheJsDescriptor(descriptor);
            Changed?.Invoke();
            error = string.Empty;
            return true;
        }

        public static string GetJsToolAuthoringPrompt()
        {
            return @"Create a Yuze Eval Tool loader-backed JavaScript Tool module.

Lifecycle rules:
- C# tools are compiled into Unity assemblies and are registered as IEvalTool instances.
- JavaScript tools are registered by module path during initialization. They are not added from strings or folders at runtime.
- `import(""tools://"")` reads cached root tool metadata. A concrete `tools://<path>` import loads the JS module only when needed.

Module requirements:
- Export const name as a non-empty path segment.
- Export const description as a non-empty string.
- Export const functions as an array of function descriptors. `parameters` is the only parameter metadata source.
- Every descriptor includes name or methodName, description, parameters, and a `safety` array.
- Valid safety flags are ReadOnly, MutatesScene, MutatesProject, Destructive, RequiresConfirmation, TriggersReload, ReflectionDangerous, NetworkService, LongRunning, MutatesEditorState, PersistsData, and MutatesRuntimeState. PersistsData means the function writes durable user or application data outside Unity project assets and is high risk. MutatesRuntimeState means the function changes transient process or Tool-owned state without changing scene, project, or durable user data.
- Every descriptor methodName must be a non-reserved JavaScript identifier and match an exported function.
- Optionally export const subTools as an array of direct child summaries. Omit subTools entirely when there are no direct children.
- Each sub tool summary includes name, path, and description.
- A parent that declares subTools must export `getSubTool(nameOrPath)`. `subTools` items are metadata summaries, never callable child instances. Concrete imports such as `tools://Debug/Button` use getSubTool to resolve the child instance and fail explicitly when the resolver is missing.

Example:
```javascript
export const name = 'Debug';
export const description = 'Small JavaScript tool example.';
export const functions = [
  {
    name: 'echo',
    methodName: 'echo',
    description: 'Return the provided value.',
    safety: ['ReadOnly'],
    parameters: [
      { name: 'value', type: 'string', optional: false, defaultValue: null, description: 'Value to echo.' }
    ]
  }
];

export function echo(value) {
  return { value };
}
```";
        }

        private static bool TryGetJsModuleSource(string path, out string source)
        {
            source = string.Empty;
            JsToolRegistration registration;
            string rootName;
            lock (SyncRoot)
            {
                rootName = GetRootName(path);
                if (!JsRoots.TryGetValue(rootName, out var tool)) return false;
                registration = tool;
            }

            var normalizedPath = NormalizePath(path);
            if (!TryGetJsDescriptor(normalizedPath, out var descriptor))
                return false;

            source = BuildJsToolModuleSource(registration.ModulePath, rootName, descriptor);
            return true;
        }

        private static bool TryGetJsDescriptor(string path, out EvalToolDescriptor descriptor)
        {
            descriptor = null!;
            if (string.IsNullOrWhiteSpace(path)) return false;

            JsToolRegistration tool;
            lock (SyncRoot)
            {
                var rootName = GetRootName(path);
                if (!JsRoots.TryGetValue(rootName, out tool)) return false;
            }

            var normalizedPath = NormalizePath(path);
            lock (JsMetadataSyncRoot)
            {
                if (JsDescriptorCache.TryGetValue(normalizedPath, out descriptor))
                    return true;
            }

            try
            {
                if (!TryReadJsDescriptor(tool.ModulePath, tool.Name, normalizedPath, out descriptor))
                    return false;
                ValidateJsDescriptor(descriptor, normalizedPath);
                CacheJsDescriptor(descriptor);
                return true;
            }
            catch
            {
                descriptor = null!;
                return false;
            }
        }

        private static bool TryReadJsMetadata(string modulePath, out string name, out string description)
        {
            name = string.Empty;
            description = string.Empty;
            if (string.IsNullOrWhiteSpace(modulePath)) return false;

            lock (JsMetadataSyncRoot)
            {
                if (!TryRunJsMetadataRequest(BuildJsMetadataRunner(modulePath), "jsTool.metadata", out var payload))
                    return false;
                var data = EvalData.AsObject(EvalJson.Parse(payload));
                if (data == null || !EvalData.GetBool(data, "success")) return false;
                name = EvalData.GetString(data, "name") ?? string.Empty;
                description = EvalData.GetString(data, "description") ?? string.Empty;
                return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description);
            }
        }

        private static bool TryReadJsDescriptor(string modulePath, string rootName, string path, out EvalToolDescriptor descriptor)
        {
            descriptor = null!;
            lock (JsMetadataSyncRoot)
            {
                if (!TryRunJsMetadataRequest(BuildJsDescriptorRunner(modulePath, rootName, path), "jsTool.descriptor",
                        out var payload))
                    return false;
                var data = EvalData.AsObject(EvalJson.Parse(payload));
                if (data == null || !EvalData.GetBool(data, "success")) return false;
                var tool = EvalData.AsObject(data.TryGetValue("tool", out var toolValue) ? toolValue : null);
                if (tool == null) return false;
                descriptor = JsDescriptorFromJson(tool, path);
                return true;
            }
        }

        private static bool TryRunJsMetadataRequest(string source, string evalName, out string payload)
        {
            payload = string.Empty;
            var resultPayload = string.Empty;
            var env = EnsureJsMetadataEnv();
            var completed = false;

            try
            {
                var runner = env.Eval<Action<Action<string>>>(source, evalName);
                runner(result =>
                {
                    resultPayload = result;
                    completed = true;
                });

                var stopwatch = Stopwatch.StartNew();
                var tickCount = 0;
                while (!completed &&
                       tickCount < JsMetadataMaxTicks &&
                       stopwatch.ElapsedMilliseconds < JsMetadataTimeoutMilliseconds)
                {
                    TickJsMetadataEnv(env);
                    tickCount++;
                }
            }
            catch
            {
                ResetJsMetadataEnv();
                return false;
            }

            payload = resultPayload;
            return completed;
        }

        private static void TickJsMetadataEnv(ScriptEnv env)
        {
            if (MainThreadDispatcher.IsMainThread)
            {
                env.Tick();
                return;
            }

            MainThreadDispatcher.RunAsync(env.Tick).GetAwaiter().GetResult();
            Thread.Sleep(1);
        }

        private static bool JsToolPathMayExist(string path)
        {
            var normalized = NormalizePath(path);
            var rootName = GetRootName(normalized);
            lock (SyncRoot)
                return JsRoots.ContainsKey(rootName);
        }

        private static void RefreshToolMetadataCaches()
        {
            ClearGeneratedModuleCache();
            ResetJsMetadataEnv();
        }

        private static void CacheJsDescriptor(EvalToolDescriptor descriptor)
        {
            lock (JsMetadataSyncRoot)
                JsDescriptorCache[descriptor.Path] = descriptor;
        }

        private static IReadOnlyList<EvalToolDescriptor> ReadAndValidateJsDescriptorTree(
            string modulePath, string rootName, EvalToolDescriptor rootDescriptor)
        {
            const int maxDescriptorCount = 1024;
            var result = new List<EvalToolDescriptor>();
            var queue = new Queue<EvalToolDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ValidateJsDescriptor(rootDescriptor, rootName);
            queue.Enqueue(rootDescriptor);
            while (queue.Count > 0)
            {
                var descriptor = queue.Dequeue();
                ValidateJsDescriptor(descriptor, descriptor.Path);
                if (!seen.Add(descriptor.Path))
                    throw new InvalidOperationException(
                        $"JS tool tree contains a duplicate or cyclic path '{descriptor.Path}'.");
                result.Add(descriptor);
                if (result.Count > maxDescriptorCount)
                    throw new InvalidOperationException(
                        $"JS tool '{rootName}' exceeds the {maxDescriptorCount} descriptor registration limit.");

                foreach (var summary in descriptor.SubTools)
                {
                    if (summary.Path.Split('/').Length > 32)
                        throw new InvalidOperationException(
                            $"JS sub tool descriptor '{summary.Path}' exceeds the maximum supported depth of 32.");
                    if (seen.Contains(summary.Path))
                        throw new InvalidOperationException(
                            $"JS tool tree contains a duplicate or cyclic path '{summary.Path}'.");
                    if (!TryReadJsDescriptor(modulePath, rootName, summary.Path, out var child))
                        throw new InvalidOperationException(
                            $"JS sub tool descriptor '{summary.Path}' could not be resolved through getSubTool(nameOrPath).");
                    ValidateJsDescriptor(child, summary.Path);
                    if (!string.Equals(child.Description, summary.Description, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"JS sub tool '{summary.Path}' description does not match its parent summary.");
                    queue.Enqueue(child);
                }
            }

            return result;
        }

        private static void ClearJsDescriptorCache()
        {
            lock (JsMetadataSyncRoot)
                JsDescriptorCache.Clear();
        }

        private static ScriptEnv EnsureJsMetadataEnv()
        {
            JsMetadataEnv ??= PuerTsBackendFactory.Create(new EvalScriptLoader());
            return JsMetadataEnv;
        }

        private static void ResetJsMetadataEnv()
        {
            try
            {
                JsMetadataEnv?.Dispose();
            }
            catch
            {
                // Metadata imports are best-effort; registration failure will surface as false.
            }
            finally
            {
                JsMetadataEnv = null;
            }
        }

        private static string BuildJsMetadataRunner(string modulePath)
        {
            return @"(function(onFinish) {
  import('" + EscapeJavaScriptString(modulePath) + @"')
    .then(function(module) {
      onFinish.Invoke(JSON.stringify({
        success: true,
        name: String(module.name || ''),
        description: String(module.description || '')
      }));
    })
    .catch(function(err) {
      onFinish.Invoke(JSON.stringify({
        success: false,
        error: String((err && err.message) || err)
      }));
    });
})";
        }

        private static string BuildJsDescriptorRunner(string modulePath, string rootName, string path)
        {
            return @"(function(onFinish) {
  function toArray(value) {
    return Array.isArray(value) ? value : [];
  }
  function functionDescriptor(fn) {
    const methodName = String((fn && (fn.methodName || fn.name)) || '');
    const safety = toArray(fn && fn.safety).map(function(flag) { return String(flag); });
    return {
      name: methodName,
      methodName,
      description: String((fn && fn.description) || ''),
      safety,
      parameters: toArray(fn && fn.parameters).map(function(parameter) {
        return {
          name: String((parameter && parameter.name) || ''),
          type: String((parameter && parameter.type) || 'object'),
          optional: Boolean(parameter && parameter.optional),
          defaultValue: parameter ? parameter.defaultValue : null,
          description: String((parameter && parameter.description) || '')
        };
      })
    };
  }
  function summary(child, parentPath) {
    const name = String((child && child.name) || '');
    const path = String((child && child.path) || (parentPath ? parentPath + '/' + name : name));
    return {
      name,
      path,
      description: String((child && child.description) || ''),
      functionCount: toArray(child && child.functions).length
    };
  }
  function descriptor(tool, fallbackPath) {
    if (!tool || typeof tool !== 'object') throw new Error('JS tool descriptor must be an object: ' + fallbackPath);
    if (!Array.isArray(tool.functions)) throw new Error('JS tool ' + fallbackPath + ' must export a functions array.');
    const name = String((tool && tool.name) || fallbackPath.split('/').pop() || '');
    const path = String((tool && tool.path) || fallbackPath);
    if (!name.trim()) throw new Error('JS tool ' + fallbackPath + ' must define a non-empty name.');
    if (!String(tool.description || '').trim()) throw new Error('JS tool ' + fallbackPath + ' must define a non-empty description.');
    const methodNames = new Set();
    tool.functions.forEach(function(fn) {
      if (!fn || typeof fn !== 'object') throw new Error('JS tool ' + path + ' contains an invalid function descriptor.');
      const methodName = String(fn.methodName || fn.name || '');
      if (!methodName.trim()) throw new Error('JS tool ' + path + ' contains an empty function name.');
      if (methodNames.has(methodName)) throw new Error('JS tool ' + path + ' contains duplicate function ' + methodName + '.');
      methodNames.add(methodName);
      if (!String(fn.description || '').trim()) throw new Error('JS tool ' + path + ' function ' + methodName + ' must define a description.');
      if (!Array.isArray(fn.parameters)) throw new Error('JS tool ' + path + ' function ' + methodName + ' must define a parameters array.');
      if (!Array.isArray(fn.safety)) throw new Error('JS tool ' + path + ' function ' + methodName + ' must define a safety array.');
      if (typeof tool[methodName] !== 'function') throw new Error('JS tool ' + path + ' does not export function ' + methodName + '.');
      const parameterNames = new Set();
      fn.parameters.forEach(function(parameter) {
        if (!parameter || typeof parameter !== 'object') throw new Error('JS tool ' + path + ' function ' + methodName + ' contains an invalid parameter descriptor.');
        const parameterName = String(parameter.name || '');
        if (!parameterName.trim() || !String(parameter.type || '').trim()) throw new Error('JS tool ' + path + ' function ' + methodName + ' has a parameter without name or type.');
        if (parameterNames.has(parameterName)) throw new Error('JS tool ' + path + ' function ' + methodName + ' contains duplicate parameter ' + parameterName + '.');
        parameterNames.add(parameterName);
      });
    });
    if (tool.subTools !== undefined && !Array.isArray(tool.subTools)) throw new Error('JS tool ' + path + ' subTools must be an array when present.');
    const childNames = new Set();
    toArray(tool.subTools).forEach(function(child) {
      if (!child || typeof child !== 'object') throw new Error('JS tool ' + path + ' contains an invalid sub tool summary.');
      const childName = String(child.name || '');
      const childPath = String(child.path || '');
      if (!childName.trim() || !childPath.trim() || !String(child.description || '').trim()) throw new Error('JS tool ' + path + ' sub tool summaries require name, path, and description.');
      if (childNames.has(childName)) throw new Error('JS tool ' + path + ' contains duplicate sub tool ' + childName + '.');
      childNames.add(childName);
      if (childPath !== path + '/' + childName) throw new Error('JS sub tool path must be the direct child path ' + path + '/' + childName + '.');
    });
    if (toArray(tool.subTools).length > 0 && typeof tool.getSubTool !== 'function') throw new Error('JS tool ' + path + ' declares subTools but does not export getSubTool(nameOrPath).');
    return {
      name,
      path,
      description: String((tool && tool.description) || ''),
      functions: toArray(tool && tool.functions).map(functionDescriptor),
      subTools: toArray(tool && tool.subTools).map(function(child) { return summary(child, path); })
    };
  }
  async function resolve(root) {
    const fullPath = '" + EscapeJavaScriptString(path) + @"';
    const segments = fullPath.split('/').filter(Boolean);
    const rootName = '" + EscapeJavaScriptString(rootName) + @"';
    if (segments.length === 0 || segments[0] !== rootName) throw new Error('JS tool root mismatch: ' + fullPath);
    let current = root;
    let currentPath = String(root.path || root.name || rootName);
    for (let i = 1; i < segments.length; i++) {
      const segment = segments[i];
      const children = toArray(current && current.subTools);
      const expectedPath = currentPath + '/' + segment;
      const child = children.find(function(candidate) {
        return String(candidate && candidate.name) === segment ||
          String(candidate && candidate.path) === expectedPath ||
          String(candidate && candidate.path) === segments.slice(0, i + 1).join('/');
      });
      if (!child) throw new Error('JS sub tool not found: ' + expectedPath);
      if (!current || typeof current.getSubTool !== 'function') {
        throw new Error('JS tool ' + currentPath + ' declares subTools summaries but does not export getSubTool(nameOrPath).');
      }
      current = await current.getSubTool(segment);
      if (!current || typeof current !== 'object') throw new Error('JS getSubTool returned no callable tool for: ' + expectedPath);
      currentPath = String((current && current.path) || child.path || expectedPath);
    }
    return descriptor(current, fullPath);
  }
  import('" + EscapeJavaScriptString(modulePath) + @"')
    .then(resolve)
    .then(function(tool) {
      onFinish.Invoke(JSON.stringify({ success: true, tool }));
    })
    .catch(function(err) {
      onFinish.Invoke(JSON.stringify({ success: false, error: String((err && err.message) || err) }));
    });
})";
        }

        internal static EvalToolDescriptor JsDescriptorFromJson(Dictionary<string, object?> data, string fallbackPath)
        {
            var path = NormalizePath(EvalData.GetString(data, "path") ?? fallbackPath);
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("JS tool descriptor path cannot be empty.");
            var name = EvalData.GetString(data, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"JS tool '{path}' must define a non-empty name.");
            ValidateToolSegment(name);
            var description = EvalData.GetString(data, "description") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(description))
                throw new InvalidOperationException($"JS tool '{path}' must define a non-empty description.");

            var functions = new List<EvalToolFunctionDescriptor>();
            if (!data.TryGetValue("functions", out var functionsValue) ||
                EvalData.AsArray(functionsValue) is not { } functionList)
                throw new InvalidOperationException($"JS tool '{path}' must define a functions array.");
            var methodNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var functionValue in functionList)
            {
                var function = EvalData.AsObject(functionValue) ??
                               throw new InvalidOperationException(
                                   $"JS tool '{path}' contains an invalid function descriptor.");
                var methodName = EvalData.GetString(function, "methodName") ??
                                 EvalData.GetString(function, "name") ??
                                 string.Empty;
                if (string.IsNullOrWhiteSpace(methodName) || !IsValidJavaScriptIdentifier(methodName))
                    throw new InvalidOperationException(
                        $"JS tool '{path}' has invalid function name '{methodName}'.");
                if (!methodNames.Add(methodName))
                    throw new InvalidOperationException(
                        $"JS tool '{path}' has duplicate function name '{methodName}'.");
                var functionDescription = EvalData.GetString(function, "description") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(functionDescription))
                    throw new InvalidOperationException(
                        $"JS tool '{path}' function '{methodName}' must define a non-empty description.");

                var parameters = new List<EvalToolParameterDescriptor>();
                if (!function.TryGetValue("parameters", out var parametersValue) ||
                    EvalData.AsArray(parametersValue) is not { } parameterList)
                    throw new InvalidOperationException(
                        $"JS tool '{path}' function '{methodName}' must define a parameters array.");
                var parameterNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameterValue in parameterList)
                {
                    var parameter = EvalData.AsObject(parameterValue) ??
                                    throw new InvalidOperationException(
                                        $"JS tool '{path}' function '{methodName}' contains an invalid parameter descriptor.");
                    var parameterName = EvalData.GetString(parameter, "name") ?? string.Empty;
                    var parameterType = EvalData.GetString(parameter, "type") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(parameterName) || string.IsNullOrWhiteSpace(parameterType))
                        throw new InvalidOperationException(
                            $"JS tool '{path}' function '{methodName}' has a parameter without a name or type.");
                    if (!parameterNames.Add(parameterName))
                        throw new InvalidOperationException(
                            $"JS tool '{path}' function '{methodName}' has duplicate parameter '{parameterName}'.");
                    parameters.Add(new EvalToolParameterDescriptor(
                        parameterName,
                        parameterType,
                        EvalData.GetBool(parameter, "optional"),
                        parameter.TryGetValue("defaultValue", out var defaultValue) ? defaultValue : null,
                        EvalData.GetString(parameter, "description") ?? string.Empty));
                }

                functions.Add(new EvalToolFunctionDescriptor(
                    methodName,
                    functionDescription,
                    parameters,
                    ParseJsSafety(function, path, methodName)));
            }

            var subTools = new List<EvalToolSummaryDescriptor>();
            if (data.TryGetValue("subTools", out var subToolsValue))
            {
                var subToolList = EvalData.AsArray(subToolsValue) ??
                                  throw new InvalidOperationException(
                                      $"JS tool '{path}' subTools must be an array when present.");
                var childNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var subToolValue in subToolList)
                {
                    var subTool = EvalData.AsObject(subToolValue) ??
                                  throw new InvalidOperationException(
                                      $"JS tool '{path}' contains an invalid sub tool summary.");
                    var childName = EvalData.GetString(subTool, "name") ?? string.Empty;
                    var childPath = NormalizePath(EvalData.GetString(subTool, "path") ?? string.Empty);
                    var childDescription = EvalData.GetString(subTool, "description") ?? string.Empty;
                    ValidateToolSegment(childName);
                    if (!childNames.Add(childName))
                        throw new InvalidOperationException(
                            $"JS tool '{path}' contains duplicate sub tool '{childName}'.");
                    var expectedChildPath = path + "/" + childName;
                    if (!string.Equals(childPath, expectedChildPath, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"JS sub tool '{childName}' path must be '{expectedChildPath}'.");
                    if (string.IsNullOrWhiteSpace(childDescription))
                        throw new InvalidOperationException(
                            $"JS sub tool '{childPath}' must define a non-empty description.");
                    subTools.Add(new EvalToolSummaryDescriptor(
                        childName,
                        childPath,
                        childDescription,
                        false,
                        IsEnabled(childPath),
                        "js",
                        EvalData.GetInt(subTool, "functionCount")));
                }
            }

            return new EvalToolDescriptor(
                name,
                path,
                description,
                false,
                IsEnabled(path),
                "js",
                functions.Select(function => EvalToolSafetyUtility.Apply(path, function)).ToList(),
                subTools);
        }

        private static EvalToolSafety ParseJsSafety(Dictionary<string, object?> function, string toolPath,
            string methodName)
        {
            if (!function.TryGetValue("safety", out var safetyValue) || safetyValue == null)
                throw new InvalidOperationException(
                    $"JS tool '{toolPath}' function '{methodName}' must define a safety array.");
            var flags = EvalData.AsArray(safetyValue) ??
                        throw new InvalidOperationException(
                            $"JS tool '{toolPath}' function '{methodName}' safety must be an array of flag names.");
            var safety = EvalToolSafety.Unspecified;
            foreach (var rawFlag in flags)
            {
                var flagName = Convert.ToString(rawFlag);
                if (string.IsNullOrWhiteSpace(flagName) ||
                    !Enum.GetNames(typeof(EvalToolSafety)).Any(name =>
                        string.Equals(name, flagName, StringComparison.OrdinalIgnoreCase)) ||
                    !Enum.TryParse(flagName, ignoreCase: true, out EvalToolSafety flag) ||
                    flag == EvalToolSafety.Unspecified || !IsSingleSafetyFlag(flag))
                    throw new InvalidOperationException(
                        $"JS tool '{toolPath}' function '{methodName}' has unknown safety flag '{flagName}'.");
                safety |= flag;
            }

            EvalToolSafetyUtility.ValidateDeclared(safety,
                $"JS tool '{toolPath}' function '{methodName}'");
            return safety;
        }

        private static void ValidateJsDescriptor(EvalToolDescriptor descriptor, string expectedPath)
        {
            var normalizedExpectedPath = NormalizePath(expectedPath);
            if (!string.Equals(descriptor.Path, normalizedExpectedPath, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"JS tool descriptor path '{descriptor.Path}' does not match requested path '{normalizedExpectedPath}'.");
            var expectedName = normalizedExpectedPath.Split('/').Last();
            if (!string.Equals(descriptor.Name, expectedName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"JS tool descriptor name '{descriptor.Name}' does not match path segment '{expectedName}'.");
        }

        private static bool IsSingleSafetyFlag(EvalToolSafety flag)
        {
            var numeric = (int)flag;
            return numeric > 0 && (numeric & (numeric - 1)) == 0;
        }

        private static string BuildJsToolModuleSource(string modulePath, string rootName, EvalToolDescriptor descriptor)
        {
            var functionsJson = EvalJson.Stringify(descriptor.Functions.Select(function => EvalData.Obj(
                ("name", function.MethodName),
                ("methodName", function.MethodName),
                ("description", function.Description),
                ("safety", EvalToolSafetyUtility.ToJson(function.Safety)),
                ("riskLevel", function.RiskLevel),
                ("requiresConfirmation", function.RequiresConfirmation),
                ("parameters", function.Parameters.Select(parameter => (object?)EvalData.Obj(
                    ("name", parameter.Name),
                    ("type", parameter.Type),
                    ("optional", parameter.Optional),
                    ("defaultValue", parameter.DefaultValue),
                    ("description", parameter.Description)
                )).ToList())
            )).Cast<object?>().ToList());
            var subToolsJson = EvalJson.Stringify(descriptor.SubTools.Select(subTool => EvalData.Obj(
                ("name", subTool.Name),
                ("path", subTool.Path),
                ("description", subTool.Description),
                ("functionCount", subTool.FunctionCount)
            )).Cast<object?>().ToList());

            var builder = new StringBuilder();
            builder.AppendLine($"import * as __root from '{EscapeJavaScriptString(modulePath)}';");
            builder.AppendLine($"const __path = '{EscapeJavaScriptString(descriptor.Path)}';");
            builder.AppendLine($"const __rootName = '{EscapeJavaScriptString(rootName)}';");
            builder.AppendLine(@"
function __toArray(value) {
  return Array.isArray(value) ? value : [];
}

	function __ensureEnabled() {
	  if (!CS.YuzeToolkit.EvalToolRegistry.IsEnabled(__path)) {
	    throw new Error('Eval JS tool ' + __path + ' is disabled.');
	  }
	}

async function __resolveTool() {
  __ensureEnabled();
  const segments = __path.split('/').filter(Boolean);
  let current = __root;
  let currentPath = String(__root.path || __root.name || __rootName);
  for (let i = 1; i < segments.length; i++) {
    const segment = segments[i];
    const expectedPath = currentPath + '/' + segment;
    const child = __toArray(current && current.subTools).find(candidate =>
      String(candidate && candidate.name) === segment ||
      String(candidate && candidate.path) === expectedPath ||
      String(candidate && candidate.path) === segments.slice(0, i + 1).join('/'));
    if (!child) throw new Error('JS sub tool not found: ' + expectedPath);
    if (!current || typeof current.getSubTool !== 'function') {
      throw new Error('JS tool ' + currentPath + ' declares subTools summaries but does not export getSubTool(nameOrPath).');
    }
    current = await current.getSubTool(segment);
    if (!current || typeof current !== 'object') throw new Error('JS getSubTool returned no callable tool for: ' + expectedPath);
    currentPath = String((current && current.path) || child.path || expectedPath);
  }
  return current;
}
");
            builder.AppendLine($"export const name = '{EscapeJavaScriptString(descriptor.Name)}';");
            builder.AppendLine($"export const path = '{EscapeJavaScriptString(descriptor.Path)}';");
            builder.AppendLine($"export const description = '{EscapeJavaScriptString(descriptor.Description)}';");
            builder.AppendLine($"export const functions = {functionsJson};");
            if (descriptor.SubTools.Count > 0)
                builder.AppendLine($"export const subTools = {subToolsJson};");
            builder.AppendLine("export function isEnabled() {");
            builder.AppendLine("  return CS.YuzeToolkit.EvalToolRegistry.IsEnabled(__path);");
            builder.AppendLine("}");
            builder.AppendLine("export async function getSubTool(nameOrPath) {");
            builder.AppendLine("  const tool = await __resolveTool();");
            builder.AppendLine("  if (!tool || typeof tool.getSubTool !== 'function') throw new Error('JS tool has no sub tool resolver: ' + path);");
            builder.AppendLine("  return await tool.getSubTool(nameOrPath);");
            builder.AppendLine("}");
            builder.AppendLine("export async function invoke(methodName, ...args) {");
            builder.AppendLine("  const tool = await __resolveTool();");
            builder.AppendLine("  const fn = tool && tool[String(methodName)];");
            builder.AppendLine("  if (typeof fn !== 'function') throw new Error('JS tool function not found: ' + String(methodName));");
            builder.AppendLine("  return await fn.apply(tool, args);");
            builder.AppendLine("}");
            foreach (var function in descriptor.Functions)
            {
                if (!IsValidJavaScriptIdentifier(function.MethodName)) continue;
                builder.AppendLine($"export async function {function.MethodName}(...args) {{");
                builder.AppendLine($"  return await invoke('{EscapeJavaScriptString(function.MethodName)}', ...args);");
                builder.AppendLine("}");
            }

            return builder.ToString();
        }
    }
}
