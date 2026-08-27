#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace YuzeToolkit.Eval.SourceGenerator
{
    [Generator]
    public sealed class EvalToolMetadataGenerator : ISourceGenerator
    {
        private const string EvalToolAttributeName = "YuzeToolkit.Eval.EvalToolAttribute";
        private const string EvalFunctionAttributeName = "YuzeToolkit.Eval.EvalFunctionAttribute";
        private const string EvalParameterAttributeName = "YuzeToolkit.Eval.EvalParameterAttribute";
        private const string EvalSubToolAttributeName = "YuzeToolkit.Eval.EvalSubToolAttribute";

        private static readonly HashSet<string> JavaScriptReservedIdentifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "arguments", "await", "break", "case", "catch", "class", "const", "continue",
            "debugger", "default", "delete", "do", "else", "enum", "eval", "export", "extends",
            "false", "finally", "for", "function", "if", "implements", "import", "in",
            "instanceof", "interface", "let", "new", "null", "package", "private", "protected",
            "public", "return", "static", "super", "switch", "this", "throw", "true", "try",
            "typeof", "var", "void", "while", "with", "yield"
        };

        private static readonly DiagnosticDescriptor ToolMustBePartial = new DiagnosticDescriptor(
            "UET001",
            "Eval tool type must be partial",
            "Eval tool type '{0}' must be partial so Yuze Eval Tool can generate IEvalTool metadata",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor ToolMustHaveUsableConstructor = new DiagnosticDescriptor(
            "UET002",
            "Eval tool type must have a parameterless constructor",
            "Eval tool type '{0}' must have a public or implicit parameterless constructor for EvalToolRegistry.TryRegister<TTool>()",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor ToolMustHaveFunctions = new DiagnosticDescriptor(
            "UET003",
            "Eval tool type must expose at least one function",
            "Eval tool type '{0}' must have at least one public instance method marked with EvalFunctionAttribute",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor FunctionMustBePublicInstance = new DiagnosticDescriptor(
            "UET004",
            "Eval function must be a public instance method",
            "Eval function '{0}' must be a public instance, non-generic method",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor DuplicateFunctionName = new DiagnosticDescriptor(
            "UET005",
            "Eval function names must be unique",
            "Eval tool '{0}' has more than one exported function named '{1}'",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor InvalidFunctionName = new DiagnosticDescriptor(
            "UET006",
            "Eval function name must be a non-reserved JavaScript identifier",
            "Eval function '{0}' cannot be exported because it is not a valid non-reserved JavaScript identifier",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor UnsupportedDefaultValue = new DiagnosticDescriptor(
            "UET007",
            "Eval function parameter default value is not metadata-friendly",
            "Default value for parameter '{0}' on function '{1}' cannot be represented in generated metadata and will be emitted as null",
            "UnityEvalTool",
            DiagnosticSeverity.Warning,
            true);

        private static readonly DiagnosticDescriptor NestedToolIsUnsupported = new DiagnosticDescriptor(
            "UET008",
            "Nested eval tool types are not supported",
            "Eval tool type '{0}' must be declared at namespace scope; nested tool types are not supported",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor AsyncFunctionIsUnsupported = new DiagnosticDescriptor(
            "UET009",
            "Eval functions must complete synchronously",
            "Eval function '{0}' returns asynchronously; Task, ValueTask, and async methods are not supported by generated JavaScript wrappers",
            "UnityEvalTool",
            DiagnosticSeverity.Error,
            true);

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new Receiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxReceiver is Receiver receiver)) return;

            foreach (var declaration in receiver.Candidates)
            {
                var model = context.Compilation.GetSemanticModel(declaration.SyntaxTree);
                if (!(model.GetDeclaredSymbol(declaration) is INamedTypeSymbol typeSymbol)) continue;

                var toolAttribute = GetAttribute(typeSymbol, EvalToolAttributeName);
                if (toolAttribute == null) continue;

                if (typeSymbol.TypeKind != TypeKind.Class || typeSymbol.IsAbstract)
                    continue;

                var tool = BuildToolModel(context, declaration, typeSymbol, toolAttribute);
                if (tool == null) continue;

                var hintName = GetHintName(typeSymbol);
                context.AddSource(hintName, SourceText.From(EmitSource(tool), Encoding.UTF8));
            }
        }

        private static ToolModel? BuildToolModel(
            GeneratorExecutionContext context,
            ClassDeclarationSyntax declaration,
            INamedTypeSymbol typeSymbol,
            AttributeData toolAttribute)
        {
            if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                Report(context, ToolMustBePartial, declaration.Identifier.GetLocation(), typeSymbol.ToDisplayString());
                return null;
            }

            if (typeSymbol.ContainingType != null)
            {
                Report(context, NestedToolIsUnsupported, declaration.Identifier.GetLocation(), typeSymbol.ToDisplayString());
                return null;
            }

            if (!HasParameterlessConstructor(typeSymbol))
            {
                Report(context, ToolMustHaveUsableConstructor, declaration.Identifier.GetLocation(), typeSymbol.ToDisplayString());
                return null;
            }

            var name = GetStringConstructorArgument(toolAttribute, 0);
            var description = GetStringConstructorArgument(toolAttribute, 1);
            var subTools = GetSubToolTypeNames(typeSymbol);
            var functions = new List<FunctionModel>();
            var seenFunctionNames = new HashSet<string>(StringComparer.Ordinal);
            var hasFatalError = false;

            foreach (var member in typeSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                var functionAttribute = GetAttribute(member, EvalFunctionAttributeName);
                if (functionAttribute == null) continue;

                var location = member.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation();
                if (member.DeclaredAccessibility != Accessibility.Public ||
                    member.IsStatic ||
                    member.IsGenericMethod ||
                    member.MethodKind != MethodKind.Ordinary)
                {
                    Report(context, FunctionMustBePublicInstance, location, member.Name);
                    hasFatalError = true;
                    continue;
                }

                if (!IsValidJavaScriptIdentifier(member.Name))
                {
                    Report(context, InvalidFunctionName, location, member.Name);
                    hasFatalError = true;
                    continue;
                }

                if (member.IsAsync || IsTaskLike(member.ReturnType))
                {
                    Report(context, AsyncFunctionIsUnsupported, location, member.Name);
                    hasFatalError = true;
                    continue;
                }

                if (!seenFunctionNames.Add(member.Name))
                {
                    Report(context, DuplicateFunctionName, location, typeSymbol.ToDisplayString(), member.Name);
                    hasFatalError = true;
                    continue;
                }

                var parameters = new List<ParameterModel>();
                foreach (var parameter in member.Parameters)
                {
                    var defaultValue = BuildDefaultValueLiteral(parameter, context, member.Name);
                    parameters.Add(new ParameterModel(
                        parameter.Name,
                        FormatType(parameter.Type),
                        parameter.IsOptional || parameter.HasExplicitDefaultValue,
                        defaultValue,
                        GetParameterDescription(parameter)));
                }

                functions.Add(new FunctionModel(
                    member.Name,
                    GetStringConstructorArgument(functionAttribute, 0),
                    GetSafetyLiteral(functionAttribute),
                    parameters));
            }

            if (functions.Count == 0 && subTools.Count == 0)
            {
                Report(context, ToolMustHaveFunctions, declaration.Identifier.GetLocation(), typeSymbol.ToDisplayString());
                hasFatalError = true;
            }

            if (hasFatalError) return null;

            return new ToolModel(
                GetAccessibility(typeSymbol),
                typeSymbol.IsSealed,
                GetNamespace(typeSymbol),
                GetContainingTypeNames(typeSymbol),
                typeSymbol.Name,
                name,
                description,
                functions,
                subTools);
        }

        private static AttributeData? GetAttribute(ISymbol symbol, string metadataName)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                var attributeClass = attribute.AttributeClass;
                if (attributeClass == null) continue;
                if (string.Equals(attributeClass.ToDisplayString(), metadataName, StringComparison.Ordinal) ||
                    string.Equals(attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "global::" + metadataName, StringComparison.Ordinal))
                    return attribute;
            }

            return null;
        }

        private static string GetStringConstructorArgument(AttributeData attribute, int index)
        {
            if (attribute.ConstructorArguments.Length <= index) return string.Empty;
            return attribute.ConstructorArguments[index].Value as string ?? string.Empty;
        }

        private static string GetSafetyLiteral(AttributeData attribute)
        {
            foreach (var pair in attribute.NamedArguments)
            {
                if (!string.Equals(pair.Key, "Safety", StringComparison.Ordinal)) continue;
                var value = pair.Value.Value;
                if (value == null) break;
                return "(global::YuzeToolkit.Eval.EvalToolSafety)" +
                       Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            }

            return "global::YuzeToolkit.Eval.EvalToolSafety.Unspecified";
        }

        private static string GetParameterDescription(IParameterSymbol parameter)
        {
            var attribute = GetAttribute(parameter, EvalParameterAttributeName);
            return attribute == null ? string.Empty : GetStringConstructorArgument(attribute, 0);
        }

        private static IReadOnlyList<string> GetSubToolTypeNames(INamedTypeSymbol typeSymbol)
        {
            var result = new List<string>();
            foreach (var attribute in typeSymbol.GetAttributes())
            {
                var attributeClass = attribute.AttributeClass;
                if (attributeClass == null) continue;
                var isSubToolAttribute =
                    string.Equals(attributeClass.ToDisplayString(), EvalSubToolAttributeName, StringComparison.Ordinal) ||
                    string.Equals(attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "global::" + EvalSubToolAttributeName, StringComparison.Ordinal);
                if (!isSubToolAttribute || attribute.ConstructorArguments.Length == 0) continue;
                if (attribute.ConstructorArguments[0].Value is INamedTypeSymbol subToolType)
                    result.Add(subToolType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }

            return result;
        }

        private static bool HasParameterlessConstructor(INamedTypeSymbol typeSymbol)
        {
            var constructors = typeSymbol.InstanceConstructors
                .Where(constructor => !constructor.IsStatic && !constructor.IsImplicitlyDeclared)
                .ToList();
            if (constructors.Count == 0) return true;

            return constructors.Any(constructor =>
                constructor.Parameters.Length == 0 &&
                constructor.DeclaredAccessibility == Accessibility.Public);
        }

        private static bool IsTaskLike(ITypeSymbol type)
        {
            if (!(type is INamedTypeSymbol namedType)) return false;
            var definition = namedType.OriginalDefinition;
            if (!string.Equals(definition.ContainingNamespace?.ToDisplayString(), "System.Threading.Tasks",
                    StringComparison.Ordinal))
                return false;
            return string.Equals(definition.MetadataName, "Task", StringComparison.Ordinal) ||
                   string.Equals(definition.MetadataName, "Task`1", StringComparison.Ordinal) ||
                   string.Equals(definition.MetadataName, "ValueTask", StringComparison.Ordinal) ||
                   string.Equals(definition.MetadataName, "ValueTask`1", StringComparison.Ordinal);
        }

        private static string BuildDefaultValueLiteral(IParameterSymbol parameter, GeneratorExecutionContext context, string functionName)
        {
            if (!parameter.HasExplicitDefaultValue) return "null";

            var value = parameter.ExplicitDefaultValue;
            if (value == null) return "null";
            if (value is string text) return "@\"" + EscapeVerbatimString(text) + "\"";
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is char character) return "'" + EscapeChar(character) + "'";
            if (value is byte || value is sbyte || value is short || value is ushort || value is int)
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
            if (value is uint)
                return Convert.ToString(value, CultureInfo.InvariantCulture) + "u";
            if (value is long)
                return Convert.ToString(value, CultureInfo.InvariantCulture) + "L";
            if (value is ulong)
                return Convert.ToString(value, CultureInfo.InvariantCulture) + "UL";
            if (value is float single)
                return single.ToString("R", CultureInfo.InvariantCulture) + "f";
            if (value is double dbl)
                return dbl.ToString("R", CultureInfo.InvariantCulture) + "d";
            if (value is decimal dec)
                return dec.ToString(CultureInfo.InvariantCulture) + "m";

            var location = parameter.Locations.FirstOrDefault();
            Report(context, UnsupportedDefaultValue, location, parameter.Name, functionName);
            return "null";
        }

        private static string FormatType(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_String) return "string";
            if (type.SpecialType == SpecialType.System_Boolean) return "bool";
            if (type.SpecialType == SpecialType.System_Byte) return "byte";
            if (type.SpecialType == SpecialType.System_Int16) return "short";
            if (type.SpecialType == SpecialType.System_Int32) return "int";
            if (type.SpecialType == SpecialType.System_Int64) return "long";
            if (type.SpecialType == SpecialType.System_Single) return "float";
            if (type.SpecialType == SpecialType.System_Double) return "double";
            if (type.SpecialType == SpecialType.System_Decimal) return "decimal";
            if (type.SpecialType == SpecialType.System_Object) return "object";

            if (type is IArrayTypeSymbol arrayType)
                return FormatType(arrayType.ElementType) + "[]";

            if (IsNullable(type, out var nullableType))
                return FormatType(nullableType) + "?";

            if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var name = namedType.Name;
                return name + "<" + string.Join(", ", namedType.TypeArguments.Select(FormatType)) + ">";
            }

            return type.Name;
        }

        private static bool IsNullable(ITypeSymbol type, out ITypeSymbol underlyingType)
        {
            underlyingType = type;
            if (type is INamedTypeSymbol namedType &&
                namedType.IsGenericType &&
                namedType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T &&
                namedType.TypeArguments.Length == 1)
            {
                underlyingType = namedType.TypeArguments[0];
                return true;
            }

            return false;
        }

        private static string EmitSource(ToolModel tool)
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated/>");
            builder.AppendLine("#nullable enable");
            builder.AppendLine();

            var indent = string.Empty;
            if (!string.IsNullOrEmpty(tool.NamespaceName))
            {
                builder.Append("namespace ").AppendLine(tool.NamespaceName);
                builder.AppendLine("{");
                indent = "    ";
            }

            foreach (var containingType in tool.ContainingTypeNames)
            {
                builder.Append(indent).Append("partial class ").AppendLine(containingType);
                builder.Append(indent).AppendLine("{");
                indent += "    ";
            }

            builder.Append(indent)
                .Append(tool.Accessibility)
                .Append(tool.IsSealed ? " sealed" : string.Empty)
                .Append(" partial class ")
                .Append(tool.TypeName)
                .AppendLine(" : global::YuzeToolkit.Eval.IEvalTool");
            builder.Append(indent).AppendLine("{");

            var bodyIndent = indent + "    ";
            builder.Append(bodyIndent)
                .AppendLine("private static readonly global::System.Collections.Generic.IReadOnlyList<global::YuzeToolkit.Eval.EvalToolFunctionDescriptor> __evalFunctions =");
            builder.Append(bodyIndent).AppendLine("    new global::YuzeToolkit.Eval.EvalToolFunctionDescriptor[]");
            builder.Append(bodyIndent).AppendLine("    {");

            foreach (var function in tool.Functions)
            {
                builder.Append(bodyIndent).AppendLine("        new global::YuzeToolkit.Eval.EvalToolFunctionDescriptor(");
                builder.Append(bodyIndent).Append("            @\"").Append(EscapeVerbatimString(function.Name)).AppendLine("\",");
                builder.Append(bodyIndent).Append("            @\"").Append(EscapeVerbatimString(function.Description)).AppendLine("\",");
                builder.Append(bodyIndent).AppendLine("            new global::YuzeToolkit.Eval.EvalToolParameterDescriptor[]");
                builder.Append(bodyIndent).AppendLine("            {");
                foreach (var parameter in function.Parameters)
                {
                    builder.Append(bodyIndent).AppendLine("                new global::YuzeToolkit.Eval.EvalToolParameterDescriptor(");
                    builder.Append(bodyIndent).Append("                    @\"").Append(EscapeVerbatimString(parameter.Name)).AppendLine("\",");
                    builder.Append(bodyIndent).Append("                    @\"").Append(EscapeVerbatimString(parameter.Type)).AppendLine("\",");
                    builder.Append(bodyIndent).Append("                    ").Append(parameter.Optional ? "true" : "false").AppendLine(",");
                    builder.Append(bodyIndent).Append("                    ").Append(parameter.DefaultValueLiteral).AppendLine(",");
                    builder.Append(bodyIndent).Append("                    @\"").Append(EscapeVerbatimString(parameter.Description)).AppendLine("\"),");
                }

                builder.Append(bodyIndent).AppendLine("            },");
                builder.Append(bodyIndent).Append("            ").Append(function.SafetyLiteral).AppendLine("),");
            }

            builder.Append(bodyIndent).AppendLine("    };");
            builder.AppendLine();
            builder.Append(bodyIndent)
                .AppendLine("private static readonly global::System.Collections.Generic.IReadOnlyList<global::YuzeToolkit.Eval.IEvalTool> __evalSubTools =");
            builder.Append(bodyIndent).AppendLine("    new global::YuzeToolkit.Eval.IEvalTool[]");
            builder.Append(bodyIndent).AppendLine("    {");
            foreach (var subToolTypeName in tool.SubToolTypeNames)
                builder.Append(bodyIndent).Append("        new ").Append(subToolTypeName).AppendLine("(),");
            builder.Append(bodyIndent).AppendLine("    };");
            builder.AppendLine();
            builder.Append(bodyIndent).Append("public string Name => @\"").Append(EscapeVerbatimString(tool.ToolName)).AppendLine("\";");
            builder.Append(bodyIndent).Append("public string Description => @\"").Append(EscapeVerbatimString(tool.Description)).AppendLine("\";");
            builder.Append(bodyIndent).AppendLine("public global::System.Collections.Generic.IReadOnlyList<global::YuzeToolkit.Eval.EvalToolFunctionDescriptor> Functions => __evalFunctions;");
            builder.Append(bodyIndent).AppendLine("public global::System.Collections.Generic.IReadOnlyList<global::YuzeToolkit.Eval.IEvalTool> SubTools => __evalSubTools;");
            builder.Append(indent).AppendLine("}");

            for (var i = tool.ContainingTypeNames.Count - 1; i >= 0; i--)
            {
                indent = indent.Substring(0, indent.Length - 4);
                builder.Append(indent).AppendLine("}");
            }

            if (!string.IsNullOrEmpty(tool.NamespaceName))
                builder.AppendLine("}");

            return builder.ToString();
        }

        private static string GetAccessibility(INamedTypeSymbol typeSymbol)
        {
            if (typeSymbol.DeclaredAccessibility == Accessibility.Internal) return "internal";
            return "public";
        }

        private static string GetNamespace(INamedTypeSymbol typeSymbol)
        {
            var containingNamespace = typeSymbol.ContainingNamespace;
            return containingNamespace == null || containingNamespace.IsGlobalNamespace
                ? string.Empty
                : containingNamespace.ToDisplayString();
        }

        private static IReadOnlyList<string> GetContainingTypeNames(INamedTypeSymbol typeSymbol)
        {
            var names = new Stack<string>();
            var containingType = typeSymbol.ContainingType;
            while (containingType != null)
            {
                names.Push(containingType.Name);
                containingType = containingType.ContainingType;
            }

            return names.ToList();
        }

        private static string GetHintName(INamedTypeSymbol typeSymbol)
        {
            var name = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty)
                .Replace('<', '_')
                .Replace('>', '_')
                .Replace('.', '_');
            return name + ".UnityEvalTool.g.cs";
        }

        private static bool IsValidJavaScriptIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (JavaScriptReservedIdentifiers.Contains(value)) return false;
            if (!(char.IsLetter(value[0]) || value[0] == '_' || value[0] == '$')) return false;
            for (var i = 1; i < value.Length; i++)
            {
                var c = value[i];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$')) return false;
            }

            return true;
        }

        private static string EscapeVerbatimString(string value) =>
            value.Replace("\"", "\"\"");

        private static string EscapeChar(char value)
        {
            return value switch
            {
                '\'' => "\\'",
                '\\' => "\\\\",
                '\0' => "\\0",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => value.ToString()
            };
        }

        private static void Report(
            GeneratorExecutionContext context,
            DiagnosticDescriptor descriptor,
            Location? location,
            params object[] messageArgs)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
        }

        private sealed class Receiver : ISyntaxReceiver
        {
            public List<ClassDeclarationSyntax> Candidates { get; } = new List<ClassDeclarationSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode is ClassDeclarationSyntax classDeclaration &&
                    classDeclaration.AttributeLists.Count > 0)
                    Candidates.Add(classDeclaration);
            }
        }

        private sealed class ToolModel
        {
            public ToolModel(
                string accessibility,
                bool isSealed,
                string namespaceName,
                IReadOnlyList<string> containingTypeNames,
                string typeName,
                string toolName,
                string description,
                IReadOnlyList<FunctionModel> functions,
                IReadOnlyList<string> subToolTypeNames)
            {
                Accessibility = accessibility;
                IsSealed = isSealed;
                NamespaceName = namespaceName;
                ContainingTypeNames = containingTypeNames;
                TypeName = typeName;
                ToolName = toolName;
                Description = description;
                Functions = functions;
                SubToolTypeNames = subToolTypeNames;
            }

            public string Accessibility { get; }
            public bool IsSealed { get; }
            public string NamespaceName { get; }
            public IReadOnlyList<string> ContainingTypeNames { get; }
            public string TypeName { get; }
            public string ToolName { get; }
            public string Description { get; }
            public IReadOnlyList<FunctionModel> Functions { get; }
            public IReadOnlyList<string> SubToolTypeNames { get; }
        }

        private sealed class FunctionModel
        {
            public FunctionModel(
                string name,
                string description,
                string safetyLiteral,
                IReadOnlyList<ParameterModel> parameters)
            {
                Name = name;
                Description = description;
                SafetyLiteral = safetyLiteral;
                Parameters = parameters;
            }

            public string Name { get; }
            public string Description { get; }
            public string SafetyLiteral { get; }
            public IReadOnlyList<ParameterModel> Parameters { get; }
        }

        private sealed class ParameterModel
        {
            public ParameterModel(string name, string type, bool optional, string defaultValueLiteral, string description)
            {
                Name = name;
                Type = type;
                Optional = optional;
                DefaultValueLiteral = defaultValueLiteral;
                Description = description;
            }

            public string Name { get; }
            public string Type { get; }
            public bool Optional { get; }
            public string DefaultValueLiteral { get; }
            public string Description { get; }
        }
    }
}
