#nullable enable
using System;
using System.Collections.Generic;

namespace YuzeToolkit.Eval
{
    public sealed class EvalToolFunctionDescriptor
    {
        public static readonly IReadOnlyList<EvalToolFunctionDescriptor>
            Empty = Array.Empty<EvalToolFunctionDescriptor>();

        public EvalToolFunctionDescriptor(string methodName, string description)
            : this(methodName, description, Array.Empty<EvalToolParameterDescriptor>())
        {
        }

        public EvalToolFunctionDescriptor(
            string methodName,
            string description,
            IReadOnlyList<EvalToolParameterDescriptor>? parameters)
            : this(methodName, description, parameters, EvalToolSafety.Unspecified)
        {
        }

        public EvalToolFunctionDescriptor(
            string methodName,
            string description,
            IReadOnlyList<EvalToolParameterDescriptor>? parameters,
            EvalToolSafety safety)
        {
            MethodName = methodName;
            Description = description;
            Parameters = parameters ?? Array.Empty<EvalToolParameterDescriptor>();
            Safety = safety;
        }

        public string MethodName { get; }

        public string Description { get; }

        public IReadOnlyList<EvalToolParameterDescriptor> Parameters { get; }

        public EvalToolSafety Safety { get; }

        public bool RequiresConfirmation => (Safety & EvalToolSafety.RequiresConfirmation) != 0;

        public string RiskLevel => EvalToolSafetyUtility.GetRiskLevel(Safety);
    }

    public sealed class EvalToolParameterDescriptor
    {
        public EvalToolParameterDescriptor(string name, string type, bool optional, object? defaultValue)
            : this(name, type, optional, defaultValue, string.Empty)
        {
        }

        public EvalToolParameterDescriptor(string name, string type, bool optional, object? defaultValue, string description)
        {
            Name = name;
            Type = type;
            Optional = optional;
            DefaultValue = defaultValue;
            Description = description;
        }

        public string Name { get; }

        public string Type { get; }

        public bool Optional { get; }

        public object? DefaultValue { get; }

        public string Description { get; }
    }

    public sealed class EvalToolDescriptor
    {
        public EvalToolDescriptor(
            string name,
            string path,
            string description,
            bool editorOnly,
            bool enabled,
            string source,
            IReadOnlyList<EvalToolFunctionDescriptor> functions,
            IReadOnlyList<EvalToolSummaryDescriptor>? subTools = null)
        {
            Name = name;
            Path = path;
            Description = description;
            EditorOnly = editorOnly;
            Enabled = enabled;
            Source = source;
            Functions = functions;
            SubTools = subTools ?? Array.Empty<EvalToolSummaryDescriptor>();
        }

        public string Name { get; }

        public string Path { get; }

        public string Description { get; }

        public bool EditorOnly { get; }

        public bool Enabled { get; }

        public string Source { get; }

        public IReadOnlyList<EvalToolFunctionDescriptor> Functions { get; }

        public IReadOnlyList<EvalToolSummaryDescriptor> SubTools { get; }
    }

    public sealed class EvalToolSummaryDescriptor
    {
        public EvalToolSummaryDescriptor(
            string name,
            string path,
            string description,
            bool editorOnly,
            bool enabled,
            string source,
            int functionCount)
        {
            Name = name;
            Path = path;
            Description = description;
            EditorOnly = editorOnly;
            Enabled = enabled;
            Source = source;
            FunctionCount = functionCount;
        }

        public string Name { get; }

        public string Path { get; }

        public string Description { get; }

        public bool EditorOnly { get; }

        public bool Enabled { get; }

        public string Source { get; }

        public int FunctionCount { get; }
    }
}
