#nullable enable
using System;

namespace YuzeToolkit
{
    [Flags]
    public enum EvalToolSafety
    {
        Unspecified = 0,
        ReadOnly = 1 << 0,
        MutatesScene = 1 << 1,
        MutatesProject = 1 << 2,
        Destructive = 1 << 3,
        RequiresConfirmation = 1 << 4,
        TriggersReload = 1 << 5,
        ReflectionDangerous = 1 << 6,
        NetworkService = 1 << 7,
        LongRunning = 1 << 8,
        MutatesEditorState = 1 << 9,
        PersistsData = 1 << 10,
        MutatesRuntimeState = 1 << 11
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class EvalToolAttribute : Attribute
    {
        public EvalToolAttribute(string name, string description)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }

        public string Name { get; }

        public string Description { get; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class EvalFunctionAttribute : Attribute
    {
        public EvalFunctionAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }

        public EvalToolSafety Safety { get; set; } = EvalToolSafety.Unspecified;
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class EvalParameterAttribute : Attribute
    {
        public EvalParameterAttribute(string description)
        {
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }

        public string Description { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class EvalSubToolAttribute : Attribute
    {
        public EvalSubToolAttribute(Type toolType)
        {
            ToolType = toolType ?? throw new ArgumentNullException(nameof(toolType));
        }

        public Type ToolType { get; }
    }
}
