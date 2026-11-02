using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SamCoupe.Sdk;

namespace SamCoupe.Compiler;

internal static class CompilerPipeline
{
    private static readonly ImmutableHashSet<SpecialType> NumericTypes =
    [
        SpecialType.System_Byte,
        SpecialType.System_SByte,
        SpecialType.System_Int16,
        SpecialType.System_UInt16,
        SpecialType.System_Char,
        SpecialType.System_Int32,
        SpecialType.System_UInt32
    ];

    internal static CompileResult Compile(SourceFile[] files, CompileOptions options)
    {
        var diagnostics = new List<CompileDiagnostic>();
        if (files.Length == 0)
        {
            diagnostics.Add(new CompileDiagnostic("SAM0001", "At least one C# source file is required.", string.Empty));
            return Failure(diagnostics);
        }

        var trees = new List<SyntaxTree>();
        for (var index = 0; index < files.Length; index++)
        {
            var file = files[index];
            var path = string.IsNullOrWhiteSpace(file.Path) ? $"input{index}.cs" : file.Path;
            trees.Add(CSharpSyntaxTree.ParseText(file.Text ?? string.Empty, new CSharpParseOptions(LanguageVersion.Preview), path));
        }

        var references = GetReferences();
        var compilation = CSharpCompilation.Create(
            "SamCoupeProgram",
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: false,
                checkOverflow: false,
                nullableContextOptions: NullableContextOptions.Enable));

        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            {
                diagnostics.Add(ToDiagnostic(diagnostic));
            }
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Failure(diagnostics);
        }

        var methods = new Dictionary<IMethodSymbol, MethodDeclarationSyntax>(SymbolEqualityComparer.Default);
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is { } symbol)
                {
                    methods[symbol.OriginalDefinition] = declaration;
                }
            }

            ValidateSourceShape(tree, model, diagnostics);
        }

        var entries = methods
            .Where(pair => pair.Key.Name == "Main" && pair.Key.ContainingType.ToDisplayString() != "SamCoupe.Sdk.Sam")
            .ToArray();
        if (entries.Length != 1)
        {
            diagnostics.Add(new CompileDiagnostic(
                "SAM0002",
                entries.Length == 0 ? "Exactly one static void Main() entry point is required." : "Multiple Main methods were found; exactly one entry point is allowed.",
                string.Empty));
            return Failure(diagnostics);
        }

        var main = entries[0].Key;
        if (!main.IsStatic || !main.ReturnsVoid || main.Parameters.Length != 0 || main.Arity != 0)
        {
            AddSymbolError(diagnostics, "SAM0003", "The entry point must be declared `static void Main()` with no parameters.", entries[0].Value.Identifier.GetLocation());
        }

        var reachable = GetReachableMethods(main, methods, compilation, diagnostics);
        foreach (var method in reachable)
        {
            if (methods.TryGetValue(method, out var syntax))
            {
                ValidateMethod(method, syntax, compilation.GetSemanticModel(syntax.SyntaxTree), diagnostics);
            }
        }

        var referencedFields = GetReferencedFields(reachable, methods, compilation);
        foreach (var field in referencedFields)
        {
            var syntax = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (syntax?.Parent?.Parent is FieldDeclarationSyntax declaration)
            {
                ValidateDataField(declaration, compilation.GetSemanticModel(declaration.SyntaxTree), diagnostics);
            }
        }

        DetectRecursion(main, reachable, methods, compilation, diagnostics);

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Failure(diagnostics);
        }

        var maxCallDepth = GetMaxCallDepth(main, reachable, methods, compilation);
        var stackBytesRequired = checked((maxCallDepth + 4) * 2 + 128);
        if (stackBytesRequired > options.StackLimitBytes)
        {
            diagnostics.Add(new CompileDiagnostic(
                "SAM0019",
                $"The acyclic call graph requires {stackBytesRequired} bytes of resident stack, exceeding the configured {options.StackLimitBytes}-byte limit.",
                string.Empty));
            return Failure(diagnostics);
        }

        var emittedMethods = reachable
            .Where(methods.ContainsKey)
            .OrderBy(symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
            .ToArray();
        var emitter = new Z80Emitter(compilation, methods, emittedMethods, main, options, referencedFields);
        var output = emitter.Emit();
        diagnostics.AddRange(emitter.Diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Failure(diagnostics);
        }

        return new CompileResult(
            true,
            output,
            diagnostics.ToImmutableArray(),
            emitter.SourceMap.ToImmutableArray(),
            maxCallDepth,
            stackBytesRequired);
    }

    private static ImmutableArray<MetadataReference> GetReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var sdkPath = typeof(Sam).Assembly.Location;
        if (!paths.Contains(sdkPath, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(sdkPath);
        }

        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToImmutableArray<MetadataReference>();
    }

    private static void ValidateSourceShape(SyntaxTree tree, SemanticModel model, List<CompileDiagnostic> diagnostics)
    {
        foreach (var node in tree.GetRoot().DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ClassDeclarationSyntax @class:
                    if (!@class.Modifiers.Any(SyntaxKind.StaticKeyword))
                    {
                        AddSyntaxError(diagnostics, "SAM0004", "Only static classes are supported.", @class.Identifier.GetLocation());
                    }

                    foreach (var member in @class.Members)
                    {
                        if (member is not FieldDeclarationSyntax and not MethodDeclarationSyntax)
                        {
                            AddSyntaxError(diagnostics, "SAM0005", "Only static method declarations are supported in source classes.", member.GetLocation());
                        }
                    }

                    break;
                case StructDeclarationSyntax or RecordDeclarationSyntax or InterfaceDeclarationSyntax or EnumDeclarationSyntax:
                    AddSyntaxError(diagnostics, "SAM0006", "Only static classes and static methods are supported.", node.GetLocation());
                    break;
                case GlobalStatementSyntax:
                    AddSyntaxError(diagnostics, "SAM0007", "Top-level statements are not supported; declare `static void Main()`.", node.GetLocation());
                    break;
                case MethodDeclarationSyntax method:
                    var symbol = model.GetDeclaredSymbol(method);
                    if (symbol is not null && !symbol.IsStatic)
                    {
                        AddSyntaxError(diagnostics, "SAM0008", "Instance methods are not supported.", method.Identifier.GetLocation());
                    }

                    if (method.TypeParameterList is not null ||
                        method.Modifiers.Any(SyntaxKind.AsyncKeyword) ||
                        method.Modifiers.Any(SyntaxKind.ExternKeyword) ||
                        method.Modifiers.Any(SyntaxKind.AbstractKeyword))
                    {
                        AddSyntaxError(diagnostics, "SAM0009", "Generic, async, extern and abstract methods are not supported.", method.Identifier.GetLocation());
                    }

                    if (method.Body is null || method.ExpressionBody is not null)
                    {
                        AddSyntaxError(diagnostics, "SAM0010", "Methods must have a block body.", method.Identifier.GetLocation());
                    }

                    break;
                case NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax:
                case CompilationUnitSyntax:
                case AttributeListSyntax:
                case UsingDirectiveSyntax:
                    break;
                case CheckedStatementSyntax checkedStatement when checkedStatement.Keyword.IsKind(SyntaxKind.CheckedKeyword):
                case CheckedExpressionSyntax checkedExpression when checkedExpression.Keyword.IsKind(SyntaxKind.CheckedKeyword):
                    AddSyntaxError(diagnostics, "SAM0011", "Checked arithmetic is not supported; arithmetic is unchecked.", node.GetLocation());
                    break;
            }
        }
    }

    private static void ValidateMethod(
        IMethodSymbol method,
        MethodDeclarationSyntax syntax,
        SemanticModel model,
        List<CompileDiagnostic> diagnostics)
    {
        if (!IsSupportedType(method.ReturnType, allowVoid: true))
        {
            AddSymbolError(diagnostics, "SAM0012", $"Return type '{method.ReturnType}' is not supported.", syntax.ReturnType.GetLocation());
        }

        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || !IsSupportedType(parameter.Type, allowVoid: false))
            {
                AddSymbolError(diagnostics, "SAM0013", $"Parameter '{parameter.Name}' has an unsupported type or passing mode.", syntax.ParameterList.GetLocation());
            }
        }

        foreach (var variable in syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (model.GetDeclaredSymbol(variable) is ILocalSymbol local && !IsSupportedType(local.Type, allowVoid: false))
            {
                AddSymbolError(diagnostics, "SAM0014", $"Local '{local.Name}' has unsupported type '{local.Type}'.", variable.Identifier.GetLocation());
            }
        }

        foreach (var parameterSyntax in syntax.ParameterList.Parameters)
        {
            if (parameterSyntax.Modifiers.Count != 0 || parameterSyntax.Default is not null)
            {
                AddSyntaxError(diagnostics, "SAM0015", "ref/out/in/params and optional parameters are not supported.", parameterSyntax.GetLocation());
            }
        }

        foreach (var expression in syntax.DescendantNodes().OfType<ExpressionSyntax>())
        {
            var expressionType = model.GetTypeInfo(expression).Type;
            var symbol = model.GetSymbolInfo(expression).Symbol;
            if (expressionType is not null &&
                expressionType.TypeKind != TypeKind.Error &&
                symbol is not IMethodSymbol and not INamespaceOrTypeSymbol &&
                !IsSupportedType(expressionType, allowVoid: true))
            {
                AddSymbolError(
                    diagnostics,
                    "SAM0021",
                    $"Expression type '{expressionType}' is not supported; long/int64 and implicit 64-bit arithmetic are not compiled.",
                    expression.GetLocation());
            }
        }

        foreach (var element in syntax.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
        {
            var type = model.GetTypeInfo(element.Expression).Type;
            if (type?.SpecialType == SpecialType.System_String ||
                type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte or SpecialType.System_UInt16 })
            {
                if (element.ArgumentList.Arguments.Count != 1 ||
                    model.GetTypeInfo(element.ArgumentList.Arguments[0].Expression).Type?.SpecialType is not
                        (SpecialType.System_Byte or SpecialType.System_UInt16 or
                         SpecialType.System_Int32 or SpecialType.System_UInt32))
                {
                    AddSyntaxError(diagnostics, "SAM0022", "Array indexing requires one byte, ushort, int, or uint index.", element.GetLocation());
                }

                continue;
            }

            AddSyntaxError(
                diagnostics,
                "SAM0022",
                "Only one-dimensional byte[] and ushort[] indexing is supported.",
                element.GetLocation());
        }

        foreach (var creation in syntax.DescendantNodes().OfType<ArrayCreationExpressionSyntax>())
        {
            AddSyntaxError(
                diagnostics,
                "SAM0023",
                "Runtime array creation is not supported; declare data as a static readonly byte[] field.",
                creation.GetLocation());
        }

        foreach (var creation in syntax.DescendantNodes().OfType<ImplicitArrayCreationExpressionSyntax>())
        {
            AddSyntaxError(
                diagnostics,
                "SAM0023",
                "Runtime array creation is not supported; declare data as a static readonly byte[] field.",
                creation.GetLocation());
        }
    }

    private static bool IsSupportedType(ITypeSymbol type, bool allowVoid)
    {
        if (allowVoid && type.SpecialType == SpecialType.System_Void)
        {
            return true;
        }

        return type.SpecialType == SpecialType.System_String || NumericTypes.Contains(type.SpecialType) ||
               type.SpecialType == SpecialType.System_Boolean || IsByteArray(type) || IsWordArray(type) ||
               type.ToDisplayString() == "SamCoupe.Sdk.SpriteFrame";
    }

    private static void ValidateDataField(
        FieldDeclarationSyntax field,
        SemanticModel model,
        List<CompileDiagnostic> diagnostics)
    {
        var type = model.GetTypeInfo(field.Declaration.Type).Type;
        if (!field.Modifiers.Any(SyntaxKind.StaticKeyword) || type is null)
        {
            AddSyntaxError(diagnostics, "SAM0024", "Source fields must be static.", field.GetLocation());
            return;
        }

        if (field.Modifiers.Any(SyntaxKind.ConstKeyword))
        {
            return;
        }

        if (IsByteArray(type) || IsWordArray(type))
        {
            if (!field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            {
                AddSyntaxError(diagnostics, "SAM0024", "Resident arrays must be static readonly references; their elements may be changed.", field.GetLocation());
                return;
            }

            foreach (var variable in field.Declaration.Variables)
            {
                var initializer = GetArrayInitializer(variable);
                var elementType = ((IArrayTypeSymbol)type).ElementType;
                if (initializer is null || initializer.Expressions.Count == 0 ||
                    initializer.Expressions.Any(expression =>
                        !model.GetConstantValue(expression).HasValue ||
                        model.GetConstantValue(expression).Value is not { } value ||
                        !TryGetArrayElementValue(value, elementType, out _)))
                {
                    AddSyntaxError(diagnostics, "SAM0025", "A resident byte[] or ushort[] field must use a non-empty initializer containing only in-range constants.", variable.GetLocation());
                }

                if (model.GetDeclaredSymbol(variable) is IFieldSymbol symbol)
                {
                    var banked = symbol.GetAttributes().FirstOrDefault(attribute =>
                        attribute.AttributeClass?.ToDisplayString() == "SamCoupe.Sdk.BankedDataAttribute");
                    if (banked is not null &&
                        (banked.ConstructorArguments.Length != 1 ||
                         banked.ConstructorArguments[0].Value is not int page ||
                         page is < 4 or > 14))
                    {
                        AddSyntaxError(diagnostics, "SAM0026", "Banked data pages must be in the range 4 through 14.", variable.GetLocation());
                    }
                }
            }

            return;
        }

        if (!IsSupportedScalarField(type))
        {
            AddSyntaxError(diagnostics, "SAM0024", $"Static field type '{type}' is not supported.", field.GetLocation());
            return;
        }

        foreach (var variable in field.Declaration.Variables)
        {
            if (variable.Initializer is not null &&
                (!model.GetConstantValue(variable.Initializer.Value).HasValue ||
                 !IsSupportedConstantType(type)))
                AddSyntaxError(diagnostics, "SAM0025", "Static scalar fields must have constant initializers.", variable.GetLocation());
        }
    }

    private static InitializerExpressionSyntax? GetArrayInitializer(VariableDeclaratorSyntax variable) =>
        variable.Initializer?.Value switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } arrayInitializer } => arrayInitializer,
            ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInitializer } => implicitInitializer,
            InitializerExpressionSyntax directInitializer => directInitializer,
            _ => null
        };

    private static bool IsSupportedScalarField(ITypeSymbol type) =>
        NumericTypes.Contains(type.SpecialType) || type.SpecialType == SpecialType.System_Boolean;

    private static bool IsSupportedConstantType(ITypeSymbol type) =>
        NumericTypes.Contains(type.SpecialType) || type.SpecialType == SpecialType.System_Boolean;

    private static bool TryGetArrayElementValue(object value, ITypeSymbol elementType, out uint result)
    {
        try
        {
            var number = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            var maximum = elementType.SpecialType == SpecialType.System_Byte ? byte.MaxValue : ushort.MaxValue;
            if (number is >= 0 && number <= maximum)
            {
                result = (uint)number;
                return true;
            }
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
        }

        result = 0;
        return false;
    }

    private static bool TryGetByteValue(object value, out byte result)
    {
        try
        {
            var number = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            if (number is >= byte.MinValue and <= byte.MaxValue)
            {
                result = (byte)number;
                return true;
            }
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
        }

        result = 0;
        return false;
    }

    private static bool IsByteArray(ITypeSymbol type) =>
        type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte };

    private static bool IsWordArray(ITypeSymbol type) =>
        type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_UInt16 };

    private static HashSet<IFieldSymbol> GetReferencedFields(
        HashSet<IMethodSymbol> reachable,
        Dictionary<IMethodSymbol, MethodDeclarationSyntax> methods,
        Compilation compilation)
    {
        var fields = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
        foreach (var method in reachable)
        {
            if (!methods.TryGetValue(method, out var syntax))
                continue;
            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            foreach (var node in syntax.DescendantNodes())
                if (model.GetSymbolInfo(node).Symbol is IFieldSymbol field)
                    fields.Add(field.OriginalDefinition);
        }

        return fields;
    }

    private static HashSet<IMethodSymbol> GetReachableMethods(
        IMethodSymbol main,
        Dictionary<IMethodSymbol, MethodDeclarationSyntax> methods,
        Compilation compilation,
        List<CompileDiagnostic> diagnostics)
    {
        var reached = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<IMethodSymbol>();
        pending.Push(main.OriginalDefinition);
        while (pending.TryPop(out var method))
        {
            if (!reached.Add(method) || !methods.TryGetValue(method, out var syntax))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target)
                {
                    continue;
                }

                target = target.OriginalDefinition;
                if (IsSamIntrinsic(target, compilation))
                {
                    if (!IsSupportedIntrinsic(target))
                    {
                        AddSyntaxError(diagnostics, "SAM0016", $"Unsupported SAM SDK intrinsic '{target.Name}'.", invocation.GetLocation());
                    }

                    continue;
                }

                if (methods.ContainsKey(target))
                {
                    pending.Push(target);
                }
                else
                {
                    AddSyntaxError(diagnostics, "SAM0017", $"Calls to external method '{target.ToDisplayString()}' are not supported.", invocation.GetLocation());
                }
            }
        }

        return reached;
    }

    private static bool IsSupportedIntrinsic(IMethodSymbol method) =>
        method.Name switch
        {
            nameof(Sam.ClearScreen) or nameof(Sam.WaitForKey) when method.Parameters.Length == 0 && method.ReturnsVoid => true,
            nameof(Sam.DrawOpaqueSprite) when IsSamType(method.ContainingType) &&
                method.Parameters.Length == 2 && IsByteArray(method.Parameters[0].Type) &&
                method.Parameters[1].Type.SpecialType == SpecialType.System_UInt16 && method.ReturnsVoid => true,
            nameof(Sam.BuildInterleavedSprite) when IsSamType(method.ContainingType) &&
                method.Parameters.Length == 2 && IsByteArray(method.Parameters[0].Type) &&
                IsByteArray(method.Parameters[1].Type) && method.ReturnsVoid => true,
            nameof(Sam.SelectSpriteFrame) when IsSamType(method.ContainingType) &&
                method.Parameters.Length == 2 && IsByteArray(method.Parameters[0].Type) &&
                method.Parameters[1].Type.SpecialType == SpecialType.System_Byte &&
                method.ReturnType.ToDisplayString() == "SamCoupe.Sdk.SpriteFrame" => true,
            nameof(Sam.LoadTutorialPalette) when IsSamType(method.ContainingType) &&
                method.Parameters.Length == 1 && IsByteArray(method.Parameters[0].Type) && method.ReturnsVoid => true,
            nameof(Sam.DrawBuiltMaskedSprite) or nameof(Sam.ClearSprite) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && method.ReturnsVoid => true,
            nameof(Sam.FlipScreen) when IsSamType(method.ContainingType) &&
                method.Parameters.Length == 0 && method.ReturnsVoid => true,
            nameof(Sam.BeginDoubleBuffer) when IsSamType(method.ContainingType) && method.ReturnsVoid &&
                (method.Parameters.Length == 0 ||
                method.Parameters.Length == 2 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_Byte &&
                method.Parameters[1].Type.SpecialType == SpecialType.System_Byte) => true,
            nameof(Sam.DrawMaskedSprite) or nameof(Sam.DrawRleMaskedSprite) when IsSamType(method.ContainingType) &&
                method.Parameters.Length == 4 &&
                method.Parameters[0].Type.ToDisplayString() == "SamCoupe.Sdk.SpriteFrame" &&
                method.Parameters[1].Type.SpecialType == SpecialType.System_UInt16 &&
                method.Parameters[2].Type.SpecialType == SpecialType.System_Byte &&
                method.Parameters[3].Type.SpecialType == SpecialType.System_Byte && method.ReturnsVoid => true,
            nameof(Sam.FillScreen) or nameof(Sam.WaitForFrames) when IsSamType(method.ContainingType) &&
                method.Parameters is [{ Type.SpecialType: SpecialType.System_Byte }] && method.ReturnsVoid => true,
            nameof(Sam.FillStripedBackground) or nameof(Sam.SetPaletteEntry) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && method.ReturnsVoid => true,
            nameof(Sam.IsKeyPressed) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && method.ReturnType.SpecialType == SpecialType.System_Boolean => true,
            nameof(Sam.AnySolidPixelInPackedRow) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var maskType },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && IsByteArray(maskType) &&
                method.ReturnType.SpecialType == SpecialType.System_Boolean => true,
            nameof(Sam.AnySolidPixelInPackedScreenColumn) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var maskType },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && IsByteArray(maskType) &&
                method.ReturnType.SpecialType == SpecialType.System_Boolean => true,
            nameof(Sam.AnySolidPixelInPackedRectangle) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var maskType },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && IsByteArray(maskType) &&
                method.ReturnType.SpecialType == SpecialType.System_Boolean => true,
            nameof(Sam.MapMemoryPage) when IsSamType(method.ContainingType) &&
                method.Parameters is [{ Type.SpecialType: SpecialType.System_Byte }] && method.ReturnsVoid => true,
            nameof(Sam.RestoreMemoryPage) when IsSamType(method.ContainingType) &&
                method.Parameters.Length == 0 && method.ReturnsVoid => true,
            nameof(Sam.CopyBytes) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var sourceType },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type: var destinationType },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_UInt16 }
                ] && IsByteArray(sourceType) && IsByteArray(destinationType) && method.ReturnsVoid => true,
            nameof(Sam.DecodeRleBackground) or nameof(Sam.ExpandByteRuns) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var sourceType },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type: var destinationType },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && IsByteArray(sourceType) && IsByteArray(destinationType) && method.ReturnsVoid => true,
            nameof(Sam.ShiftMaskedSpriteOnePixel) when IsSamType(method.ContainingType) &&
                method.Parameters is [{ Type: var sourceType }, { Type: var destinationType }] &&
                IsByteArray(sourceType) && IsByteArray(destinationType) && method.ReturnsVoid => true,
            nameof(Sam.CopyBytesToScreen) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var sourceType },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_UInt16 }
                ] && IsByteArray(sourceType) && method.ReturnsVoid => true,
            nameof(Sam.CopyBankedBytesToScreen) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var sourceType },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && IsByteArray(sourceType) && method.ReturnsVoid => true,
            nameof(Sam.ComposeTransparentImage) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var sourceType },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type: var destinationType },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && IsByteArray(sourceType) && IsByteArray(destinationType) && method.ReturnsVoid => true,
            nameof(Sam.DrawPackedOutline) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type: var destinationType },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_UInt16 },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && IsByteArray(destinationType) && method.ReturnsVoid => true,
            nameof(Sam.SetBorderColor) when IsSamType(method.ContainingType) &&
                method.Parameters is [{ Type.SpecialType: SpecialType.System_Byte }] && method.ReturnsVoid => true,
            nameof(Sam.WriteSaaRegister) when IsSamType(method.ContainingType) &&
                method.Parameters is [
                { Type.SpecialType: SpecialType.System_Byte },
                { Type.SpecialType: SpecialType.System_Byte }
                ] && method.ReturnsVoid => true,
            nameof(Sam.WriteScreenByte) when IsSamType(method.ContainingType) &&
                method.Parameters is [{ Type.SpecialType: SpecialType.System_UInt16 }, { Type.SpecialType: SpecialType.System_Byte }] &&
                method.ReturnsVoid => true,
            nameof(Sam.Print) when method.Parameters is [{ Type.SpecialType: SpecialType.System_String }] && method.ReturnsVoid => true,
            nameof(Sam.PrintChar) when method.Parameters is [{ Type.SpecialType: SpecialType.System_Char }] && method.ReturnsVoid => true,
            _ => false
        };

    internal static bool IsSamIntrinsic(IMethodSymbol method, Compilation compilation)
    {
        var root = compilation.GetTypeByMetadataName("SamCoupe.Sdk.Sam");
        var rom = root?.GetTypeMembers("Rom").SingleOrDefault();
        return root is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, root) ||
               rom is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, rom);
    }

    internal static bool IsSamRomIntrinsic(IMethodSymbol method, Compilation compilation)
    {
        var root = compilation.GetTypeByMetadataName("SamCoupe.Sdk.Sam");
        var rom = root?.GetTypeMembers("Rom").SingleOrDefault();
        return rom is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, rom);
    }

    private static bool IsSamType(INamedTypeSymbol type) =>
        type.ToDisplayString() == "SamCoupe.Sdk.Sam";

    private static void DetectRecursion(
        IMethodSymbol main,
        HashSet<IMethodSymbol> reachable,
        Dictionary<IMethodSymbol, MethodDeclarationSyntax> methods,
        Compilation compilation,
        List<CompileDiagnostic> diagnostics)
    {
        var state = new Dictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
        var reported = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        void Visit(IMethodSymbol method)
        {
            method = method.OriginalDefinition;
            if (state.TryGetValue(method, out var color))
            {
                if (color == 1 && reported.Add(method) && methods.TryGetValue(method, out var recursiveSyntax))
                {
                    AddSyntaxError(diagnostics, "SAM0018", $"Recursive call cycle contains '{method.Name}'; recursion is not supported.", recursiveSyntax.Identifier.GetLocation());
                }

                return;
            }

            state[method] = 1;
            if (methods.TryGetValue(method, out var syntax))
            {
                var model = compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(call).Symbol is IMethodSymbol target &&
                        !IsSamIntrinsic(target, compilation) &&
                        reachable.Contains(target.OriginalDefinition))
                    {
                        Visit(target.OriginalDefinition);
                    }
                }
            }

            state[method] = 2;
        }

        Visit(main);
    }

    private static int GetMaxCallDepth(
        IMethodSymbol main,
        HashSet<IMethodSymbol> reachable,
        Dictionary<IMethodSymbol, MethodDeclarationSyntax> methods,
        Compilation compilation)
    {
        var depths = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        int Depth(IMethodSymbol method)
        {
            method = method.OriginalDefinition;
            if (depths.TryGetValue(method, out var cached))
            {
                return cached;
            }

            var depth = 1;
            if (methods.TryGetValue(method, out var syntax))
            {
                var model = compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol target &&
                        !IsSamIntrinsic(target, compilation) &&
                        reachable.Contains(target.OriginalDefinition))
                    {
                        depth = Math.Max(depth, 1 + Depth(target));
                    }
                }
            }

            depths[method] = depth;
            return depth;
        }

        return Depth(main);
    }

    private static void AddSymbolError(List<CompileDiagnostic> diagnostics, string code, string message, Location location) =>
        AddSyntaxError(diagnostics, code, message, location);

    internal static void AddSyntaxError(List<CompileDiagnostic> diagnostics, string code, string message, Location location)
    {
        var line = location.GetLineSpan();
        diagnostics.Add(new CompileDiagnostic(
            code,
            message,
            line.Path,
            line.StartLinePosition.Line + 1,
            line.StartLinePosition.Character + 1,
            DiagnosticSeverity.Error));
    }

    private static CompileDiagnostic ToDiagnostic(Microsoft.CodeAnalysis.Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetLineSpan();
        var severity = diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Error;
        return new CompileDiagnostic(
            diagnostic.Id,
            diagnostic.GetMessage(),
            diagnostic.Location == Location.None ? string.Empty : line.Path,
            diagnostic.Location == Location.None ? 0 : line.StartLinePosition.Line + 1,
            diagnostic.Location == Location.None ? 0 : line.StartLinePosition.Character + 1,
            severity);
    }

    private static CompileResult Failure(List<CompileDiagnostic> diagnostics) =>
        new(false, string.Empty, diagnostics.ToImmutableArray(), [], 0, 0);
}
