#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using YuzeToolkit.Eval.SourceGenerator;
using Xunit;

namespace YuzeToolkit.Eval.SourceGenerator.Tests
{
    public sealed class EvalToolMetadataGeneratorTests
    {
        [Fact]
        public void GeneratesMetadataFromEvalToolAttributes()
        {
            var result = RunGenerator(@"
using System.Collections.Generic;
using YuzeToolkit.Eval;

namespace Demo
{
    [EvalTool(""runtime"", ""Runtime tool."")]
    public sealed partial class RuntimeTool
    {
        [EvalFunction(""Return state."", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getState([EvalParameter(""Maximum result count."")] int limit = 5, string mode = ""all"", bool includeInactive = true)
        {
            return new Dictionary<string, object?>();
        }
    }
}");

            Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            var source = Assert.Single(result.GeneratedSources);
            Assert.Contains("partial class RuntimeTool : global::YuzeToolkit.Eval.IEvalTool", source);
            Assert.Contains("public string Name => @\"runtime\";", source);
            Assert.Contains("@\"getState\"", source);
            Assert.Contains("@\"limit\"", source);
            Assert.Contains("@\"int\"", source);
            Assert.Contains("5,", source);
            Assert.Contains("@\"Maximum result count.\"", source);
            Assert.Contains("@\"mode\"", source);
            Assert.Contains("@\"all\"", source);
            Assert.Contains("@\"includeInactive\"", source);
            Assert.Contains("true,", source);
            Assert.Contains("(global::YuzeToolkit.Eval.EvalToolSafety)1", source);
        }

        [Fact]
        public void ReportsDiagnosticForNonPartialTool()
        {
            var result = RunGenerator(@"
using YuzeToolkit.Eval;

[EvalTool(""bad"", ""Bad tool."")]
public sealed class BadTool
{
    [EvalFunction(""Do work."")]
    public void run() {}
}");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "UET001");
            Assert.Empty(result.GeneratedSources);
        }

        [Fact]
        public void ReportsDiagnosticForDuplicateExportedFunctionNames()
        {
            var result = RunGenerator(@"
using YuzeToolkit.Eval;

[EvalTool(""bad"", ""Bad tool."")]
public sealed partial class BadTool
{
    [EvalFunction(""Do work."")]
    public void run() {}

    [EvalFunction(""Do other work."")]
    public void run(int value) {}
}");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "UET005");
            Assert.Empty(result.GeneratedSources);
        }

        [Fact]
        public void ReportsDiagnosticForReservedJavaScriptFunctionName()
        {
            var result = RunGenerator(@"
using YuzeToolkit.Eval;

[EvalTool(""bad"", ""Bad tool."")]
public sealed partial class BadTool
{
    [EvalFunction(""Cannot be emitted as an ES module declaration."")]
    public void @delete() {}
}");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "UET006");
            Assert.Empty(result.GeneratedSources);
        }

        [Fact]
        public void ReportsDiagnosticForNestedTool()
        {
            var result = RunGenerator(@"
using YuzeToolkit.Eval;

public partial class Container<T>
{
    [EvalTool(""nested"", ""Nested tool."")]
    public sealed partial class NestedTool
    {
        [EvalFunction(""Do work."")]
        public void run() {}
    }
}");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "UET008");
            Assert.Empty(result.GeneratedSources);
        }

        [Theory]
        [InlineData("Task")]
        [InlineData("Task<int>")]
        [InlineData("ValueTask")]
        [InlineData("ValueTask<int>")]
        public void ReportsDiagnosticForTaskReturningFunction(string returnType)
        {
            var result = RunGenerator(@"
using System.Threading.Tasks;
using YuzeToolkit.Eval;

[EvalTool(""async"", ""Async tool."")]
public sealed partial class AsyncTool
{
    [EvalFunction(""Do work."")]
    public " + returnType + @" run() => default;
}");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "UET009");
            Assert.Empty(result.GeneratedSources);
        }

        [Fact]
        public void ReportsDiagnosticForAsyncVoidFunction()
        {
            var result = RunGenerator(@"
using YuzeToolkit.Eval;

[EvalTool(""async"", ""Async tool."")]
public sealed partial class AsyncTool
{
    [EvalFunction(""Do work."")]
    public async void run() { await System.Threading.Tasks.Task.Yield(); }
}");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "UET009");
            Assert.Empty(result.GeneratedSources);
        }

        [Fact]
        public void GeneratesMetadataForBuiltInUnityEvalToolSources()
        {
            var packageRoot = FindPackageRoot();
            var files = Directory.GetFiles(Path.Combine(packageRoot, "com.yuzetoolkit.yuzeevaltool"), "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                    path.EndsWith("Tool.cs", StringComparison.Ordinal) ||
                    path.EndsWith("Tools.cs", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Core{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToArray();

            var result = RunGenerator(files.Select(File.ReadAllText).ToArray());

            Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            Assert.Equal(20, result.GeneratedSources.Length);
            Assert.Contains(result.GeneratedSources, source => source.Contains("partial class RuntimeTool : global::YuzeToolkit.Eval.IEvalTool"));
            Assert.Contains(result.GeneratedSources, source => source.Contains("partial class AssetsTool : global::YuzeToolkit.Eval.IEvalTool"));
            Assert.Contains(result.GeneratedSources, source => source.Contains("partial class ObserveFramesTool : global::YuzeToolkit.Eval.IEvalTool"));
            Assert.Contains(result.GeneratedSources, source => source.Contains("partial class TestsTool : global::YuzeToolkit.Eval.IEvalTool"));
            Assert.Contains(result.GeneratedSources, source => source.Contains("partial class CodeUsagesTool : global::YuzeToolkit.Eval.IEvalTool"));
            Assert.Contains(result.GeneratedSources, source => source.Contains("partial class ToolManagerTool : global::YuzeToolkit.Eval.IEvalTool"));
        }

        private static TestGeneratorResult RunGenerator(string source)
        {
            return RunGenerator(new[] { source });
        }

        private static TestGeneratorResult RunGenerator(string[] sources)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp8);
            var syntaxTrees = sources
                .Select(source => CSharpSyntaxTree.ParseText(source, parseOptions))
                .ToArray();
            var stubTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;

namespace YuzeToolkit.Eval
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class EvalToolAttribute : Attribute
    {
        public EvalToolAttribute(string name, string description) {}
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class EvalFunctionAttribute : Attribute
    {
        public EvalFunctionAttribute(string description) {}
        public EvalToolSafety Safety { get; set; }
    }

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
        PersistsData = 1 << 10
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class EvalParameterAttribute : Attribute
    {
        public EvalParameterAttribute(string description) {}
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class EvalSubToolAttribute : Attribute
    {
        public EvalSubToolAttribute(Type toolType) {}
    }

    public interface IEvalTool {}

    public sealed class EvalToolFunctionDescriptor
    {
        public EvalToolFunctionDescriptor(string methodName, string description, IReadOnlyList<EvalToolParameterDescriptor> parameters) {}
        public EvalToolFunctionDescriptor(string methodName, string description, IReadOnlyList<EvalToolParameterDescriptor> parameters, EvalToolSafety safety) {}
    }

    public sealed class EvalToolParameterDescriptor
    {
        public EvalToolParameterDescriptor(string name, string type, bool optional, object? defaultValue, string description) {}
    }
}", parseOptions);
            var compilation = CSharpCompilation.Create(
                "GeneratorTests",
                syntaxTrees.Concat(new[] { stubTree }),
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var generator = new EvalToolMetadataGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator }, parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
            var generatedSources = outputCompilation.SyntaxTrees
                .Skip(syntaxTrees.Length + 1)
                .Select(tree => tree.GetText().ToString())
                .ToArray();
            return new TestGeneratorResult(diagnostics, generatedSources);
        }

        private static string FindPackageRoot()
        {
            var configured = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "UnityEvalToolPackageRoot")?.Value;
            if (!string.IsNullOrWhiteSpace(configured) &&
                Directory.Exists(Path.Combine(configured, "com.yuzetoolkit.yuzeevaltool")))
                return configured;
            throw new DirectoryNotFoundException(
                $"Could not locate the configured Yuze Eval Tool Packages directory '{configured}'.");
        }

        private sealed class TestGeneratorResult
        {
            public TestGeneratorResult(ImmutableArray<Diagnostic> diagnostics, string[] generatedSources)
            {
                Diagnostics = diagnostics;
                GeneratedSources = generatedSources;
            }

            public ImmutableArray<Diagnostic> Diagnostics { get; }
            public string[] GeneratedSources { get; }
        }
    }
}
