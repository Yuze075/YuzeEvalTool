#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace YuzeToolkit.Eval
{
    public interface IEvalTool
    {
        string Name { get; }

        string Description { get; }

        IReadOnlyList<EvalToolFunctionDescriptor> Functions { get; }

        IReadOnlyList<IEvalTool> SubTools { get; }
    }

    /// <summary>
    /// Reusable implementation base for project-owned Eval Tools. Debug UI code does not participate in this type's
    /// registration or lifetime.
    /// </summary>
    [Preserve]
    public abstract class EvalToolBase : IEvalTool
    {
        protected EvalToolBase(
            string name,
            string description,
            IReadOnlyList<EvalToolFunctionDescriptor>? functions = null,
            IReadOnlyList<IEvalTool>? subTools = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Eval Tool name cannot be empty.", nameof(name));
            if (string.IsNullOrWhiteSpace(description))
                throw new ArgumentException("Eval Tool description cannot be empty.", nameof(description));

            Name = name;
            Description = description;
            Functions = functions == null
                ? EvalToolFunctionDescriptor.Empty
                : Copy(functions);
            SubTools = subTools == null
                ? Array.Empty<IEvalTool>()
                : Copy(subTools);
        }

        public string Name { get; }

        public string Description { get; }

        public IReadOnlyList<EvalToolFunctionDescriptor> Functions { get; }

        public IReadOnlyList<IEvalTool> SubTools { get; }

        private static T[] Copy<T>(IReadOnlyList<T> values)
        {
            var result = new T[values.Count];
            for (var index = 0; index < values.Count; index++)
                result[index] = values[index];
            return result;
        }
    }

    /// <summary>A Tool path node that only groups explicitly supplied child Tools.</summary>
    [Preserve]
    public class EvalToolGroup : EvalToolBase
    {
        public EvalToolGroup(string name, string description, IReadOnlyList<IEvalTool> subTools)
            : base(name, description, subTools: subTools ?? throw new ArgumentNullException(nameof(subTools)))
        {
        }
    }

    /// <summary>A leaf Tool that reads a value through get().</summary>
    [Preserve]
    public class EvalReadOnlyValueTool<TValue> : EvalToolBase
    {
        private readonly Func<TValue> _getter;

        public EvalReadOnlyValueTool(string name, string description, Func<TValue> getter)
            : base(name, description, new[]
            {
                new EvalToolFunctionDescriptor(
                    "get",
                    "Return the current value.",
                    null,
                    EvalToolSafety.ReadOnly)
            })
        {
            _getter = getter ?? throw new ArgumentNullException(nameof(getter));
        }

        [Preserve]
        public virtual TValue get() => _getter();
    }

    /// <summary>A leaf Tool that reads through get() and writes through set(value).</summary>
    [Preserve]
    public class EvalWritableValueTool<TValue> : EvalToolBase
    {
        private readonly Func<TValue> _getter;
        private readonly Action<TValue> _setter;

        public EvalWritableValueTool(
            string name,
            string description,
            Func<TValue> getter,
            Action<TValue> setter,
            EvalToolSafety safety = EvalToolSafety.MutatesScene)
            : base(name, description, CreateFunctions(safety))
        {
            _getter = getter ?? throw new ArgumentNullException(nameof(getter));
            _setter = setter ?? throw new ArgumentNullException(nameof(setter));
        }

        [Preserve]
        public virtual TValue get() => _getter();

        [Preserve]
        public virtual TValue set(TValue value)
        {
            _setter(value);
            return _getter();
        }

        private static IReadOnlyList<EvalToolFunctionDescriptor> CreateFunctions(EvalToolSafety safety)
        {
            EvalToolUtility.ValidateMutationSafety(safety, nameof(safety));
            return new[]
            {
                new EvalToolFunctionDescriptor(
                    "get",
                    "Return the current value.",
                    null,
                    EvalToolSafety.ReadOnly),
                new EvalToolFunctionDescriptor(
                    "set",
                    "Set the value and return the updated value.",
                    new[]
                    {
                        new EvalToolParameterDescriptor(
                            "value",
                            EvalToolUtility.GetTypeName(typeof(TValue)),
                            false,
                            null,
                            "New value.")
                    },
                    safety)
            };
        }
    }

    /// <summary>A leaf Tool that executes an action through invoke().</summary>
    [Preserve]
    public class EvalActionTool : EvalToolBase
    {
        private readonly Action _action;

        public EvalActionTool(
            string name,
            string description,
            Action action,
            EvalToolSafety safety = EvalToolSafety.MutatesScene)
            : base(name, description, CreateFunctions(safety))
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
        }

        [Preserve]
        public virtual string invoke()
        {
            _action();
            return "invoked";
        }

        private static IReadOnlyList<EvalToolFunctionDescriptor> CreateFunctions(EvalToolSafety safety)
        {
            EvalToolUtility.ValidateMutationSafety(safety, nameof(safety));
            return new[]
            {
                new EvalToolFunctionDescriptor("invoke", "Invoke this action.", null, safety)
            };
        }
    }

    /// <summary>Shared validation and stable path-segment helpers for manually composed Tools.</summary>
    public static class EvalToolUtility
    {
        public static string ToToolName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Tool";
            var chars = value.Trim().ToCharArray();
            for (var index = 0; index < chars.Length; index++)
            {
                var character = chars[index];
                if (char.IsLetterOrDigit(character) || character is '_' or '-') continue;
                chars[index] = '_';
            }

            var result = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "Tool" : result;
        }

        public static string GetTypeName(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(bool)) return "bool";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(string)) return "string";
            if (type == typeof(Vector2)) return "Vector2";
            if (type == typeof(Vector3)) return "Vector3";
            if (type == typeof(Vector4)) return "Vector4";
            if (type == typeof(Color)) return "Color";
            if (type.IsEnum) return type.Name;
            return type.FullName ?? type.Name;
        }

        internal static void ValidateMutationSafety(EvalToolSafety safety, string parameterName)
        {
            const EvalToolSafety mutationFlags =
                EvalToolSafety.MutatesScene |
                EvalToolSafety.MutatesProject |
                EvalToolSafety.Destructive |
                EvalToolSafety.TriggersReload |
                EvalToolSafety.ReflectionDangerous |
                EvalToolSafety.NetworkService |
                EvalToolSafety.LongRunning |
                EvalToolSafety.MutatesEditorState |
                EvalToolSafety.PersistsData |
                EvalToolSafety.MutatesRuntimeState;
            const EvalToolSafety knownFlags =
                EvalToolSafety.ReadOnly |
                mutationFlags |
                EvalToolSafety.RequiresConfirmation;

            if ((safety & ~knownFlags) != 0 ||
                (safety & EvalToolSafety.ReadOnly) != 0 ||
                (safety & mutationFlags) == 0)
                throw new ArgumentException(
                    "A writable value or action Tool must declare a mutation safety flag.", parameterName);
            if ((safety & EvalToolSafety.Destructive) != 0 &&
                (safety & EvalToolSafety.RequiresConfirmation) == 0)
                throw new ArgumentException(
                    "Destructive Tool actions must also require confirmation.", parameterName);
        }
    }
}
