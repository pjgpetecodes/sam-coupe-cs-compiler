using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SamCoupe.Compiler;

internal sealed class Z80Emitter
{
    private const byte NullStringFault = 1;
    private const byte StringBoundsFault = 2;
    private const byte DivideByZeroFault = 3;
    private const byte SignedOverflowFault = 4;
    private const byte NonAsciiFault = 6;

    private readonly Compilation _compilation;
    private readonly Dictionary<IMethodSymbol, MethodDeclarationSyntax> _methodSyntax;
    private readonly IMethodSymbol[] _methods;
    private readonly IMethodSymbol _main;
    private readonly CompileOptions _options;
    private StringBuilder _code = new();
    private readonly List<CompileDiagnostic> _diagnostics = [];
    private readonly List<SequencePoint> _sourceMap = [];
    private readonly List<Slot> _slots = [];
    private readonly Dictionary<ISymbol, Slot> _variableSlots = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, byte[]> _staticInitializers = new(StringComparer.Ordinal);
    private readonly Dictionary<IFieldSymbol, DataField> _dataFields = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, string> _methodLabels = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, string> _methodCallLabels = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, int> _bankedMethodPages = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, string> _bankedMethodCode = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, string> _stringLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _helpers = new(StringComparer.Ordinal);
    private readonly Stack<(string Break, string Continue)> _loops = new();
    private readonly string _returnSlot = "cs_return_value";
    private bool _usesReturnSlot;
    private SemanticModel _model = null!;
    private IMethodSymbol _currentMethod = null!;
    private int _nextSlot;
    private int _nextLabel;
    private int _nextSequencePoint;

    internal Z80Emitter(
        Compilation compilation,
        Dictionary<IMethodSymbol, MethodDeclarationSyntax> methodSyntax,
        IMethodSymbol[] methods,
        IMethodSymbol main,
        CompileOptions options,
        IReadOnlySet<IFieldSymbol> referencedFields)
    {
        _compilation = compilation;
        _methodSyntax = methodSyntax;
        _methods = methods;
        _main = main;
        _options = options;
        var dataIndex = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var field in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                var fieldType = model.GetTypeInfo(field.Declaration.Type).Type;
                if (fieldType is not IArrayTypeSymbol
                    { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte or SpecialType.System_UInt16 })
                {
                    continue;
                }

                foreach (var declarator in field.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol symbol ||
                        !referencedFields.Contains(symbol.OriginalDefinition))
                    {
                        continue;
                    }

                    var initializer = GetArrayInitializer(declarator);
                    if (initializer is null)
                        continue;
                    var elementSize = ((IArrayTypeSymbol)fieldType).ElementType.SpecialType == SpecialType.System_UInt16 ? 2 : 1;
                    var values = initializer.Expressions
                        .SelectMany(expression => EncodeArrayElement(
                            model.GetConstantValue(expression).Value,
                            elementSize))
                        .ToArray();
                    var page = GetBankPage(symbol);
                    _dataFields[symbol.OriginalDefinition] =
                        new DataField($"cs_data_{++dataIndex:D3}", initializer.Expressions.Count, values, elementSize, page);
                }
            }
        }

        foreach (var field in referencedFields)
        {
            if (field.HasConstantValue || _dataFields.ContainsKey(field))
                continue;
            var slot = AddVariableSlot(field);
            var initializer = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
            if (initializer?.Initializer is { } value)
            {
                var constant = compilation.GetSemanticModel(value.SyntaxTree).GetConstantValue(value.Value);
                if (constant.HasValue)
                    _staticInitializers[slot.Name] = EncodeScalar(constant.Value, field.Type);
            }
        }

        for (var index = 0; index < methods.Length; index++)
        {
            _methodLabels[methods[index]] = $"cs_method_{index + 1:D3}";
            int? bankPage = GetBankedCodePage(methods[index]);
            if (bankPage is not null)
                _bankedMethodPages[methods[index]] = bankPage.Value;
        }

        foreach (var method in methods)
        {
            _methodCallLabels[method] = _bankedMethodPages.ContainsKey(method)
                ? $"cs_call_{_methodLabels[method]}"
                : _methodLabels[method];
        }

        foreach (var method in methods)
        {
            var syntax = methodSyntax[method];
            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            foreach (var parameter in method.Parameters)
            {
                AddVariableSlot(parameter);
            }

            foreach (var declaration in syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is ILocalSymbol local && !local.IsConst)
                {
                    AddVariableSlot(local);
                }
            }
        }
    }

    internal List<CompileDiagnostic> Diagnostics => _diagnostics;
    internal List<SequencePoint> SourceMap => _sourceMap;

    internal string Emit()
    {
        var occupiedPages = _bankedMethodPages.Values.ToHashSet();
        foreach (var group in _dataFields.Values.Where(data => data.BankPage is not null)
                     .GroupBy(data => data.BankPage!.Value))
        {
            var bytes = 2 + group.Sum(data => data.Bytes.Length + 2);
            for (var offset = 0; offset < bytes; offset += 0x4000)
                occupiedPages.Add(group.Key + offset / 0x4000);
        }
        var initialScreenPage = Enumerable.Range(2, 6).Select(index => index * 2)
            .FirstOrDefault(page => !occupiedPages.Contains(page) && !occupiedPages.Contains(page + 1));
        if (initialScreenPage == 0)
        {
            Unsupported(_methodSyntax[_main],
                "Banked code/data leave no free 24 KB MODE 4 startup screen pair in 256 KB RAM.");
            initialScreenPage = 4;
        }
        _code.Append("cs_initial_screen_page: EQU ").AppendLine(initialScreenPage.ToString());
        _code.AppendLine("        ORG 0x8000");
        _code.AppendLine("        DUMP 1,0");
        _code.AppendLine("        AUTOEXEC");
        _code.AppendLine("        JP sam_start");
        _code.AppendLine();
        _code.AppendLine("cs_main:");
        if (_dataFields.Values.Any(RequiresBankedInitialization))
            _code.AppendLine("        CALL cs_initialize_banked_data");
        _code.Append("        CALL ").AppendLine(_methodCallLabels[_main]);
        _code.AppendLine("        RET");

        foreach (var method in _methods)
        {
            if (_bankedMethodPages.TryGetValue(method, out int page))
            {
                var resident = _code;
                _code = new StringBuilder();
                EmitMethod(method);
                _bankedMethodCode[method] = _code.ToString();
                _code = resident;
            }
            else
            {
                EmitMethod(method);
            }
        }

        EmitHelpers();
        EmitBankedInitializers();
        EmitData();
        EmitBankedCodeCallers();
        _code.Append("        INCLUDE \"")
            .Append(_options.RuntimeInclude.Replace("\"", "\"\"", StringComparison.Ordinal))
            .AppendLine("\"");
        _code.AppendLine("sam_cli_image_end:");
        _code.AppendLine("cs_resident_end:");
        EmitBankedCode();
        EmitBankedData();
        return _code.ToString();
    }

    private void EmitMethod(IMethodSymbol method)
    {
        _currentMethod = method;
        var syntax = _methodSyntax[method];
        _model = _compilation.GetSemanticModel(syntax.SyntaxTree);
        _code.AppendLine();
        _code.Append(_methodLabels[method]).AppendLine(":");
        EmitStatement(syntax.Body!);
        if (method.ReturnsVoid)
        {
            _code.AppendLine("        RET");
        }
    }

    private void EmitStatement(StatementSyntax statement)
    {
        if (statement is not (BlockSyntax or EmptyStatementSyntax))
        {
            EmitSequencePoint(statement);
        }

        switch (statement)
        {
            case BlockSyntax block:
                foreach (var child in block.Statements)
                {
                    EmitStatement(child);
                }

                break;
            case EmptyStatementSyntax:
                break;
            case LocalDeclarationStatementSyntax local:
                if (local.Modifiers.Any(SyntaxKind.ConstKeyword))
                {
                    Unsupported(local, "const local declarations are not yet supported.");
                    break;
                }

                EmitLocalDeclaration(local.Declaration);
                break;
            case ExpressionStatementSyntax expression:
                EmitDiscardedValue(_model.GetOperation(expression.Expression)!);
                break;
            case IfStatementSyntax conditional:
                EmitIf(conditional);
                break;
            case WhileStatementSyntax loop:
                EmitWhile(loop);
                break;
            case ForStatementSyntax loop:
                EmitFor(loop);
                break;
            case BreakStatementSyntax:
                if (_loops.TryPeek(out var breakContext))
                {
                    _code.Append("        JP ").AppendLine(breakContext.Break);
                }

                break;
            case ContinueStatementSyntax:
                if (_loops.TryPeek(out var continueContext))
                {
                    _code.Append("        JP ").AppendLine(continueContext.Continue);
                }

                break;
            case ReturnStatementSyntax ret:
                if (ret.Expression is not null)
                {
                    var value = EmitValue(_model.GetOperation(ret.Expression)!);
                    CopyConverted(value, value.Type, _returnSlot, _currentMethod.ReturnType);
                }

                _code.AppendLine("        RET");
                break;
            case CheckedStatementSyntax checkedStatement when checkedStatement.Keyword.IsKind(SyntaxKind.UncheckedKeyword):
                EmitStatement(checkedStatement.Block);
                break;
            case CheckedStatementSyntax:
                Unsupported(statement, "checked arithmetic is not supported.");
                break;
            default:
                Unsupported(statement, $"Statement '{statement.Kind()}' is not supported.");
                break;
        }
    }

    private void EmitIf(IfStatementSyntax statement)
    {
        var elseLabel = NewLabel("else");
        var endLabel = NewLabel("endif");
        EmitFalseBranch(_model.GetOperation(statement.Condition)!, elseLabel);
        EmitStatement(statement.Statement);
        if (statement.Else is not null)
        {
            _code.Append("        JP ").AppendLine(endLabel);
        }

        _code.Append(elseLabel).AppendLine(":");
        if (statement.Else is not null)
        {
            EmitStatement(statement.Else.Statement);
            _code.Append(endLabel).AppendLine(":");
        }
    }

    private void EmitWhile(WhileStatementSyntax statement)
    {
        var test = NewLabel("while_test");
        var body = NewLabel("while_body");
        var end = NewLabel("while_end");
        _code.Append(test).AppendLine(":");
        EmitFalseBranch(_model.GetOperation(statement.Condition)!, end);
        _code.Append(body).AppendLine(":");
        _loops.Push((end, test));
        EmitStatement(statement.Statement);
        _loops.Pop();
        _code.Append("        JP ").AppendLine(test);
        _code.Append(end).AppendLine(":");
    }

    private void EmitFor(ForStatementSyntax statement)
    {
        if (statement.Declaration is not null)
        {
            EmitLocalDeclaration(statement.Declaration);
        }

        foreach (var initializer in statement.Initializers)
        {
            EmitDiscardedValue(_model.GetOperation(initializer)!);
        }

        var test = NewLabel("for_test");
        var increment = NewLabel("for_increment");
        var body = NewLabel("for_body");
        var end = NewLabel("for_end");
        _code.Append(test).AppendLine(":");
        if (statement.Condition is not null)
        {
            EmitFalseBranch(_model.GetOperation(statement.Condition)!, end);
        }

        _code.Append(body).AppendLine(":");
        _loops.Push((end, increment));
        EmitStatement(statement.Statement);
        _loops.Pop();
        _code.Append(increment).AppendLine(":");
        foreach (var expression in statement.Incrementors)
        {
            EmitDiscardedValue(_model.GetOperation(expression)!);
        }

        _code.Append("        JP ").AppendLine(test);
        _code.Append(end).AppendLine(":");
    }

    private void EmitFalseBranch(IOperation operation, string label)
    {
        if (operation is IBinaryOperation andOperation &&
            andOperation.OperatorKind == BinaryOperatorKind.ConditionalAnd)
        {
            EmitFalseBranch(andOperation.LeftOperand, label);
            EmitFalseBranch(andOperation.RightOperand, label);
            return;
        }

        if (operation is IBinaryOperation orOperation &&
            orOperation.OperatorKind == BinaryOperatorKind.ConditionalOr)
        {
            var end = NewLabel("bool_or_true");
            EmitTrueBranch(orOperation.LeftOperand, end);
            EmitFalseBranch(orOperation.RightOperand, label);
            _code.Append(end).AppendLine(":");
            return;
        }

        if (operation is IBinaryOperation comparison &&
            TryEmitByteComparisonBranch(comparison, label, branchWhenTrue: false))
        {
            return;
        }

        var condition = EmitValue(operation);
        _code.Append("        LD A,(").Append(condition.Name).AppendLine(")");
        _code.AppendLine("        OR A");
        _code.Append("        JP Z,").AppendLine(label);
    }

    private void EmitTrueBranch(IOperation operation, string label)
    {
        if (operation is IBinaryOperation orOperation &&
            orOperation.OperatorKind == BinaryOperatorKind.ConditionalOr)
        {
            EmitTrueBranch(orOperation.LeftOperand, label);
            EmitTrueBranch(orOperation.RightOperand, label);
            return;
        }

        if (operation is IBinaryOperation andOperation &&
            andOperation.OperatorKind == BinaryOperatorKind.ConditionalAnd)
        {
            var end = NewLabel("bool_and_false");
            EmitFalseBranch(andOperation.LeftOperand, end);
            EmitTrueBranch(andOperation.RightOperand, label);
            _code.Append(end).AppendLine(":");
            return;
        }

        if (operation is IBinaryOperation comparison &&
            TryEmitByteComparisonBranch(comparison, label, branchWhenTrue: true))
        {
            return;
        }

        var condition = EmitValue(operation);
        _code.Append("        LD A,(").Append(condition.Name).AppendLine(")");
        _code.AppendLine("        OR A");
        _code.Append("        JP NZ,").AppendLine(label);
    }

    private bool TryEmitByteComparisonBranch(IBinaryOperation comparison, string label, bool branchWhenTrue)
    {
        if (comparison.OperatorKind is not (
                BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or
                BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
                BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual))
        {
            return false;
        }

        BinaryOperatorKind operation = branchWhenTrue ? comparison.OperatorKind switch
        {
            BinaryOperatorKind.Equals => BinaryOperatorKind.NotEquals,
            BinaryOperatorKind.NotEquals => BinaryOperatorKind.Equals,
            BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThanOrEqual,
            BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThan,
            BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThanOrEqual,
            BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThan,
            _ => throw new InvalidOperationException()
        } : comparison.OperatorKind;

        if (!TryGetByteVariableSlot(comparison.LeftOperand, out Slot source))
        {
            return false;
        }

        if (TryGetByteVariableSlot(comparison.RightOperand, out Slot right))
        {
            _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
            _code.AppendLine("        LD B,A");
            _code.Append("        LD A,(").Append(right.Name).AppendLine(")");
            _code.AppendLine("        LD C,A");
            _code.AppendLine("        LD A,B");
            _code.AppendLine("        CP C");
            EmitFalseJumpForComparison(operation, label);
            return true;
        }

        if (comparison.RightOperand.ConstantValue is not { HasValue: true, Value: int limit } ||
            limit is < 0 or > byte.MaxValue)
            return false;

        // The conversion to C# int is lossless for byte operands. Compare
        // their exact 0..255 domain directly instead of materializing int slots.
        switch (operation)
        {
            case BinaryOperatorKind.LessThan:
                _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
                _code.Append("        CP ").Append(limit).AppendLine();
                _code.Append("        JP NC,").AppendLine(label);
                break;
            case BinaryOperatorKind.LessThanOrEqual:
                if (limit == byte.MaxValue)
                    return true;
                _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
                _code.Append("        CP ").Append(limit + 1).AppendLine();
                _code.Append("        JP NC,").AppendLine(label);
                break;
            case BinaryOperatorKind.GreaterThan:
                if (limit == byte.MaxValue)
                {
                    _code.Append("        JP ").AppendLine(label);
                    return true;
                }
                _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
                _code.Append("        CP ").Append(limit + 1).AppendLine();
                _code.Append("        JP C,").AppendLine(label);
                break;
            case BinaryOperatorKind.GreaterThanOrEqual:
                if (limit == 0)
                    return true;
                _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
                _code.Append("        CP ").Append(limit).AppendLine();
                _code.Append("        JP C,").AppendLine(label);
                break;
            case BinaryOperatorKind.Equals:
                _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
                _code.Append("        CP ").Append(limit).AppendLine();
                _code.Append("        JP NZ,").AppendLine(label);
                break;
            case BinaryOperatorKind.NotEquals:
                _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
                _code.Append("        CP ").Append(limit).AppendLine();
                _code.Append("        JP Z,").AppendLine(label);
                break;
        }

        return true;
    }

    private bool TryGetByteVariableSlot(IOperation operation, out Slot slot)
    {
        while (operation is IConversionOperation { IsImplicit: true, Type.SpecialType: SpecialType.System_Int32 } conversion)
        {
            operation = conversion.Operand;
        }

        slot = operation switch
        {
            ILocalReferenceOperation { Type.SpecialType: SpecialType.System_Byte } local =>
                GetVariableSlot(local.Local),
            IParameterReferenceOperation { Type.SpecialType: SpecialType.System_Byte } parameter =>
                GetVariableSlot(parameter.Parameter),
            IFieldReferenceOperation { Type.SpecialType: SpecialType.System_Byte } field
                when _variableSlots.TryGetValue(field.Field, out var fieldSlot) => fieldSlot,
            _ => null!
        };
        return slot is not null;
    }

    private void EmitFalseJumpForComparison(BinaryOperatorKind operation, string label)
    {
        switch (operation)
        {
            case BinaryOperatorKind.Equals:
                _code.Append("        JP NZ,").AppendLine(label);
                break;
            case BinaryOperatorKind.NotEquals:
                _code.Append("        JP Z,").AppendLine(label);
                break;
            case BinaryOperatorKind.LessThan:
                _code.Append("        JP NC,").AppendLine(label);
                break;
            case BinaryOperatorKind.LessThanOrEqual:
                var greater = NewLabel("byte_cmp_greater");
                _code.Append("        JP C,").AppendLine(greater);
                _code.Append("        JP NZ,").AppendLine(label);
                _code.Append(greater).AppendLine(":");
                break;
            case BinaryOperatorKind.GreaterThan:
                _code.Append("        JP C,").AppendLine(label);
                _code.Append("        JP Z,").AppendLine(label);
                break;
            case BinaryOperatorKind.GreaterThanOrEqual:
                _code.Append("        JP C,").AppendLine(label);
                break;
        }
    }

    private void EmitDiscardedValue(IOperation operation)
    {
        switch (operation)
        {
            case IIncrementOrDecrementOperation increment:
                EmitIncrement(increment, null);
                break;
            case IInvocationOperation invocation when invocation.TargetMethod.ReturnsVoid:
                EmitInvocation(invocation, null);
                break;
            default:
                EmitValue(operation);
                break;
        }
    }

    private void EmitLocalDeclaration(VariableDeclarationSyntax declaration)
    {
        foreach (var variable in declaration.Variables)
        {
            if (variable.Initializer is null || _model.GetDeclaredSymbol(variable) is not ILocalSymbol symbol)
            {
                continue;
            }

            var constant = _model.GetConstantValue(variable.Initializer.Value);
            if (constant.HasValue && IsSupportedConstantType(symbol.Type))
            {
                EmitConstant(GetVariableSlot(symbol), symbol.Type, constant.Value);
            }
            else
            {
                var value = EmitValue(_model.GetOperation(variable.Initializer.Value)!);
                CopyConverted(value, value.Type, GetVariableSlot(symbol), symbol.Type);
            }
        }
    }

    private Slot EmitValue(IOperation operation)
    {
        if (operation is IParenthesizedOperation parenthesized)
        {
            return EmitValue(parenthesized.Operand);
        }

        var type = operation.Type;
        if (operation is ILocalReferenceOperation local)
        {
            return GetVariableSlot(local.Local);
        }

        if (operation is IParameterReferenceOperation parameter)
        {
            return GetVariableSlot(parameter.Parameter);
        }

        if (operation is IFieldReferenceOperation fieldReference &&
            !fieldReference.Field.HasConstantValue &&
            !_dataFields.ContainsKey(fieldReference.Field.OriginalDefinition) &&
            _variableSlots.ContainsKey(fieldReference.Field))
        {
            return GetVariableSlot(fieldReference.Field);
        }

        var result = NewSlot(type, "tmp");
        var constant = _model.GetConstantValue(operation.Syntax);
        if (constant.HasValue && IsSupportedConstantType(type))
        {
            EmitConstant(result, type!, constant.Value);
            return result;
        }

        switch (operation)
        {
            case IConversionOperation conversion:
                if (TryEmitNarrowCast(conversion, result))
                {
                    return result;
                }

                var input = EmitValue(conversion.Operand);
                Convert(input, conversion.Operand.Type!, result, conversion.Type!);
                return result;
            case IDefaultValueOperation:
                EmitConstant(result, type!, type!.SpecialType == SpecialType.System_String ? null : 0);
                return result;
            case IBinaryOperation binary:
                EmitBinary(binary, result);
                return result;
            case IUnaryOperation unary:
                EmitUnary(unary, result);
                return result;
            case ISimpleAssignmentOperation assignment:
                EmitSimpleAssignment(assignment, result);
                return result;
            case ICompoundAssignmentOperation compound:
                EmitCompoundAssignment(compound, result);
                return result;
            case IIncrementOrDecrementOperation increment:
                EmitIncrement(increment, result);
                return result;
            case IInvocationOperation invocation:
                EmitInvocation(invocation, result);
                return result;
            case IPropertyReferenceOperation property:
                EmitProperty(property, result);
                return result;
            case IFieldReferenceOperation field when field.Field.HasConstantValue:
                EmitConstant(result, type!, field.Field.ConstantValue);
                return result;
            case IFieldReferenceOperation field when _dataFields.TryGetValue(field.Field.OriginalDefinition, out var data):
                _code.Append("        LD HL,").AppendLine(data.Label);
                _code.Append("        LD (").Append(result.Name).AppendLine("),HL");
                return result;
            case IArrayElementReferenceOperation element:
                EmitArrayElementRead(element.Type!, EmitArrayAddress(element), result);
                return result;
            case ILiteralOperation literal when literal.ConstantValue.HasValue:
                EmitConstant(
                    result,
                    literal.Type ?? _compilation.GetSpecialType(SpecialType.System_String),
                    literal.ConstantValue.Value);
                return result;
            case IParenthesizedOperation:
                throw new InvalidOperationException("Parenthesized operation should have been unwrapped.");
            default:
                Unsupported(operation.Syntax, $"Expression '{operation.Kind}' is not supported.");
                EmitConstant(result, type ?? _compilation.GetSpecialType(SpecialType.System_Int32), 0);
                return result;
        }
    }

    private void EmitBinary(IBinaryOperation binary, Slot result)
    {
        if (TryEmitPackedScreenOffset(binary, result))
        {
            return;
        }

        if (binary.Type?.SpecialType == SpecialType.System_String && binary.OperatorKind == BinaryOperatorKind.Add)
        {
            var value = _model.GetConstantValue(binary.Syntax);
            if (value.HasValue && value.Value is string text)
            {
                EmitConstant(result, binary.Type, text);
            }
            else
            {
                Unsupported(binary.Syntax, "Only compile-time constant string concatenation is supported.");
                EmitConstant(result, binary.Type, null);
            }

            return;
        }

        if (binary.LeftOperand.Type?.SpecialType == SpecialType.System_String &&
            binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            var left = EmitValue(binary.LeftOperand);
            var right = EmitValue(binary.RightOperand);
            Copy(left, "cs_string_left", 2);
            Copy(right, "cs_string_right", 2);
            _helpers.Add("string_equals");
            _code.AppendLine("        CALL cs_string_equals");
            _code.Append("        LD (").Append(result.Name).AppendLine("),A");
            if (binary.OperatorKind == BinaryOperatorKind.NotEquals)
            {
                _code.Append("        LD A,(").Append(result.Name).AppendLine(")");
                _code.AppendLine("        XOR 1");
                _code.Append("        LD (").Append(result.Name).AppendLine("),A");
            }

            return;
        }

        if (binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
        {
            EmitShortCircuit(binary, result);
            return;
        }

        if (binary.OperatorKind is
                BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or
                BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
                BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual &&
            TryEmitByteComparisonValue(binary, result))
        {
            return;
        }

        var leftValue = EmitValue(binary.LeftOperand);
        var rightValue = EmitValue(binary.RightOperand);
        var size = SizeOf(binary.Type);
        var op = binary.OperatorKind;
        if (op is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or
            BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
            BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
        {
            var comparisonType = binary.LeftOperand.Type!;
            EmitComparison(op, leftValue, rightValue, comparisonType, comparisonType, result, SizeOf(comparisonType));
            return;
        }

        if (op is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            EmitAddSub(leftValue, rightValue, result, size, op == BinaryOperatorKind.Subtract);
            return;
        }

        if (op is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr)
        {
            EmitBitwise(leftValue, rightValue, result, size, op);
            return;
        }

        if (op is BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder)
        {
            PrepareMath(leftValue, rightValue);
            if (op == BinaryOperatorKind.Multiply)
            {
                _helpers.Add("mul32");
                _code.AppendLine("        CALL cs_mul32");
            }
            else if (IsSigned(binary.Type!))
            {
                _helpers.Add("signed_div32");
                _code.Append(op == BinaryOperatorKind.Divide ? "        CALL cs_div32" : "        CALL cs_rem32").AppendLine();
            }
            else
            {
                EmitDivisionByZeroGuard();
                _helpers.Add("udiv32");
                _code.AppendLine("        CALL cs_udiv32");
                if (op == BinaryOperatorKind.Remainder)
                {
                    Copy("cs_math_remainder", "cs_math_result", 4);
                }
            }

            Copy("cs_math_result", result, SizeOf(binary.Type));
            return;
        }

        if (op is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift)
        {
            PrepareMath(leftValue, rightValue);
            _code.Append("        LD A,(cs_math_rhs)").AppendLine();
            _code.AppendLine("        AND 0x1F");
            _code.AppendLine("        LD (cs_shift_count),A");
            var signedRight = op == BinaryOperatorKind.RightShift && IsSigned(binary.LeftOperand.Type!);
            var helper = op == BinaryOperatorKind.LeftShift ? "shift_left32" : signedRight ? "shift_right_signed32" : "shift_right32";
            _helpers.Add(helper);
            Copy("cs_math_lhs", "cs_math_result", 4);
            _code.Append("        CALL cs_").Append(helper).AppendLine();
            Copy("cs_math_result", result, SizeOf(binary.Type));
            return;
        }

        Unsupported(binary.Syntax, $"Binary operator '{op}' is not supported.");
        EmitConstant(result, binary.Type!, 0);
    }

    private bool TryEmitPackedScreenOffset(IBinaryOperation binary, Slot result)
    {
        if (binary.OperatorKind != BinaryOperatorKind.Add ||
            binary.Type?.SpecialType != SpecialType.System_Int32)
        {
            return false;
        }

        if (!TryGetScaledByte(binary.LeftOperand, BinaryOperatorKind.Multiply, 128, out Slot row) ||
            !TryGetScaledByte(binary.RightOperand, BinaryOperatorKind.Divide, 2, out Slot column))
        {
            if (!TryGetScaledByte(binary.RightOperand, BinaryOperatorKind.Multiply, 128, out row) ||
                !TryGetScaledByte(binary.LeftOperand, BinaryOperatorKind.Divide, 2, out column))
            {
                return false;
            }
        }

        // Byte coordinates are promoted to int by C#, but y*128 + x/2 is
        // always in 0..32767. Its packed high/low bytes can be assembled
        // directly, avoiding the general 32-bit multiply and divide helpers.
        _code.Append("        LD A,(").Append(row.Name).AppendLine(")");
        _code.AppendLine("        LD B,A");
        _code.AppendLine("        SRL A");
        _code.Append("        LD (").Append(result.Name).AppendLine("+1),A");
        _code.Append("        LD A,(").Append(column.Name).AppendLine(")");
        _code.AppendLine("        SRL A");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,B");
        _code.AppendLine("        RRCA");
        _code.AppendLine("        AND 0x80");
        _code.AppendLine("        OR C");
        _code.Append("        LD (").Append(result.Name).AppendLine("+0),A");
        _code.AppendLine("        XOR A");
        _code.Append("        LD (").Append(result.Name).AppendLine("+2),A");
        _code.Append("        LD (").Append(result.Name).AppendLine("+3),A");
        return true;
    }

    private bool TryGetScaledByte(
        IOperation operation,
        BinaryOperatorKind operatorKind,
        int factor,
        out Slot slot)
    {
        if (operation is IBinaryOperation binary &&
            binary.OperatorKind == operatorKind &&
            _model.GetConstantValue(binary.RightOperand.Syntax) is { HasValue: true, Value: int value } &&
            value == factor &&
            TryGetByteVariableSlot(binary.LeftOperand, out slot))
        {
            return true;
        }

        slot = null!;
        return false;
    }

    private bool TryEmitByteComparisonValue(IBinaryOperation comparison, Slot result)
    {
        if (!TryGetByteVariableSlot(comparison.LeftOperand, out Slot left) ||
            !TryGetByteVariableSlot(comparison.RightOperand, out Slot right))
        {
            return false;
        }

        _code.Append("        LD A,(").Append(left.Name).AppendLine(")");
        _code.AppendLine("        LD B,A");
        _code.Append("        LD A,(").Append(right.Name).AppendLine(")");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,B");
        _code.AppendLine("        CP C");

        string yes = NewLabel("byte_cmp_true");
        string no = NewLabel("byte_cmp_false");
        string end = NewLabel("byte_cmp_end");
        switch (comparison.OperatorKind)
        {
            case BinaryOperatorKind.Equals:
                _code.Append("        JP Z,").AppendLine(yes);
                break;
            case BinaryOperatorKind.NotEquals:
                _code.Append("        JP NZ,").AppendLine(yes);
                break;
            case BinaryOperatorKind.LessThan:
                _code.Append("        JP C,").AppendLine(yes);
                break;
            case BinaryOperatorKind.LessThanOrEqual:
                _code.Append("        JP C,").AppendLine(yes);
                _code.Append("        JP Z,").AppendLine(yes);
                break;
            case BinaryOperatorKind.GreaterThan:
                _code.Append("        JP C,").AppendLine(no);
                _code.Append("        JP NZ,").AppendLine(yes);
                _code.Append(no).AppendLine(":");
                break;
            case BinaryOperatorKind.GreaterThanOrEqual:
                _code.Append("        JP NC,").AppendLine(yes);
                break;
        }

        _code.AppendLine("        LD A,0");
        _code.Append("        JP ").AppendLine(end);
        _code.Append(yes).AppendLine(":");
        _code.AppendLine("        LD A,1");
        _code.Append(end).AppendLine(":");
        _code.Append("        LD (").Append(result.Name).AppendLine("),A");
        return true;
    }

    private void EmitShortCircuit(IBinaryOperation operation, Slot result)
    {
        var shortcut = NewLabel("bool_short");
        var end = NewLabel("bool_end");
        var left = EmitValue(operation.LeftOperand);
        _code.Append("        LD A,(").Append(left.Name).AppendLine(")");
        _code.AppendLine("        OR A");
        _code.Append("        JP ").Append(operation.OperatorKind == BinaryOperatorKind.ConditionalAnd ? "Z," : "NZ,").AppendLine(shortcut);
        var right = EmitValue(operation.RightOperand);
        Copy(right, result, 1);
        _code.Append("        JP ").AppendLine(end);
        _code.Append(shortcut).AppendLine(":");
        _code.Append("        LD A,").Append(operation.OperatorKind == BinaryOperatorKind.ConditionalAnd ? "0" : "1").AppendLine();
        _code.Append("        LD (").Append(result.Name).AppendLine("),A");
        _code.Append(end).AppendLine(":");
    }

    private void EmitComparison(
        BinaryOperatorKind op,
        Slot left,
        Slot right,
        ITypeSymbol leftType,
        ITypeSymbol operationType,
        Slot result,
        int size)
    {
        var less = NewLabel("cmp_less");
        var greater = NewLabel("cmp_greater");
        var equal = NewLabel("cmp_equal");
        var yes = NewLabel("cmp_true");
        var no = NewLabel("cmp_false");
        var end = NewLabel("cmp_end");

        for (var index = size - 1; index >= 0; index--)
        {
            _code.Append("        LD A,(").Append(right.Name).Append('+').Append(index).AppendLine(")");
            if (index == size - 1 && IsSigned(operationType))
            {
                _code.AppendLine("        XOR 0x80");
            }

            _code.AppendLine("        LD C,A");
            _code.Append("        LD A,(").Append(left.Name).Append('+').Append(index).AppendLine(")");
            if (index == size - 1 && IsSigned(operationType))
            {
                _code.AppendLine("        XOR 0x80");
            }

            _code.AppendLine("        CP C");
            _code.Append("        JP C,").AppendLine(less);
            _code.Append("        JP NZ,").AppendLine(greater);
        }

        _code.Append("        JP ").AppendLine(equal);
        _code.Append(less).AppendLine(":");
        _code.Append("        JP ").AppendLine(op is BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.NotEquals ? yes : no);
        _code.Append(greater).AppendLine(":");
        _code.Append("        JP ").AppendLine(op is BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.NotEquals ? yes : no);
        _code.Append(equal).AppendLine(":");
        _code.Append("        JP ").AppendLine(op is BinaryOperatorKind.Equals or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThanOrEqual ? yes : no);
        _code.Append(yes).AppendLine(":");
        _code.Append("        LD A,1").AppendLine();
        _code.Append("        LD (").Append(result.Name).AppendLine("),A");
        _code.Append("        JP ").AppendLine(end);
        _code.Append(no).AppendLine(":");
        _code.Append("        LD A,0").AppendLine();
        _code.Append("        LD (").Append(result.Name).AppendLine("),A");
        _code.Append(end).AppendLine(":");
    }

    private void EmitAddSub(Slot left, Slot right, Slot result, int size, bool subtract)
    {
        for (var index = 0; index < size; index++)
        {
            var first = subtract ? right : left;
            var second = subtract ? left : right;
            _code.Append("        LD A,(").Append(first.Name).Append('+').Append(index).AppendLine(")");
            _code.AppendLine("        LD C,A");
            _code.Append("        LD A,(").Append(second.Name).Append('+').Append(index).AppendLine(")");
            if (index == 0)
            {
                _code.AppendLine(subtract ? "        SUB C" : "        ADD A,C");
            }
            else
            {
                _code.AppendLine(subtract ? "        SBC A,C" : "        ADC A,C");
            }

            _code.Append("        LD (").Append(result.Name).Append('+').Append(index).AppendLine("),A");
        }
    }

    private void EmitBitwise(Slot left, Slot right, Slot result, int size, BinaryOperatorKind operation)
    {
        var instruction = operation switch
        {
            BinaryOperatorKind.And => "AND",
            BinaryOperatorKind.Or => "OR",
            BinaryOperatorKind.ExclusiveOr => "XOR",
            _ => throw new InvalidOperationException()
        };
        for (var index = 0; index < size; index++)
        {
            _code.Append("        LD A,(").Append(left.Name).Append('+').Append(index).AppendLine(")");
            _code.AppendLine("        LD C,A");
            _code.Append("        LD A,(").Append(right.Name).Append('+').Append(index).AppendLine(")");
            _code.Append("        ").Append(instruction).AppendLine(" C");
            _code.Append("        LD (").Append(result.Name).Append('+').Append(index).AppendLine("),A");
        }
    }

    private void EmitUnary(IUnaryOperation unary, Slot result)
    {
        var value = EmitValue(unary.Operand);
        var size = SizeOf(unary.Type);
        switch (unary.OperatorKind)
        {
            case UnaryOperatorKind.Plus:
                Copy(value, result, size);
                break;
            case UnaryOperatorKind.Not when unary.Type?.SpecialType == SpecialType.System_Boolean:
                _code.Append("        LD A,(").Append(value.Name).AppendLine(")");
                _code.AppendLine("        XOR 1");
                _code.Append("        LD (").Append(result.Name).AppendLine("),A");
                break;
            case UnaryOperatorKind.Minus:
                Negate(value, result, size);
                break;
            case UnaryOperatorKind.BitwiseNegation:
                for (var index = 0; index < size; index++)
                {
                    _code.Append("        LD A,(").Append(value.Name).Append('+').Append(index).AppendLine(")");
                    _code.AppendLine("        CPL");
                    _code.Append("        LD (").Append(result.Name).Append('+').Append(index).AppendLine("),A");
                }

                break;
            default:
                Unsupported(unary.Syntax, $"Unary operator '{unary.OperatorKind}' is not supported.");
                EmitConstant(result, unary.Type!, 0);
                break;
        }
    }

    private bool TryEmitNarrowCast(IConversionOperation conversion, Slot result)
    {
        int size = SizeOf(conversion.Type!);
        if (conversion.IsImplicit ||
            conversion.IsChecked ||
            conversion.Conversion.IsUserDefined ||
            size is not (1 or 2) ||
            conversion.Type!.SpecialType is not (SpecialType.System_Byte or SpecialType.System_UInt16) ||
            !CanEmitNarrowValue(conversion.Operand))
        {
            return false;
        }

        EmitNarrowValue(conversion.Operand, result, size, conversion.Type);
        return true;
    }

    private bool CanEmitNarrowValue(IOperation operation)
    {
        if (operation is IParenthesizedOperation parenthesized)
        {
            return CanEmitNarrowValue(parenthesized.Operand);
        }

        if (operation is IConversionOperation conversion)
        {
            return !conversion.IsChecked &&
                   !conversion.Conversion.IsUserDefined &&
                   CanEmitNarrowValue(conversion.Operand);
        }

        if (operation is IBinaryOperation binary)
        {
            if (binary.IsChecked)
            {
                return false;
            }

            bool supported = binary.OperatorKind is
                BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or
                BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr;
            if (binary.OperatorKind == BinaryOperatorKind.Multiply)
            {
                supported = IsPowerOfTwoConstant(binary.LeftOperand) ||
                            IsPowerOfTwoConstant(binary.RightOperand);
            }

            return supported &&
                   CanEmitNarrowValue(binary.LeftOperand) &&
                   CanEmitNarrowValue(binary.RightOperand);
        }

        if (operation is IUnaryOperation unary)
        {
            return !unary.IsChecked &&
                   (unary.OperatorKind is UnaryOperatorKind.Plus or UnaryOperatorKind.Minus or
                       UnaryOperatorKind.BitwiseNegation) &&
                   CanEmitNarrowValue(unary.Operand);
        }

        return operation.Type?.SpecialType is
            SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_UInt16 or SpecialType.System_Int16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32;
    }

    private bool IsPowerOfTwoConstant(IOperation operation)
    {
        Optional<object?> constant = _model.GetConstantValue(operation.Syntax);
        if (!constant.HasValue || constant.Value is null)
        {
            return false;
        }

        try
        {
            uint value = System.Convert.ToUInt32(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            return value != 0 && (value & (value - 1)) == 0;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    private void EmitNarrowValue(IOperation operation, Slot destination, int size, ITypeSymbol destinationType)
    {
        if (operation is IParenthesizedOperation parenthesized)
        {
            EmitNarrowValue(parenthesized.Operand, destination, size, destinationType);
            return;
        }

        Optional<object?> constant = _model.GetConstantValue(operation.Syntax);
        if (constant.HasValue && constant.Value is not null &&
            operation.Type?.SpecialType is not SpecialType.System_String)
        {
            long value = System.Convert.ToInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            uint bits = unchecked((uint)value);
            object narrowed = size == 1 ? (object)(byte)bits : (ushort)bits;
            EmitConstant(destination, destinationType, narrowed);
            return;
        }

        if (operation is IConversionOperation conversion)
        {
            EmitNarrowValue(conversion.Operand, destination, size, destinationType);
            return;
        }

        if (operation is IBinaryOperation binary)
        {
            if (binary.OperatorKind == BinaryOperatorKind.Multiply)
            {
                bool shiftLeft = IsPowerOfTwoConstant(binary.LeftOperand);
                IOperation value = shiftLeft ? binary.RightOperand : binary.LeftOperand;
                Optional<object?> powerConstant = _model.GetConstantValue(
                    shiftLeft ? binary.LeftOperand.Syntax : binary.RightOperand.Syntax);
                uint power = System.Convert.ToUInt32(powerConstant.Value, System.Globalization.CultureInfo.InvariantCulture);
                int shifts = 0;
                while (power > 1)
                {
                    power >>= 1;
                    shifts++;
                }

                Slot factor = NewSlot(destinationType, "narrow_factor");
                EmitNarrowValue(value, factor, size, destinationType);
                Copy(factor, destination, size);
                for (int index = 0; index < shifts; index++)
                {
                    if (size == 1)
                    {
                        _code.Append("        LD A,(").Append(destination.Name).AppendLine(")");
                        _code.AppendLine("        ADD A,A");
                        _code.Append("        LD (").Append(destination.Name).AppendLine("),A");
                    }
                    else
                    {
                        _code.Append("        LD HL,(").Append(destination.Name).AppendLine(")");
                        _code.AppendLine("        ADD HL,HL");
                        _code.Append("        LD (").Append(destination.Name).AppendLine("),HL");
                    }
                }

                return;
            }

            if (size == 1 &&
                (binary.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract) &&
                TryGetNarrowConstant(binary.RightOperand, out byte rightConstant))
            {
                Slot leftValue = NewSlot(destinationType, "narrow_immediate");
                EmitNarrowValue(binary.LeftOperand, leftValue, size, destinationType);
                _code.Append("        LD A,(").Append(leftValue.Name).AppendLine(")");
                _code.Append("        ").Append(binary.OperatorKind == BinaryOperatorKind.Add ? "ADD A," : "SUB ")
                    .Append(rightConstant).AppendLine();
                _code.Append("        LD (").Append(destination.Name).AppendLine("),A");
                return;
            }

            if (size == 1 &&
                binary.OperatorKind == BinaryOperatorKind.Add &&
                TryGetNarrowConstant(binary.LeftOperand, out byte leftConstant))
            {
                Slot rightValue = NewSlot(destinationType, "narrow_immediate");
                EmitNarrowValue(binary.RightOperand, rightValue, size, destinationType);
                _code.Append("        LD A,(").Append(rightValue.Name).AppendLine(")");
                _code.Append("        ADD A,").Append(leftConstant).AppendLine();
                _code.Append("        LD (").Append(destination.Name).AppendLine("),A");
                return;
            }

            if (size == 1 &&
                binary.OperatorKind == BinaryOperatorKind.Subtract &&
                TryGetNarrowConstant(binary.LeftOperand, out leftConstant))
            {
                Slot rightValue = NewSlot(destinationType, "narrow_immediate");
                EmitNarrowValue(binary.RightOperand, rightValue, size, destinationType);
                _code.Append("        LD A,(").Append(rightValue.Name).AppendLine(")");
                _code.AppendLine("        LD C,A");
                _code.Append("        LD A,").Append(leftConstant).AppendLine();
                _code.AppendLine("        SUB C");
                _code.Append("        LD (").Append(destination.Name).AppendLine("),A");
                return;
            }

            Slot left = NewSlot(destinationType, "narrow_left");
            Slot right = NewSlot(destinationType, "narrow_right");
            EmitNarrowValue(binary.LeftOperand, left, size, destinationType);
            EmitNarrowValue(binary.RightOperand, right, size, destinationType);
            if (binary.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
            {
                EmitAddSub(left, right, destination, size, binary.OperatorKind == BinaryOperatorKind.Subtract);
            }
            else
            {
                EmitBitwise(left, right, destination, size, binary.OperatorKind);
            }

            return;
        }

        if (operation is IUnaryOperation unary)
        {
            Slot value = NewSlot(destinationType, "narrow_unary");
            EmitNarrowValue(unary.Operand, value, size, destinationType);
            if (unary.OperatorKind == UnaryOperatorKind.Plus)
            {
                Copy(value, destination, size);
            }
            else if (unary.OperatorKind == UnaryOperatorKind.Minus)
            {
                Negate(value, destination, size);
            }
            else
            {
                for (int index = 0; index < size; index++)
                {
                    _code.Append("        LD A,(").Append(value.Name).Append('+').Append(index).AppendLine(")");
                    _code.AppendLine("        CPL");
                    _code.Append("        LD (").Append(destination.Name).Append('+').Append(index).AppendLine("),A");
                }
            }

            return;
        }

        Slot source = EmitValue(operation);
        Convert(source, source.Type!, destination, destinationType);
    }

    private bool TryGetNarrowConstant(IOperation operation, out byte value)
    {
        Optional<object?> constant = _model.GetConstantValue(operation.Syntax);
        if (constant.HasValue && constant.Value is not null)
        {
            try
            {
                long number = System.Convert.ToInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
                value = unchecked((byte)number);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
            }
        }

        value = 0;
        return false;
    }

    private void EmitSimpleAssignment(ISimpleAssignmentOperation assignment, Slot result)
    {
        if (assignment.Target is IArrayElementReferenceOperation arrayElement)
        {
            var address = EmitArrayAddress(arrayElement);
            var arrayValue = EmitValue(assignment.Value);
            EmitArrayElementWrite(arrayElement.Type!, address, arrayValue);
            Copy(arrayValue, result, SizeOf(assignment.Type));
            return;
        }

        var target = GetTargetSlot(assignment.Target);
        var value = EmitValue(assignment.Value);
        CopyConverted(value, value.Type, target, assignment.Target.Type);
        Copy(target, result, SizeOf(assignment.Type));
    }

    private void EmitCompoundAssignment(ICompoundAssignmentOperation assignment, Slot result)
    {
        if (assignment.Target is IArrayElementReferenceOperation arrayElement)
        {
            var address = EmitArrayAddress(arrayElement);
            var existing = NewSlot(arrayElement.Type, "array_compound_target");
            EmitArrayElementRead(arrayElement.Type!, address, existing);
            var arrayTargetType = arrayElement.Type!;
            var arrayOperatorType = assignment.Value.Type ?? arrayTargetType;
            var arrayLeft = NewSlot(arrayOperatorType, "array_compound_left");
            Convert(existing, arrayTargetType, arrayLeft, arrayOperatorType);
            var arrayRight = EmitValue(assignment.Value);
            var arrayRaw = NewSlot(arrayOperatorType, "array_compound_result");
            EmitCompoundOperation(assignment.OperatorKind, assignment.Syntax, arrayLeft, arrayRight, arrayRaw, arrayOperatorType);
            var converted = NewSlot(arrayTargetType, "array_compound_converted");
            Convert(arrayRaw, arrayOperatorType, converted, arrayTargetType);
            EmitArrayElementWrite(arrayElement.Type!, address, converted);
            Copy(converted, result, SizeOf(assignment.Type));
            return;
        }

        var target = GetTargetSlot(assignment.Target);
        var targetType = assignment.Target.Type!;
        var operatorType = assignment.Value.Type ?? targetType;
        var left = NewSlot(operatorType, "compound_left");
        Convert(target, targetType, left, operatorType);
        var right = EmitValue(assignment.Value);
        var raw = NewSlot(operatorType, "compound_result");
        EmitCompoundOperation(assignment.OperatorKind, assignment.Syntax, left, right, raw, operatorType);
        Convert(raw, operatorType, target, targetType);
        Copy(target, result, SizeOf(assignment.Type));
    }

    private void EmitCompoundOperation(
        BinaryOperatorKind operatorKind,
        SyntaxNode syntax,
        Slot left,
        Slot right,
        Slot raw,
        ITypeSymbol operatorType)
    {
        if (operatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            EmitAddSub(left, right, raw, SizeOf(operatorType), operatorKind == BinaryOperatorKind.Subtract);
        }
        else if (operatorKind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr)
        {
            EmitBitwise(left, right, raw, SizeOf(operatorType), operatorKind);
        }
        else if (operatorKind is BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder)
        {
            PrepareMath(left, right);
            if (operatorKind == BinaryOperatorKind.Multiply)
            {
                _helpers.Add("mul32");
                _code.AppendLine("        CALL cs_mul32");
            }
            else if (IsSigned(operatorType))
            {
                _helpers.Add("signed_div32");
                _code.Append(operatorKind == BinaryOperatorKind.Divide ? "        CALL cs_div32" : "        CALL cs_rem32").AppendLine();
            }
            else
            {
                EmitDivisionByZeroGuard();
                _helpers.Add("udiv32");
                _code.AppendLine("        CALL cs_udiv32");
                if (operatorKind == BinaryOperatorKind.Remainder)
                {
                    Copy("cs_math_remainder", "cs_math_result", 4);
                }
            }

            Copy("cs_math_result", raw, SizeOf(operatorType));
        }
        else if (operatorKind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift)
        {
            PrepareMath(left, right);
            _code.AppendLine("        LD A,(cs_math_rhs)");
            _code.AppendLine("        AND 0x1F");
            _code.AppendLine("        LD (cs_shift_count),A");
            var signedRight = operatorKind == BinaryOperatorKind.RightShift && IsSigned(operatorType);
            var helper = operatorKind == BinaryOperatorKind.LeftShift
                ? "shift_left32"
                : signedRight ? "shift_right_signed32" : "shift_right32";
            _helpers.Add(helper);
            Copy("cs_math_lhs", "cs_math_result", 4);
            _code.Append("        CALL cs_").Append(helper).AppendLine();
            Copy("cs_math_result", raw, SizeOf(operatorType));
        }
        else
        {
            Unsupported(syntax, $"Compound assignment '{operatorKind}' is not supported.");
            EmitConstant(raw, operatorType, 0);
        }
    }

    private void EmitIncrement(IIncrementOrDecrementOperation operation, Slot? result)
    {
        if (operation.Target is IArrayElementReferenceOperation arrayElement)
        {
            var address = EmitArrayAddress(arrayElement);
            var arrayTarget = NewSlot(arrayElement.Type, "array_increment_target");
            EmitArrayElementRead(arrayElement.Type!, address, arrayTarget);
            if (operation.IsPostfix && result is not null)
                Copy(arrayTarget, result, SizeOf(operation.Type));
            IncrementSlot(arrayTarget, operation);
            EmitArrayElementWrite(arrayElement.Type!, address, arrayTarget);
            if (!operation.IsPostfix && result is not null)
                Copy(arrayTarget, result, SizeOf(operation.Type));
            return;
        }

        var target = GetTargetSlot(operation.Target);
        var size = SizeOf(operation.Type);
        if (operation.IsPostfix && result is not null)
        {
            Copy(target, result, size);
        }

        IncrementSlot(target, operation);

        if (!operation.IsPostfix && result is not null)
        {
            Copy(target, result, size);
        }
    }

    private void IncrementSlot(Slot target, IIncrementOrDecrementOperation operation)
    {
        for (var index = 0; index < SizeOf(operation.Type); index++)
        {
            _code.Append("        LD A,(").Append(target.Name).Append('+').Append(index).AppendLine(")");
            if (index == 0)
                _code.AppendLine(operation.Kind == OperationKind.Increment ? "        ADD A,1" : "        SUB 1");
            else
                _code.AppendLine(operation.Kind == OperationKind.Increment ? "        ADC A,0" : "        SBC A,0");
            _code.Append("        LD (").Append(target.Name).Append('+').Append(index).AppendLine("),A");
        }
    }

    private void EmitInvocation(IInvocationOperation invocation, Slot? result)
    {
        var target = invocation.TargetMethod.OriginalDefinition;
        if (CompilerPipeline.IsSamIntrinsic(target, _compilation) &&
            (TryEmitConstantIntrinsic(invocation) || TryEmitBytePrintChar(invocation)))
        {
            return;
        }

        if (!CompilerPipeline.IsSamIntrinsic(target, _compilation) &&
            _methodCallLabels.TryGetValue(target, out var directLabel) &&
            invocation.Arguments.All(argument => !ContainsInvocation(argument.Value)))
        {
            foreach (var argument in invocation.Arguments.OrderBy(argument => argument.Syntax.SpanStart))
            {
                if (argument.Parameter is null ||
                    !_variableSlots.TryGetValue(argument.Parameter, out var parameterSlot))
                {
                    continue;
                }

                var constant = _model.GetConstantValue(argument.Value.Syntax);
                if (constant.HasValue && IsSupportedConstantType(argument.Parameter.Type))
                {
                    EmitConstant(parameterSlot, argument.Parameter.Type, constant.Value);
                    continue;
                }

                if (argument.Value is IConversionOperation conversion &&
                    TryEmitNarrowCast(conversion, parameterSlot))
                {
                    continue;
                }

                var value = EmitValue(argument.Value);
                CopyConverted(value, value.Type, parameterSlot, argument.Parameter.Type);
            }

            _code.Append("        CALL ").AppendLine(directLabel);
            if (!target.ReturnsVoid && result is not null)
            {
                Copy(_returnSlot, result, SizeOf(target.ReturnType));
            }

            return;
        }

        var argumentValues = new Dictionary<int, Slot>();
        foreach (var argument in invocation.Arguments.OrderBy(argument => argument.Syntax.SpanStart))
        {
            var value = EmitValue(argument.Value);
            if (argument.Parameter is not null)
            {
                var converted = NewSlot(argument.Parameter.Type, "arg");
                CopyConverted(value, value.Type, converted, argument.Parameter.Type);
                value = converted;
            }

            argumentValues[argument.Parameter?.Ordinal ?? argumentValues.Count] = value;
        }

        if (CompilerPipeline.IsSamIntrinsic(target, _compilation))
        {
            EmitIntrinsic(target, argumentValues, invocation.Syntax, result);
            return;
        }

        if (!_methodCallLabels.TryGetValue(target, out var label))
        {
            Unsupported(invocation.Syntax, $"Call target '{target.ToDisplayString()}' is not available in the compiled source set.");
            if (result is not null)
            {
                EmitConstant(result, invocation.Type ?? _compilation.GetSpecialType(SpecialType.System_Int32), 0);
            }
            return;
        }

        foreach (var parameter in target.Parameters)
        {
            if (argumentValues.TryGetValue(parameter.Ordinal, out var value) &&
                _variableSlots.TryGetValue(parameter, out var parameterSlot))
            {
                CopyConverted(value, value.Type, parameterSlot, parameter.Type);
            }
        }

        _code.Append("        CALL ").AppendLine(label);
        if (!target.ReturnsVoid && result is not null)
        {
            Copy(_returnSlot, result, SizeOf(target.ReturnType));
        }
    }

    private static bool ContainsInvocation(IOperation operation)
    {
        var pending = new Stack<IOperation>();
        pending.Push(operation);
        while (pending.TryPop(out var current))
        {
            if (current is IInvocationOperation)
            {
                return true;
            }

            foreach (var child in current.ChildOperations)
            {
                pending.Push(child);
            }
        }

        return false;
    }

    private bool TryEmitConstantIntrinsic(IInvocationOperation invocation)
    {
        if (invocation.Arguments.Any(argument => !argument.Value.ConstantValue.HasValue))
        {
            return false;
        }

        var arguments = invocation.Arguments.ToDictionary(
            argument => argument.Parameter!.Ordinal,
            argument => argument.Value.ConstantValue.Value);
        var method = invocation.TargetMethod;
        var isRom = CompilerPipeline.IsSamRomIntrinsic(method, _compilation);
        switch (method.Name)
        {
            case "ClearScreen":
                _code.AppendLine(isRom ? "        CALL sam_rom_clear_screen" : "        CALL sam_clear_screen");
                return true;
            case "WaitForKey":
                _code.AppendLine(isRom ? "        CALL sam_rom_wait_for_key" : "        CALL sam_wait_for_key");
                return true;
            case "WriteScreenByte" when !isRom:
                _code.Append("        LD HL,").Append(ToUInt32(arguments[0])).AppendLine();
                _code.Append("        LD A,").Append(ToUInt32(arguments[1])).AppendLine();
                _code.AppendLine("        CALL sam_write_screen_byte");
                return true;
            case "ClearSprite" when !isRom:
                _code.Append("        LD HL,").Append(ToUInt32(arguments[0])).AppendLine();
                _code.Append("        LD B,").Append(ToUInt32(arguments[1])).AppendLine();
                _code.Append("        LD C,").Append(ToUInt32(arguments[2])).AppendLine();
                _code.AppendLine("        CALL sam_clear_sprite");
                return true;
            case "Print" when arguments[0] is string text && text.All(character => character <= 0x7f):
                _code.Append("        LD HL,").AppendLine(InternString(text));
                _code.AppendLine("        CALL sam_print");
                return true;
            case "PrintChar" when ToUInt32(arguments[0]) < 128:
                _code.Append("        LD HL,").Append(ToUInt32(arguments[0])).AppendLine();
                _code.AppendLine("        CALL sam_print_char");
                return true;
            default:
                return false;
        }
    }

    private bool TryEmitBytePrintChar(IInvocationOperation invocation)
    {
        if (invocation.TargetMethod.Name != "PrintChar" ||
            invocation.Arguments is not [{ Value: IConversionOperation { Type.SpecialType: SpecialType.System_Char } conversion }])
        {
            return false;
        }

        Slot? source = conversion.Operand switch
        {
            ILocalReferenceOperation { Type.SpecialType: SpecialType.System_Byte } local => GetVariableSlot(local.Local),
            IParameterReferenceOperation { Type.SpecialType: SpecialType.System_Byte } parameter => GetVariableSlot(parameter.Parameter),
            _ => null
        };
        if (source is null)
        {
            return false;
        }

        _code.Append("        LD A,(").Append(source.Name).AppendLine(")");
        _code.AppendLine("        CP 128");
        _code.AppendLine("        JP NC,cs_trap_non_ascii");
        _code.AppendLine("        LD L,A");
        _code.AppendLine("        LD H,0");
        _code.AppendLine("        CALL sam_print_char");
        return true;
    }

    private void EmitIntrinsic(IMethodSymbol method, Dictionary<int, Slot> arguments, SyntaxNode syntax, Slot? result)
    {
        var isRom = CompilerPipeline.IsSamRomIntrinsic(method, _compilation);
        var packedRowInvocation = _model.GetOperation(syntax) as IInvocationOperation;
        var packedRowCount = packedRowInvocation is not null
            ? packedRowInvocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 3)?.Value.ConstantValue
            : default;
        var usesTenPixelScan = packedRowCount is { HasValue: true } &&
            packedRowCount.Value is { HasValue: true } &&
            packedRowCount.Value.Value is byte byteCount && byteCount == 10;
        IOperation? packedMaskOperation = packedRowInvocation?.Arguments
            .FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value;
        while (packedMaskOperation is IConversionOperation { IsImplicit: true } maskConversion)
        {
            packedMaskOperation = maskConversion.Operand;
        }
        var usesScreenSizedMask = usesTenPixelScan &&
            packedMaskOperation is IFieldReferenceOperation { Field.IsReadOnly: true } maskField &&
            _dataFields.TryGetValue(maskField.Field.OriginalDefinition, out var maskData) &&
            maskData.ElementSize == 1 &&
            maskData.Length == 0x6000;
        switch (method.Name)
        {
            case "ClearScreen":
                _code.AppendLine(isRom ? "        CALL sam_rom_clear_screen" : "        CALL sam_clear_screen");
                break;
            case "WaitForKey":
                _code.AppendLine(isRom ? "        CALL sam_rom_wait_for_key" : "        CALL sam_wait_for_key");
                break;
            case "WriteScreenByte":
                if (isRom)
                {
                    Unsupported(syntax, "Sam.Rom does not expose screen-memory writes.");
                    break;
                }

                if (arguments.TryGetValue(0, out var offset) && arguments.TryGetValue(1, out var pixel))
                {
                    _code.Append("        LD HL,(").Append(offset.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(pixel.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_write_screen_byte");
                }

                break;
            case "Print":
                if (arguments.TryGetValue(0, out var text))
                {
                    EmitNullCheck(text, NullStringFault);
                    _code.Append("        LD HL,(").Append(text.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_print");
                }

                break;
            case "PrintChar":
                if (arguments.TryGetValue(0, out var character))
                {
                    _code.Append("        LD A,(").Append(character.Name).AppendLine("+1)");
                    _code.AppendLine("        OR A");
                    _code.AppendLine("        JP NZ,cs_trap_non_ascii");
                    _code.Append("        LD A,(").Append(character.Name).AppendLine(")");
                    _code.AppendLine("        CP 128");
                    _code.AppendLine("        JP NC,cs_trap_non_ascii");
                    _code.Append("        LD HL,(").Append(character.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_print_char");
                }

                break;
            case "DrawOpaqueSprite":
                if (arguments.TryGetValue(0, out var opaqueData) &&
                    arguments.TryGetValue(1, out var opaqueOffset))
                {
                    _code.Append("        LD HL,(").Append(opaqueOffset.Name).AppendLine(")");
                    _code.Append("        LD DE,(").Append(opaqueData.Name).AppendLine(")");
                    _code.AppendLine("        INC DE");
                    _code.AppendLine("        INC DE");
                    _code.AppendLine("        CALL sam_draw_opaque_sprite");
                }

                break;
            case "BuildInterleavedSprite":
                if (arguments.TryGetValue(0, out var pixels) &&
                    arguments.TryGetValue(1, out var masks))
                {
                    _code.Append("        LD HL,(").Append(pixels.Name).AppendLine(")");
                    _code.AppendLine("        INC HL");
                    _code.AppendLine("        INC HL");
                    _code.Append("        LD DE,(").Append(masks.Name).AppendLine(")");
                    _code.AppendLine("        INC DE");
                    _code.AppendLine("        INC DE");
                    _code.AppendLine("        CALL sam_build_interleaved_sprite");
                }

                break;
            case "SelectSpriteFrame":
                if (arguments.TryGetValue(0, out var frameData) &&
                    arguments.TryGetValue(1, out var frameIndex) &&
                    result is not null)
                {
                    Copy(frameData, result, 2);
                    _code.Append("        LD HL,(").Append(result.Name).AppendLine(")");
                    _code.AppendLine("        INC HL");
                    _code.AppendLine("        INC HL");
                    _code.Append("        LD (").Append(result.Name).AppendLine("),HL");
                    _code.Append("        LD A,(").Append(frameIndex.Name).AppendLine(")");
                    _code.Append("        LD (").Append(result.Name).AppendLine("+2),A");
                }

                break;
            case "BeginDoubleBuffer":
                if (arguments.Count == 0)
                {
                    _code.AppendLine("        CALL sam_begin_double_buffer");
                }
                else if (arguments.TryGetValue(0, out var firstScreenPage) &&
                         arguments.TryGetValue(1, out var secondScreenPage))
                {
                    _code.Append("        LD A,(").Append(secondScreenPage.Name).AppendLine(")");
                    _code.AppendLine("        LD B,A");
                    _code.Append("        LD A,(").Append(firstScreenPage.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_begin_double_buffer_pages");
                }

                break;
            case "LoadTutorialPalette":
                if (arguments.TryGetValue(0, out var palette))
                {
                    _code.Append("        LD HL,(").Append(palette.Name).AppendLine(")");
                    _code.AppendLine("        INC HL");
                    _code.AppendLine("        INC HL");
                    _code.AppendLine("        CALL sam_load_tutorial_palette");
                }

                break;
            case "DrawBuiltMaskedSprite":
            case "ClearSprite":
                if (arguments.TryGetValue(0, out var builtOffset) &&
                    arguments.TryGetValue(1, out var builtWidth) &&
                    arguments.TryGetValue(2, out var builtHeight))
                {
                    _code.Append("        LD HL,(").Append(builtOffset.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(builtWidth.Name).AppendLine(")");
                    _code.AppendLine("        LD B,A");
                    _code.Append("        LD A,(").Append(builtHeight.Name).AppendLine(")");
                    _code.AppendLine("        LD C,A");
                    _code.AppendLine(method.Name == "ClearSprite"
                        ? "        CALL sam_clear_sprite"
                        : "        CALL sam_draw_built_masked_sprite");
                }

                break;
            case "DrawMaskedSprite":
            case "DrawRleMaskedSprite":
                if (arguments.TryGetValue(0, out var selectedFrame) &&
                    arguments.TryGetValue(1, out var spriteOffset) &&
                    arguments.TryGetValue(2, out var spriteWidth) &&
                    arguments.TryGetValue(3, out var spriteHeight))
                {
                    _code.Append("        LD HL,(").Append(spriteOffset.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(selectedFrame.Name).AppendLine(")");
                    _code.AppendLine("        LD E,A");
                    _code.Append("        LD A,(").Append(selectedFrame.Name).AppendLine("+1)");
                    _code.AppendLine("        LD D,A");
                    _code.Append("        LD A,(").Append(spriteWidth.Name).AppendLine(")");
                    _code.AppendLine("        LD B,A");
                    _code.Append("        LD A,(").Append(spriteHeight.Name).AppendLine(")");
                    _code.AppendLine("        LD C,A");
                    _code.Append("        LD A,(").Append(selectedFrame.Name).AppendLine("+2)");
                    _code.AppendLine(method.Name == "DrawMaskedSprite"
                        ? "        CALL sam_draw_masked_sprite"
                        : "        CALL sam_draw_rle_masked_sprite");
                }

                break;
            case "FillScreen":
            case "WaitForFrames":
                if (arguments.TryGetValue(0, out var singleByte))
                {
                    _code.Append("        LD A,(").Append(singleByte.Name).AppendLine(")");
                    _code.AppendLine(method.Name == "FillScreen"
                        ? "        CALL sam_fill_screen"
                        : "        CALL sam_wait_frames");
                }

                break;
            case "FillStripedBackground":
                if (arguments.TryGetValue(0, out var stripeFirst) &&
                    arguments.TryGetValue(1, out var stripeSecond))
                {
                    _code.Append("        LD A,(").Append(stripeFirst.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(stripeSecond.Name).AppendLine(")");
                    _code.AppendLine("        LD B,A");
                    _code.Append("        LD A,(").Append(stripeFirst.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_fill_striped_background");
                }

                break;
            case "SetPaletteEntry":
                if (arguments.TryGetValue(0, out var paletteIndex) &&
                    arguments.TryGetValue(1, out var paletteValue))
                {
                    _code.Append("        LD A,(").Append(paletteIndex.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(paletteValue.Name).AppendLine(")");
                    _code.AppendLine("        LD B,A");
                    _code.Append("        LD A,(").Append(paletteIndex.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_set_palette_entry");
                }

                break;
            case "IsKeyPressed":
                if (arguments.TryGetValue(0, out var keyPort) &&
                    arguments.TryGetValue(1, out var keyMask))
                {
                    _code.Append("        LD BC,(").Append(keyPort.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(keyMask.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_is_key_pressed");
                    if (result is not null)
                    {
                        _usesReturnSlot = true;
                        _code.Append("        LD (").Append(_returnSlot).AppendLine("),A");
                        Copy(_returnSlot, result, 1);
                    }
                }

                break;
            case "AnySolidPixelInPackedRow":
                if (arguments.TryGetValue(0, out var packedMask) &&
                    arguments.TryGetValue(1, out var packedOffset) &&
                    arguments.TryGetValue(2, out var firstPixel) &&
                    arguments.TryGetValue(3, out var pixelCount) &&
                    result is not null)
                {
                    _code.Append("        LD HL,(").Append(packedMask.Name).AppendLine(")");
                    _code.Append("        LD DE,(").Append(packedOffset.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(firstPixel.Name).AppendLine(")");
                    _code.AppendLine("        LD B,A");
                    _code.Append("        LD A,(").Append(pixelCount.Name).AppendLine(")");
                    _code.AppendLine("        LD C,A");
                    var helper = usesScreenSizedMask
                        ? "sam_any_solid_ten_pixels_screen"
                        : usesTenPixelScan
                            ? "sam_any_solid_ten_pixels"
                            : "sam_any_solid_pixel_row";
                    _code.Append("        CALL ").AppendLine(helper);
                    _code.Append("        LD (").Append(result.Name).AppendLine("),A");
                }

                break;
            case "AnySolidPixelInPackedScreenColumn":
                if (arguments.TryGetValue(0, out var packedColumnMask) &&
                    arguments.TryGetValue(1, out var packedColumnX) &&
                    arguments.TryGetValue(2, out var packedColumnY) &&
                    arguments.TryGetValue(3, out var packedColumnCount) &&
                    result is not null)
                {
                    _code.Append("        LD HL,(").Append(packedColumnMask.Name).AppendLine(")");
                    _code.Append("        LD A,(").Append(packedColumnX.Name).AppendLine(")");
                    _code.AppendLine("        LD B,A");
                    _code.Append("        LD A,(").Append(packedColumnY.Name).AppendLine(")");
                    _code.AppendLine("        LD D,A");
                    _code.Append("        LD A,(").Append(packedColumnCount.Name).AppendLine(")");
                    _code.AppendLine("        LD C,A");
                    _code.AppendLine("        CALL sam_any_solid_pixel_column");
                    _code.Append("        LD (").Append(result.Name).AppendLine("),A");
                }

                break;
            case "AnySolidPixelInPackedRectangle":
                if (arguments.TryGetValue(0, out var rectangleMask) &&
                    arguments.TryGetValue(1, out var rectangleOffset) &&
                    arguments.TryGetValue(2, out var rectangleParity) &&
                    arguments.TryGetValue(3, out var rectangleWidth) &&
                    arguments.TryGetValue(4, out var rectangleRows) &&
                    result is not null)
                {
                    _code.Append("        LD HL,(").Append(rectangleMask.Name).AppendLine(")");
                    _code.AppendLine("        LD (sam_packed_rectangle_array),HL");
                    _code.Append("        LD HL,(").Append(rectangleOffset.Name).AppendLine(")");
                    _code.AppendLine("        LD (sam_packed_rectangle_offset),HL");
                    _code.Append("        LD A,(").Append(rectangleParity.Name).AppendLine(")");
                    _code.AppendLine("        LD (sam_packed_rectangle_parity),A");
                    _code.Append("        LD A,(").Append(rectangleWidth.Name).AppendLine(")");
                    _code.AppendLine("        LD (sam_packed_rectangle_width),A");
                    _code.Append("        LD A,(").Append(rectangleRows.Name).AppendLine(")");
                    _code.AppendLine("        LD (sam_packed_rectangle_rows),A");
                    _code.AppendLine("        CALL sam_any_solid_pixel_rectangle");
                    _code.Append("        LD (").Append(result.Name).AppendLine("),A");
                }

                break;
            case "FlipScreen":
                _code.AppendLine("        CALL sam_flip_screen");
                break;
            case "MapMemoryPage":
                if (arguments.TryGetValue(0, out var bankPage))
                {
                    _code.Append("        LD A,(").Append(bankPage.Name).AppendLine(")");
                    _code.AppendLine("        CALL sam_map_memory_page");
                }

                break;
            case "RestoreMemoryPage":
                _code.AppendLine("        CALL sam_restore_memory_page");
                break;
            case "CopyBytes":
                if (arguments.TryGetValue(0, out var copySource) &&
                    arguments.TryGetValue(1, out var copySourceOffset) &&
                    arguments.TryGetValue(2, out var copyDestination) &&
                    arguments.TryGetValue(3, out var copyDestinationOffset) &&
                    arguments.TryGetValue(4, out var copyCount))
                {
                    EmitStoreWordArgument(copySource, "sam_copy_source");
                    EmitStoreWordArgument(copySourceOffset, "sam_copy_source_offset");
                    EmitStoreWordArgument(copyDestination, "sam_copy_destination");
                    EmitStoreWordArgument(copyDestinationOffset, "sam_copy_destination_offset");
                    EmitStoreWordArgument(copyCount, "sam_copy_count");
                    _code.AppendLine("        CALL sam_copy_bytes");
                }

                break;
            case "DecodeRleBackground":
            case "ExpandByteRuns":
                if (arguments.TryGetValue(0, out var decodeSource) &&
                    arguments.TryGetValue(1, out var decodeSourcePage) &&
                    arguments.TryGetValue(2, out var decodeDestination) &&
                    arguments.TryGetValue(3, out var decodeDestinationPage))
                {
                    EmitStoreWordArgument(decodeSource, "sam_decode_source");
                    EmitStoreByteArgument(decodeSourcePage, "sam_decode_source_page");
                    EmitStoreWordArgument(decodeDestination, "sam_decode_destination");
                    EmitStoreByteArgument(decodeDestinationPage, "sam_decode_destination_page");
                    _code.Append("        CALL ")
                        .Append(method.Name == "DecodeRleBackground"
                            ? "sam_decode_rle_background"
                            : "sam_expand_byte_runs")
                        .AppendLine();
                }

                break;
            case "ShiftMaskedSpriteOnePixel":
                if (arguments.TryGetValue(0, out var shiftSource) &&
                    arguments.TryGetValue(1, out var shiftDestination))
                {
                    EmitStoreWordArgument(shiftSource, "sam_shift_source");
                    EmitStoreWordArgument(shiftDestination, "sam_shift_destination");
                    _code.AppendLine("        CALL sam_shift_masked_sprite_one_pixel");
                }

                break;
            case "CopyBytesToScreen":
                if (arguments.TryGetValue(0, out var screenSource) &&
                    arguments.TryGetValue(1, out var screenSourceOffset) &&
                    arguments.TryGetValue(2, out var screenOffset) &&
                    arguments.TryGetValue(3, out var screenCount))
                {
                    EmitStoreWordArgument(screenSource, "sam_screen_source");
                    EmitStoreWordArgument(screenSourceOffset, "sam_screen_source_offset");
                    EmitStoreWordArgument(screenOffset, "sam_screen_offset");
                    EmitStoreWordArgument(screenCount, "sam_screen_count");
                    _code.AppendLine("        CALL sam_copy_bytes_to_screen");
                }

                break;
            case "CopyBankedBytesToScreen":
                if (arguments.TryGetValue(0, out var bankedScreenSource) &&
                    arguments.TryGetValue(1, out var bankedScreenPage) &&
                    arguments.TryGetValue(2, out var bankedScreenSourceOffset) &&
                    arguments.TryGetValue(3, out var bankedScreenOffset) &&
                    arguments.TryGetValue(4, out var bankedScreenWidth) &&
                    arguments.TryGetValue(5, out var bankedScreenRows))
                {
                    EmitStoreWordArgument(bankedScreenSource, "sam_banked_screen_source");
                    EmitStoreByteArgument(bankedScreenPage, "sam_banked_screen_page");
                    EmitStoreWordArgument(bankedScreenSourceOffset, "sam_banked_screen_source_offset");
                    EmitStoreWordArgument(bankedScreenOffset, "sam_banked_screen_offset");
                    EmitStoreByteArgument(bankedScreenWidth, "sam_banked_screen_width");
                    EmitStoreByteArgument(bankedScreenRows, "sam_banked_screen_rows");
                    _code.AppendLine("        CALL sam_copy_banked_rows_to_screen");
                }

                break;
            case "ComposeTransparentImage":
                if (arguments.TryGetValue(0, out var composeSource) &&
                    arguments.TryGetValue(1, out var composeSourcePage) &&
                    arguments.TryGetValue(2, out var composeDestination) &&
                    arguments.TryGetValue(3, out var composeDestinationPage) &&
                    arguments.TryGetValue(4, out var composeSourceOffset) &&
                    arguments.TryGetValue(5, out var composeDestinationOffset) &&
                    arguments.TryGetValue(6, out var composeWidth) &&
                    arguments.TryGetValue(7, out var composeRows) &&
                    arguments.TryGetValue(8, out var composeStride))
                {
                    EmitStoreWordArgument(composeSource, "sam_compose_source");
                    EmitStoreByteArgument(composeSourcePage, "sam_compose_source_page");
                    EmitStoreWordArgument(composeDestination, "sam_compose_destination");
                    EmitStoreByteArgument(composeDestinationPage, "sam_compose_destination_page");
                    EmitStoreWordArgument(composeSourceOffset, "sam_compose_source_offset");
                    EmitStoreWordArgument(composeDestinationOffset, "sam_compose_destination_offset");
                    EmitStoreByteArgument(composeWidth, "sam_compose_width");
                    EmitStoreByteArgument(composeRows, "sam_compose_rows");
                    EmitStoreByteArgument(composeStride, "sam_compose_source_stride");
                    _code.AppendLine("        CALL sam_compose_transparent_image");
                }

                break;
            case "DrawPackedOutline":
                if (arguments.TryGetValue(0, out var outlineDestination) &&
                    arguments.TryGetValue(1, out var outlinePage) &&
                    arguments.TryGetValue(2, out var outlineOffset) &&
                    arguments.TryGetValue(3, out var outlineWidth) &&
                    arguments.TryGetValue(4, out var outlineHeight) &&
                    arguments.TryGetValue(5, out var outlineTopBottom) &&
                    arguments.TryGetValue(6, out var outlineLeft) &&
                    arguments.TryGetValue(7, out var outlineRight))
                {
                    EmitStoreWordArgument(outlineDestination, "sam_outline_destination");
                    EmitStoreByteArgument(outlinePage, "sam_outline_page");
                    EmitStoreWordArgument(outlineOffset, "sam_outline_offset");
                    EmitStoreByteArgument(outlineWidth, "sam_outline_width");
                    EmitStoreByteArgument(outlineHeight, "sam_outline_height");
                    EmitStoreByteArgument(outlineTopBottom, "sam_outline_top_bottom");
                    EmitStoreByteArgument(outlineLeft, "sam_outline_left");
                    EmitStoreByteArgument(outlineRight, "sam_outline_right");
                    _code.AppendLine("        CALL sam_draw_packed_outline");
                }

                break;
            case "SetBorderColor":
                if (arguments.TryGetValue(0, out var borderIndex))
                {
                    _code.Append("        LD A,(").Append(borderIndex.Name).AppendLine(")");
                    _code.AppendLine("        OUT (254),A");
                }

                break;
            case "WriteSaaRegister":
                if (arguments.TryGetValue(0, out var saaRegister) &&
                    arguments.TryGetValue(1, out var saaValue))
                {
                    // OUT (C),A uses all 16 bits of BC on the SAM. The SAA1099
                    // selects with 0x01FF, then accepts data at 0x00FF.
                    _code.Append("        LD A,(").Append(saaRegister.Name).AppendLine(")");
                    _code.AppendLine("        LD BC,0x01FF");
                    _code.AppendLine("        OUT (C),A");
                    _code.Append("        LD A,(").Append(saaValue.Name).AppendLine(")");
                    _code.AppendLine("        LD B,0");
                    _code.AppendLine("        OUT (C),A");
                }

                break;
            default:
                Unsupported(syntax, $"SAM SDK intrinsic '{method.Name}' is not supported.");
                break;
        }

    }

    private void EmitStoreWordArgument(Slot argument, string destination)
    {
        _code.Append("        LD HL,(").Append(argument.Name).AppendLine(")");
        _code.Append("        LD (").Append(destination).AppendLine("),HL");
    }

    private void EmitStoreByteArgument(Slot argument, string destination)
    {
        _code.Append("        LD A,(").Append(argument.Name).AppendLine(")");
        _code.Append("        LD (").Append(destination).AppendLine("),A");
    }

    private void EmitProperty(IPropertyReferenceOperation property, Slot result)
    {
        if (property.Instance?.Type is IArrayTypeSymbol &&
            property.Property.Name == "Length" && property.Instance is not null)
        {
            var array = EmitValue(property.Instance);
            _code.Append("        LD HL,(").Append(array.Name).AppendLine(")");
            _code.AppendLine("        LD A,(HL)");
            _code.Append("        LD (").Append(result.Name).AppendLine("),A");
            _code.AppendLine("        INC HL");
            _code.AppendLine("        LD A,(HL)");
            _code.Append("        LD (").Append(result.Name).AppendLine("+1),A");
            _code.AppendLine("        XOR A");
            _code.Append("        LD (").Append(result.Name).AppendLine("+2),A");
            _code.Append("        LD (").Append(result.Name).AppendLine("+3),A");
            return;
        }

        if (property.Property.ContainingType.SpecialType != SpecialType.System_String)
        {
            Unsupported(property.Syntax, $"Property '{property.Property.Name}' is not supported.");
            EmitConstant(result, property.Type!, 0);
            return;
        }

        if (property.Property.Name == "Length" && property.Arguments.Length == 0 && property.Instance is not null)
        {
            var text = EmitValue(property.Instance);
            EmitNullCheck(text, NullStringFault);
            _code.Append("        LD HL,(").Append(text.Name).AppendLine(")");
            _code.AppendLine("        LD A,(HL)");
            _code.Append("        LD (").Append(result.Name).AppendLine("),A");
            _code.AppendLine("        INC HL");
            _code.AppendLine("        LD A,(HL)");
            _code.Append("        LD (").Append(result.Name).AppendLine("+1),A");
            _code.AppendLine("        XOR A");
            _code.Append("        LD (").Append(result.Name).AppendLine("+2),A");
            _code.Append("        LD (").Append(result.Name).AppendLine("+3),A");
            return;
        }

        if (property.Property.IsIndexer && property.Arguments.Length == 1 && property.Instance is not null)
        {
            EmitStringIndex(property, result);
            return;
        }

        Unsupported(property.Syntax, $"String member '{property.Property.Name}' is not supported.");
        EmitConstant(result, property.Type!, 0);
    }

    private void EmitStringIndex(IPropertyReferenceOperation property, Slot result)
    {
        var text = EmitValue(property.Instance!);
        var index = EmitValue(property.Arguments[0].Value);
        EmitNullCheck(text, NullStringFault);
        var highIndex = SizeOf(property.Arguments[0].Value.Type) - 1;
        if (IsSigned(property.Arguments[0].Value.Type!))
        {
            _code.Append("        LD A,(").Append(index.Name).Append('+').Append(highIndex).AppendLine(")");
            _code.AppendLine("        BIT 7,A");
            _code.AppendLine("        JP NZ,cs_trap_string_bounds");
        }

        for (var byteIndex = 2; byteIndex < SizeOf(property.Arguments[0].Value.Type); byteIndex++)
        {
            _code.Append("        LD A,(").Append(index.Name).Append('+').Append(byteIndex).AppendLine(")");
            _code.AppendLine("        OR A");
            _code.AppendLine("        JP NZ,cs_trap_string_bounds");
        }

        var valid = NewLabel("string_index_valid");
        _code.Append("        LD HL,(").Append(text.Name).AppendLine(")");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD C,A");
        _code.Append("        LD A,(").Append(index.Name).AppendLine("+1)");
        _code.AppendLine("        CP C");
        _code.Append("        JP C,").AppendLine(valid);
        _code.AppendLine("        JP NZ,cs_trap_string_bounds");
        _code.Append("        LD HL,(").Append(text.Name).AppendLine(")");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD C,A");
        _code.Append("        LD A,(").Append(index.Name).AppendLine(")");
        _code.AppendLine("        CP C");
        _code.Append("        JP C,").AppendLine(valid);
        _code.AppendLine("        JP cs_trap_string_bounds");
        _code.Append(valid).AppendLine(":");
        _code.Append("        LD HL,(").Append(text.Name).AppendLine(")");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        INC HL");
        _code.Append("        LD A,(").Append(index.Name).AppendLine(")");
        _code.AppendLine("        LD E,A");
        _code.Append("        LD A,(").Append(index.Name).AppendLine("+1)");
        _code.AppendLine("        LD D,A");
        _code.AppendLine("        ADD HL,DE");
        _code.AppendLine("        LD A,(HL)");
        _code.Append("        LD (").Append(result.Name).AppendLine("),A");
        _code.AppendLine("        XOR A");
        _code.Append("        LD (").Append(result.Name).AppendLine("+1),A");
    }

    private Slot EmitArrayAddress(IArrayElementReferenceOperation element)
    {
        IOperation arrayReference = element.ArrayReference;
        while (arrayReference is IConversionOperation { IsImplicit: true } arrayConversion)
        {
            arrayReference = arrayConversion.Operand;
        }

        while (arrayReference is IParenthesizedOperation parenthesized)
        {
            arrayReference = parenthesized.Operand;
        }

        string? bankedArrayLabel = arrayReference is IFieldReferenceOperation field &&
            _dataFields.TryGetValue(field.Field.OriginalDefinition, out var data)
            ? data.Label
            : null;
        bool bankedArray = bankedArrayLabel is not null;
        var array = bankedArray ? null : EmitValue(element.ArrayReference);
        IOperation indexOperation = element.Indices[0];
        while (indexOperation is IConversionOperation { IsImplicit: true } conversion)
        {
            indexOperation = conversion.Operand;
        }
        var index = EmitValue(indexOperation);
        if (indexOperation.Type?.SpecialType == SpecialType.System_Byte &&
            element.Type?.SpecialType == SpecialType.System_Byte)
        {
            _helpers.Add("array_byte_index8");
            if (bankedArray)
                _code.Append("        LD HL,").AppendLine(bankedArrayLabel);
            else
                _code.Append("        LD HL,(").Append(array!.Name).AppendLine(")");
            _code.Append("        LD A,(").Append(index.Name).AppendLine(")");
            _code.AppendLine("        CALL cs_array_byte_index8_address");
            var byteAddress = NewSlot(_compilation.GetSpecialType(SpecialType.System_UInt16), "array_address");
            Copy("cs_array_address", byteAddress, 2);
            return byteAddress;
        }

        if (bankedArray)
            _code.Append("        LD HL,").AppendLine(bankedArrayLabel);
        else
            _code.Append("        LD HL,(").Append(array!.Name).AppendLine(")");
        _code.AppendLine("        LD (cs_array_pointer),HL");
        var indexSize = SizeOf(indexOperation.Type!);
        if (indexSize < 4)
        {
            _code.AppendLine("        XOR A");
            for (int position = indexSize; position < 4; position++)
            {
                _code.Append("        LD (cs_array_index+").Append(position).AppendLine("),A");
            }
        }
        Copy(index, "cs_array_index", indexSize);
        var wordElement = SizeOf(element.Type) == 2;
        var helper = wordElement ? "array_word" : "array_byte";
        _helpers.Add(helper);
        _code.Append("        CALL cs_").Append(helper).AppendLine("_address");
        var address = NewSlot(_compilation.GetSpecialType(SpecialType.System_UInt16), "array_address");
        Copy("cs_array_address", address, 2);
        return address;
    }

    private void EmitArrayElementRead(ITypeSymbol type, Slot address, Slot result)
    {
        _code.Append("        LD HL,(").Append(address.Name).AppendLine(")");
        _code.AppendLine("        LD A,(HL)");
        _code.Append("        LD (").Append(result.Name).AppendLine("),A");
        if (SizeOf(type) == 2)
        {
            _code.AppendLine("        INC HL");
            _code.AppendLine("        LD A,(HL)");
            _code.Append("        LD (").Append(result.Name).AppendLine("+1),A");
        }
    }

    private void EmitArrayElementWrite(ITypeSymbol type, Slot address, Slot value)
    {
        _code.Append("        LD HL,(").Append(address.Name).AppendLine(")");
        _code.Append("        LD A,(").Append(value.Name).AppendLine(")");
        _code.AppendLine("        LD (HL),A");
        if (SizeOf(type) == 2)
        {
            _code.AppendLine("        INC HL");
            _code.Append("        LD A,(").Append(value.Name).AppendLine("+1)");
            _code.AppendLine("        LD (HL),A");
        }
    }

    private void EmitArrayAddressHelper(string helper, bool wordElement)
    {
        var prefix = "cs_" + helper + "_address";
        var valid = prefix + "_valid";
        var bounds = prefix + "_bounds";
        _code.AppendLine();
        _code.Append(prefix).AppendLine(":");
        _code.AppendLine("        LD HL,(cs_array_pointer)");
        _code.AppendLine("        LD A,H");
        _code.AppendLine("        OR L");
        _code.Append("        JP Z,").Append(prefix).AppendLine("_null");
        _code.AppendLine("        LD A,(cs_array_index+2)");
        _code.AppendLine("        OR A");
        _code.Append("        JP NZ,").AppendLine(bounds);
        _code.AppendLine("        LD A,(cs_array_index+3)");
        _code.AppendLine("        OR A");
        _code.Append("        JP NZ,").AppendLine(bounds);
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD (cs_array_length),A");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD (cs_array_length+1),A");
        _code.AppendLine("        LD A,(cs_array_index+1)");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,(cs_array_length+1)");
        _code.AppendLine("        CP C");
        _code.Append("        JP C,").AppendLine(bounds);
        _code.Append("        JP NZ,").AppendLine(valid);
        _code.AppendLine("        LD A,(cs_array_index)");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,(cs_array_length)");
        _code.AppendLine("        CP C");
        _code.Append("        JP C,").AppendLine(bounds);
        _code.Append("        JP Z,").AppendLine(bounds);
        _code.Append(valid).AppendLine(":");
        _code.AppendLine("        LD HL,(cs_array_index)");
        if (wordElement)
            _code.AppendLine("        ADD HL,HL");
        _code.AppendLine("        LD DE,2");
        _code.AppendLine("        ADD HL,DE");
        _code.AppendLine("        LD DE,(cs_array_pointer)");
        _code.AppendLine("        ADD HL,DE");
        _code.AppendLine("        LD (cs_array_address),HL");
        _code.AppendLine("        RET");
        _code.Append(bounds).AppendLine(":");
        _code.AppendLine("        LD A,14");
        _code.AppendLine("        JP sam_trap");
        _code.Append(prefix).AppendLine("_null:");
        _code.AppendLine("        LD A,1");
        _code.AppendLine("        JP sam_trap");
    }

    private void EmitByteIndexArrayAddressHelper()
    {
        _code.AppendLine();
        _code.AppendLine("cs_array_byte_index8_address:");
        _code.AppendLine("        LD (cs_array_pointer),HL");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,H");
        _code.AppendLine("        OR L");
        _code.AppendLine("        JP Z,cs_array_byte_index8_null");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD E,A");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        OR A");
        _code.AppendLine("        JR NZ,cs_array_byte_index8_valid");
        _code.AppendLine("        LD A,E");
        _code.AppendLine("        CP C");
        _code.AppendLine("        JP C,cs_array_byte_index8_bounds");
        _code.AppendLine("        JP Z,cs_array_byte_index8_bounds");
        _code.AppendLine("cs_array_byte_index8_valid:");
        _code.AppendLine("        LD HL,(cs_array_pointer)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD E,C");
        _code.AppendLine("        LD D,0");
        _code.AppendLine("        ADD HL,DE");
        _code.AppendLine("        LD (cs_array_address),HL");
        _code.AppendLine("        RET");
        _code.AppendLine("cs_array_byte_index8_bounds:");
        _code.AppendLine("        LD A,14");
        _code.AppendLine("        JP sam_trap");
        _code.AppendLine("cs_array_byte_index8_null:");
        _code.AppendLine("        LD A,1");
        _code.AppendLine("        JP sam_trap");
    }

    private void EmitConstant(Slot destination, ITypeSymbol type, object? value)
    {
        var size = SizeOf(type);
        if (type.SpecialType == SpecialType.System_String)
        {
            if (value is not string text)
            {
                _code.Append("        LD HL,0").AppendLine();
                _code.Append("        LD (").Append(destination.Name).AppendLine("),HL");
                return;
            }

            if (text.Any(character => character > 0x7f))
            {
                Unsupported(_methodSyntax[_currentMethod].Identifier.Parent, "String literals must contain ASCII characters (U+0000 through U+007F).");
                _code.Append("        LD HL,0").AppendLine();
                _code.Append("        LD (").Append(destination.Name).AppendLine("),HL");
                return;
            }

            var label = InternString(text);
            _code.Append("        LD HL,").AppendLine(label);
            _code.Append("        LD (").Append(destination.Name).AppendLine("),HL");
            return;
        }

        var raw = ToUInt32(value);
        for (var index = 0; index < size; index++)
        {
            _code.Append("        LD A,").Append(((raw >> (8 * index)) & 0xff).ToString(CultureInfo.InvariantCulture)).AppendLine();
            _code.Append("        LD (").Append(destination.Name).Append('+').Append(index).AppendLine("),A");
        }
    }

    private void CopyConverted(Slot source, ITypeSymbol? sourceType, Slot destination, ITypeSymbol? destinationType)
    {
        if (sourceType is not null && destinationType is not null &&
            !SymbolEqualityComparer.Default.Equals(sourceType, destinationType))
        {
            Convert(source, sourceType, destination, destinationType);
        }
        else
        {
            Copy(source, destination, SizeOf(destinationType));
        }
    }

    private void CopyConverted(Slot source, ITypeSymbol? sourceType, string destination, ITypeSymbol? destinationType)
    {
        if (sourceType is not null && destinationType is not null &&
            !SymbolEqualityComparer.Default.Equals(sourceType, destinationType))
        {
            var converted = NewSlot(destinationType, "return_convert");
            Convert(source, sourceType, converted, destinationType);
            Copy(converted, destination, SizeOf(destinationType));
        }
        else
        {
            Copy(source, destination, SizeOf(destinationType));
        }
    }

    private void Convert(Slot source, ITypeSymbol sourceType, Slot destination, ITypeSymbol destinationType)
    {
        var sourceSize = SizeOf(sourceType);
        var destinationSize = SizeOf(destinationType);
        var copySize = Math.Min(sourceSize, destinationSize);
        Copy(source, destination, copySize);
        if (destinationSize <= sourceSize)
        {
            return;
        }

        if (IsSigned(sourceType))
        {
            _code.Append("        LD A,(").Append(source.Name).Append('+').Append(sourceSize - 1).AppendLine(")");
            _code.AppendLine("        ADD A,A");
            _code.AppendLine("        SBC A,A");
        }
        else
        {
            _code.AppendLine("        XOR A");
        }

        for (var index = sourceSize; index < destinationSize; index++)
        {
            _code.Append("        LD (").Append(destination.Name).Append('+').Append(index).AppendLine("),A");
        }
    }

    private void Negate(Slot source, Slot destination, int size)
    {
        for (var index = 0; index < size; index++)
        {
            _code.Append("        LD A,(").Append(source.Name).Append('+').Append(index).AppendLine(")");
            _code.AppendLine("        CPL");
            if (index == 0)
            {
                _code.AppendLine("        ADD A,1");
            }
            else
            {
                _code.AppendLine("        ADC A,0");
            }

            _code.Append("        LD (").Append(destination.Name).Append('+').Append(index).AppendLine("),A");
        }
    }

    private void PrepareMath(Slot left, Slot right)
    {
        Copy(left, "cs_math_lhs", 4);
        Copy(right, "cs_math_rhs", 4);
    }

    private void EmitDivisionByZeroGuard()
    {
        var nonzero = NewLabel("div_nonzero");
        _code.AppendLine("        LD A,(cs_math_rhs)");
        _code.AppendLine("        LD C,A");
        for (var index = 1; index < 4; index++)
        {
            _code.Append("        LD A,(cs_math_rhs+").Append(index).AppendLine(")");
            _code.AppendLine("        OR C");
            _code.AppendLine("        LD C,A");
        }

        _code.AppendLine("        LD A,C");
        _code.AppendLine("        OR A");
        _code.Append("        JP NZ,").AppendLine(nonzero);
        EmitTrap(DivideByZeroFault);
        _code.Append(nonzero).AppendLine(":");
    }

    private void EmitNullCheck(Slot text, byte fault)
    {
        var nonnull = NewLabel("string_nonnull");
        _code.Append("        LD A,(").Append(text.Name).AppendLine(")");
        _code.AppendLine("        LD C,A");
        _code.Append("        LD A,(").Append(text.Name).AppendLine("+1)");
        _code.AppendLine("        OR C");
        _code.Append("        JP NZ,").AppendLine(nonnull);
        EmitTrap(fault);
        _code.Append(nonnull).AppendLine(":");
    }

    private void EmitTrap(byte fault)
    {
        _code.Append("        LD A,").Append(fault).AppendLine();
        _code.AppendLine("        JP sam_trap");
    }

    private void EmitSequencePoint(SyntaxNode syntax)
    {
        var span = syntax.GetLocation().GetLineSpan();
        if (_options.EmitSourceComments)
        {
            string file = string.IsNullOrEmpty(span.Path) ? "<unknown>" : span.Path;
            file = file.Replace('\r', ' ').Replace('\n', ' ');
            string method = _currentMethod.Name.Replace('\r', ' ').Replace('\n', ' ');
            _code.Append("; ").Append(file)
                .Append(':').Append(span.StartLinePosition.Line + 1)
                .Append(" (").Append(method).AppendLine(")");
        }

        if (!_options.EmitSequencePoints)
            return;

        var label = $"cs_seq_{++_nextSequencePoint:D5}";
        _sourceMap.Add(new SequencePoint(
            label,
            span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1,
            _currentMethod.Name));
        _code.Append(label).AppendLine(":");
    }

    private void EmitHelpers()
    {
        var code = _code.ToString();
        _code.AppendLine();
        _code.AppendLine("cs_trap_string_bounds:");
        if (code.Contains("cs_trap_string_bounds", StringComparison.Ordinal))
            EmitTrap(StringBoundsFault);
        _code.AppendLine("cs_trap_non_ascii:");
        if (code.Contains("cs_trap_non_ascii", StringComparison.Ordinal))
            EmitTrap(NonAsciiFault);

        if (_helpers.Contains("mul32"))
        {
            EmitMultiplyHelper();
        }

        if (_helpers.Contains("udiv32") || _helpers.Contains("signed_div32"))
        {
            EmitUnsignedDivisionHelper();
        }

        if (_helpers.Contains("signed_div32"))
        {
            EmitSignedDivisionHelpers();
        }

        if (_helpers.Contains("shift_left32"))
        {
            EmitShiftHelper("shift_left32", "SLA (HL)", "RL (HL)", false);
        }

        if (_helpers.Contains("shift_right32"))
        {
            EmitShiftHelper("shift_right32", "SRL (HL)", "RR (HL)", true);
        }

        if (_helpers.Contains("shift_right_signed32"))
        {
            EmitShiftHelper("shift_right_signed32", "SRA (HL)", "RR (HL)", true);
        }

        if (_helpers.Contains("string_equals"))
        {
            EmitStringEqualsHelper();
        }

        if (_helpers.Contains("array_byte"))
            EmitArrayAddressHelper("array_byte", false);
        if (_helpers.Contains("array_word"))
            EmitArrayAddressHelper("array_word", true);
        if (_helpers.Contains("array_byte_index8"))
            EmitByteIndexArrayAddressHelper();
    }

    private void EmitMultiplyHelper()
    {
        _code.AppendLine();
        _code.AppendLine("cs_mul32:");
        _code.AppendLine("        XOR A");
        for (var index = 0; index < 4; index++)
        {
            _code.Append("        LD (cs_math_result+").Append(index).AppendLine("),A");
        }

        _code.AppendLine("        LD B,32");
        _code.AppendLine("cs_mul32_loop:");
        _code.AppendLine("        LD HL,cs_math_rhs+3");
        _code.AppendLine("        SRL (HL)");
        _code.AppendLine("        DEC HL");
        _code.AppendLine("        RR (HL)");
        _code.AppendLine("        DEC HL");
        _code.AppendLine("        RR (HL)");
        _code.AppendLine("        DEC HL");
        _code.AppendLine("        RR (HL)");
        _code.AppendLine("        JP NC,cs_mul32_no_add");
        for (var index = 0; index < 4; index++)
        {
            _code.Append("        LD A,(cs_math_result+").Append(index).AppendLine(")");
            _code.AppendLine("        LD C,A");
            _code.Append("        LD A,(cs_math_lhs+").Append(index).AppendLine(")");
            _code.AppendLine(index == 0 ? "        ADD A,C" : "        ADC A,C");
            _code.Append("        LD (cs_math_result+").Append(index).AppendLine("),A");
        }

        _code.AppendLine("cs_mul32_no_add:");
        _code.AppendLine("        LD HL,cs_math_lhs");
        _code.AppendLine("        SLA (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        DJNZ cs_mul32_loop");
        _code.AppendLine("        RET");
    }

    private void EmitUnsignedDivisionHelper()
    {
        _code.AppendLine();
        _code.AppendLine("cs_udiv32:");
        EmitDivisionByZeroGuard();
        Copy("cs_math_rhs", "cs_math_divisor", 4);
        Copy("cs_math_lhs", "cs_math_quotient", 4);
        _code.AppendLine("        XOR A");
        for (var index = 0; index < 5; index++)
        {
            _code.Append("        LD (cs_math_remainder+").Append(index).AppendLine("),A");
        }

        _code.AppendLine("        LD B,32");
        _code.AppendLine("cs_udiv32_loop:");
        _code.AppendLine("        LD HL,cs_math_quotient");
        _code.AppendLine("        SLA (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        LD HL,cs_math_remainder");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        RL (HL)");
        var subtract = "cs_udiv32_subtract";
        var skipSubtract = "cs_udiv32_skip_subtract";
        _code.AppendLine("        LD A,(cs_math_remainder+4)");
        _code.AppendLine("        OR A");
        _code.Append("        JP NZ,").AppendLine(subtract);
        for (var index = 3; index >= 0; index--)
        {
            _code.Append("        LD A,(cs_math_divisor+").Append(index).AppendLine(")");
            _code.AppendLine("        LD C,A");
            _code.Append("        LD A,(cs_math_remainder+").Append(index).AppendLine(")");
            _code.AppendLine("        CP C");
            _code.Append("        JP C,").AppendLine(skipSubtract);
            _code.Append("        JP NZ,").AppendLine(subtract);
        }

        _code.Append(subtract).AppendLine(":");
        for (var index = 0; index < 4; index++)
        {
            _code.Append("        LD A,(cs_math_divisor+").Append(index).AppendLine(")");
            _code.AppendLine("        LD C,A");
            _code.Append("        LD A,(cs_math_remainder+").Append(index).AppendLine(")");
            _code.AppendLine(index == 0 ? "        SUB C" : "        SBC A,C");
            _code.Append("        LD (cs_math_remainder+").Append(index).AppendLine("),A");
        }

        _code.AppendLine("        LD A,(cs_math_remainder+4)");
        _code.AppendLine("        SBC A,0");
        _code.AppendLine("        LD (cs_math_remainder+4),A");
        _code.AppendLine("        LD HL,cs_math_quotient");
        _code.AppendLine("        SET 0,(HL)");
        _code.Append(skipSubtract).AppendLine(":");
        _code.AppendLine("        DJNZ cs_udiv32_loop_continue");
        _code.AppendLine("        JP cs_udiv32_done");
        _code.AppendLine("cs_udiv32_loop_continue:");
        _code.AppendLine("        JP cs_udiv32_loop");
        _code.AppendLine("cs_udiv32_done:");
        Copy("cs_math_quotient", "cs_math_result", 4);
        _code.AppendLine("        RET");
    }

    private void EmitSignedDivisionHelpers()
    {
        _code.AppendLine();
        _code.AppendLine("cs_signed_divide:");
        EmitDivisionByZeroGuard();
        var notMinimum = "cs_signed_not_minimum";
        _code.AppendLine("        LD A,(cs_math_lhs+3)");
        _code.AppendLine("        CP 128");
        _code.Append("        JP NZ,").AppendLine(notMinimum);
        for (var index = 0; index < 3; index++)
        {
            _code.Append("        LD A,(cs_math_lhs+").Append(index).AppendLine(")");
            _code.AppendLine("        OR A");
            _code.AppendLine("        JP NZ,cs_signed_minimum_checked");
        }
        for (var index = 0; index < 4; index++)
        {
            _code.Append("        LD A,(cs_math_rhs+").Append(index).AppendLine(")");
            _code.AppendLine("        CP 255");
            _code.AppendLine("        JP NZ,cs_signed_minimum_checked");
        }

        EmitTrap(SignedOverflowFault);
        _code.AppendLine("cs_signed_minimum_checked:");
        _code.Append(notMinimum).AppendLine(":");
        _code.AppendLine("        LD A,(cs_math_lhs+3)");
        _code.AppendLine("        AND 128");
        _code.AppendLine("        LD (cs_dividend_negative),A");
        _code.AppendLine("        LD A,(cs_math_rhs+3)");
        _code.AppendLine("        AND 128");
        _code.AppendLine("        LD (cs_divisor_negative),A");
        EmitNegateMemoryIfNegative("cs_math_lhs", "cs_dividend_negative");
        EmitNegateMemoryIfNegative("cs_math_rhs", "cs_divisor_negative");
        _code.AppendLine("        CALL cs_udiv32");
        _code.AppendLine("        LD A,(cs_dividend_negative)");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,(cs_divisor_negative)");
        _code.AppendLine("        XOR C");
        _code.AppendLine("        LD (cs_quotient_negative),A");
        _code.AppendLine("        RET");
        _code.AppendLine("cs_div32:");
        _code.AppendLine("        CALL cs_signed_divide");
        EmitNegateMemoryIfNegative("cs_math_result", "cs_quotient_negative");
        _code.AppendLine("        RET");
        _code.AppendLine("cs_rem32:");
        _code.AppendLine("        CALL cs_signed_divide");
        Copy("cs_math_remainder", "cs_math_result", 4);
        EmitNegateMemoryIfNegative("cs_math_result", "cs_dividend_negative");
        _code.AppendLine("        RET");
    }

    private void EmitNegateMemoryIfNegative(string value, string sign)
    {
        var skip = NewLabel("negate_skip");
        _code.Append("        LD A,(").Append(sign).AppendLine(")");
        _code.AppendLine("        OR A");
        _code.Append("        JP Z,").AppendLine(skip);
        for (var index = 0; index < 4; index++)
        {
            _code.Append("        LD A,(").Append(value).Append('+').Append(index).AppendLine(")");
            _code.AppendLine("        CPL");
            _code.AppendLine(index == 0 ? "        ADD A,1" : "        ADC A,0");
            _code.Append("        LD (").Append(value).Append('+').Append(index).AppendLine("),A");
        }

        _code.Append(skip).AppendLine(":");
    }

    private void EmitShiftHelper(string name, string highShift, string otherShift, bool right)
    {
        _code.AppendLine();
        _code.Append("cs_").Append(name).AppendLine(":");
        _code.AppendLine("        LD A,(cs_shift_count)");
        _code.AppendLine("        OR A");
        _code.AppendLine("        RET Z");
        _code.AppendLine("        LD B,A");
        _code.Append("cs_").Append(name).AppendLine("_loop:");
        if (right)
        {
            _code.AppendLine("        LD HL,cs_math_result+3");
            _code.Append("        ").AppendLine(highShift);
            _code.AppendLine("        DEC HL");
            _code.Append("        ").AppendLine(otherShift);
            _code.AppendLine("        DEC HL");
            _code.Append("        ").AppendLine(otherShift);
            _code.AppendLine("        DEC HL");
            _code.Append("        ").AppendLine(otherShift);
        }
        else
        {
            _code.AppendLine("        LD HL,cs_math_result");
            _code.Append("        ").AppendLine(highShift);
            _code.AppendLine("        INC HL");
            _code.Append("        ").AppendLine(otherShift);
            _code.AppendLine("        INC HL");
            _code.Append("        ").AppendLine(otherShift);
            _code.AppendLine("        INC HL");
            _code.Append("        ").AppendLine(otherShift);
        }

        _code.Append("        DJNZ cs_").Append(name).AppendLine("_loop");
        _code.AppendLine("        RET");
    }

    private void EmitStringEqualsHelper()
    {
        _code.AppendLine();
        _code.AppendLine("cs_string_equals:");
        var leftNonNull = "cs_string_eq_left_nonnull";
        var equal = "cs_string_eq_true";
        var notEqual = "cs_string_eq_false";
        var loop = "cs_string_eq_loop";
        _code.AppendLine("        LD HL,(cs_string_left)");
        _code.AppendLine("        LD A,H");
        _code.AppendLine("        OR L");
        _code.Append("        JP NZ,").AppendLine(leftNonNull);
        _code.AppendLine("        LD HL,(cs_string_right)");
        _code.AppendLine("        LD A,H");
        _code.AppendLine("        OR L");
        _code.Append("        JP Z,").AppendLine(equal);
        _code.Append("        JP ").AppendLine(notEqual);
        _code.Append(leftNonNull).AppendLine(":");
        _code.AppendLine("        LD HL,(cs_string_right)");
        _code.AppendLine("        LD A,H");
        _code.AppendLine("        OR L");
        _code.Append("        JP Z,").AppendLine(notEqual);
        _code.AppendLine("        LD HL,(cs_string_left)");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD (cs_string_length_left),A");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD (cs_string_length_left+1),A");
        _code.AppendLine("        LD HL,(cs_string_right)");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,(cs_string_length_left)");
        _code.AppendLine("        CP C");
        _code.Append("        JP NZ,").AppendLine(notEqual);
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,(cs_string_length_left+1)");
        _code.AppendLine("        CP C");
        _code.Append("        JP NZ,").AppendLine(notEqual);
        _code.AppendLine("        LD HL,(cs_string_left)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD (cs_string_pointer_left),HL");
        _code.AppendLine("        LD HL,(cs_string_right)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD (cs_string_pointer_right),HL");
        _code.AppendLine("        LD A,(cs_string_length_left)");
        _code.AppendLine("        LD (cs_string_count),A");
        _code.AppendLine("        LD A,(cs_string_length_left+1)");
        _code.AppendLine("        LD (cs_string_count+1),A");
        _code.Append(loop).AppendLine(":");
        _code.AppendLine("        LD A,(cs_string_count)");
        _code.AppendLine("        LD C,A");
        _code.AppendLine("        LD A,(cs_string_count+1)");
        _code.AppendLine("        OR C");
        _code.Append("        JP Z,").AppendLine(equal);
        _code.AppendLine("        LD HL,(cs_string_pointer_left)");
        _code.AppendLine("        LD A,(HL)");
        _code.AppendLine("        LD (cs_string_compare_byte),A");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD (cs_string_pointer_left),HL");
        _code.AppendLine("        LD HL,(cs_string_pointer_right)");
        _code.AppendLine("        LD C,(HL)");
        _code.AppendLine("        INC HL");
        _code.AppendLine("        LD (cs_string_pointer_right),HL");
        _code.AppendLine("        LD A,(cs_string_compare_byte)");
        _code.AppendLine("        CP C");
        _code.Append("        JP NZ,").AppendLine(notEqual);
        _code.AppendLine("        LD A,(cs_string_count)");
        _code.AppendLine("        SUB 1");
        _code.AppendLine("        LD (cs_string_count),A");
        _code.AppendLine("        LD A,(cs_string_count+1)");
        _code.AppendLine("        SBC A,0");
        _code.AppendLine("        LD (cs_string_count+1),A");
        _code.Append("        JP ").AppendLine(loop);
        _code.Append(equal).AppendLine(":");
        _code.AppendLine("        LD A,1");
        _code.AppendLine("        RET");
        _code.Append(notEqual).AppendLine(":");
        _code.AppendLine("        XOR A");
        _code.AppendLine("        RET");
    }

    private void EmitData()
    {
        _code.AppendLine();
        foreach (var data in _dataFields.Values.Where(data => data.BankPage is null))
            EmitArrayData(data);

        foreach (var literal in _stringLabels)
        {
            var bytes = Encoding.ASCII.GetBytes(literal.Key);
            if (bytes.Length > ushort.MaxValue)
            {
                Unsupported(_methodSyntax[_currentMethod].Identifier.Parent, "A string literal cannot exceed 65535 ASCII bytes.");
                continue;
            }

            _code.Append(literal.Value).AppendLine(":");
            _code.Append("        DEFB ")
                .Append(bytes.Length & 0xff).Append(',')
                .Append((bytes.Length >> 8) & 0xff);
            foreach (var value in bytes)
            {
                _code.Append(',').Append(value);
            }

            _code.AppendLine();
        }

        foreach (var slot in _slots)
        {
            if (_staticInitializers.TryGetValue(slot.Name, out var initial))
                EmitBytes(slot.Name, initial);
            else
                EmitZeroBytes(slot.Name, slot.Size);
        }

        EmitCompilerScratch();
    }

    private void EmitBankedData()
    {
        var groups = _dataFields.Values
            .Where(data => data.BankPage is not null)
            .GroupBy(data => data.BankPage!.Value)
            .OrderBy(group => group.Key)
            .ToArray();
        foreach (var group in groups)
        {
            var size = group.Sum(data => data.Bytes.Length + 2);
            if (size > 0x7FFE)
            {
                Unsupported(_methodSyntax[_main].Identifier.Parent,
                    $"Banked page {group.Key} requires {size} bytes; at most 32766 bytes remain after reserving mapped address zero.");
                continue;
            }

            if (group.All(RequiresBankedInitialization))
            {
                var offset = 2;
                foreach (var data in group)
                {
                    _code.Append(data.Label).Append(": EQU ").Append(offset).AppendLine();
                    _code.Append(data.Label).Append("_payload: EQU ").Append(offset + 2).AppendLine();
                    offset += data.Bytes.Length + 2;
                }
                continue;
            }

            _code.AppendLine("        ORG 2");
            _code.Append("        DUMP ").Append(group.Key).AppendLine(",2");
            foreach (var data in group)
                EmitArrayData(data);
        }

        if (groups.Length != 0)
        {
            _code.AppendLine("        ORG 0x8000");
            _code.AppendLine("        DUMP 1,0");
        }
    }

    private static bool IsZeroBankedData(DataField data) =>
        data.BankPage is not null && data.Bytes.All(value => value == 0);

    private bool RequiresBankedInitialization(DataField data) =>
        IsZeroBankedData(data) || (data.BankPage is { } page &&
            page * 0x4000 + 2 + _dataFields.Values.Where(field => field.BankPage == page)
                .Sum(field => field.Bytes.Length + 2) > 12 * 0x4000);

    private void EmitBankedInitializers()
    {
        var arrays = _dataFields.Values.Where(RequiresBankedInitialization).ToArray();
        if (arrays.Length == 0)
            return;
        _code.AppendLine("cs_initialize_banked_data:");
        foreach (var data in arrays)
        {
            if (!IsZeroBankedData(data))
            {
                _code.Append("        LD HL,").Append(data.Label).AppendLine("_initial");
                _code.AppendLine("        LD (sam_initial_data_source),HL");
                _code.Append("        LD HL,").AppendLine(data.Label);
                _code.AppendLine("        LD (sam_initial_data_destination),HL");
                _code.Append("        LD HL,").Append(data.Length).AppendLine();
                _code.AppendLine("        LD (sam_initial_data_length),HL");
                _code.Append("        LD HL,").Append(data.Bytes.Length).AppendLine();
                _code.AppendLine("        LD (sam_initial_data_remaining),HL");
                _code.Append("        LD A,").Append(data.BankPage!.Value).AppendLine();
                _code.AppendLine("        LD (sam_initial_data_page),A");
                _code.AppendLine("        CALL sam_initialize_banked_runs");
                continue;
            }
            _code.Append("        LD A,").Append(data.BankPage!.Value).AppendLine();
            _code.AppendLine("        CALL sam_map_memory_page");
            _code.Append("        LD HL,").AppendLine(data.Label);
            _code.Append("        LD (HL),").Append(data.Length & 255).AppendLine();
            _code.AppendLine("        INC HL");
            _code.Append("        LD (HL),").Append(data.Length >> 8).AppendLine();
            _code.AppendLine("        INC HL");
            _code.AppendLine("        LD (HL),0");
            if (data.Bytes.Length > 1)
            {
                _code.Append("        LD DE,").Append(data.Label).AppendLine("+3");
                _code.Append("        LD BC,").Append(data.Bytes.Length - 1).AppendLine();
                _code.AppendLine("        LDIR");
            }
            _code.AppendLine("        CALL sam_restore_memory_page");
        }
        _code.AppendLine("        RET");
    }

    private void EmitBankedCode()
    {
        foreach (var group in _bankedMethodPages
                     .GroupBy(pair => pair.Value)
                     .OrderBy(group => group.Key))
        {
            _code.AppendLine("        ORG 0");
            _code.Append("        DUMP ").Append(group.Key).AppendLine(",0");
            foreach (var pair in group.OrderBy(pair => Array.IndexOf(_methods, pair.Key)))
            {
                _code.Append(_bankedMethodCode[pair.Key]);
            }
            if (group.Key == 3)
                EmitStagedBankedData();

            _code.Append("cs_banked_code_page_").Append(group.Key).AppendLine("_end:");
            _code.Append("        ASSERT cs_banked_code_page_").Append(group.Key)
            .AppendLine("_end <= 0x4000");
        }
        if (!_bankedMethodPages.Values.Contains(3) &&
            _dataFields.Values.Any(data => RequiresBankedInitialization(data) && !IsZeroBankedData(data)))
        {
            _code.AppendLine("        ORG 2");
            _code.AppendLine("        DUMP 3,2");
            EmitStagedBankedData();
            _code.AppendLine("cs_initial_data_page_3_end:");
            _code.AppendLine("        ASSERT cs_initial_data_page_3_end <= 0x4000");
        }
    }

    private void EmitStagedBankedData()
    {
        foreach (var data in _dataFields.Values.Where(data =>
                     RequiresBankedInitialization(data) && !IsZeroBankedData(data)))
        {
            var encoded = new List<byte>();
            for (var index = 0; index < data.Bytes.Length;)
            {
                var value = data.Bytes[index];
                var end = index + 1;
                while (end < data.Bytes.Length && data.Bytes[end] == value)
                    end++;
                var count = end - index;
                encoded.Add((byte)count);
                encoded.Add((byte)(count >> 8));
                encoded.Add(value);
                index = end;
            }
            encoded.AddRange([0, 0, 0]);
            EmitBytes(data.Label + "_initial", encoded);
        }
    }

    private void EmitBankedCodeCallers()
    {
        foreach (var method in _methods.Where(_bankedMethodPages.ContainsKey))
        {
            string methodLabel = _methodLabels[method];
            _code.Append(_methodCallLabels[method]).AppendLine(":");
            _code.Append("        LD A,").Append(_bankedMethodPages[method]).AppendLine();
            _code.AppendLine("        CALL sam_enter_banked_code");
            _code.Append("        CALL ").AppendLine(methodLabel);
            _code.AppendLine("        CALL sam_leave_banked_code");
            _code.AppendLine("        RET");
        }
    }

    private void EmitArrayData(DataField data)
    {
        _code.Append(data.Label).AppendLine(":");
        if (RequiresBankedInitialization(data))
        {
            _code.AppendLine("        DEFS 2");
            _code.Append(data.Label).AppendLine("_payload:");
            _code.Append("        DEFS ").Append(data.Bytes.Length).AppendLine();
            return;
        }
        _code.Append("        DEFW ").Append(data.Length).AppendLine();
        _code.Append(data.Label).AppendLine("_payload:");
        for (var index = 0; index < data.Bytes.Length; index += 32)
        {
            _code.Append("        DEFB ")
                .Append(string.Join(',', data.Bytes.Skip(index).Take(32)))
                .AppendLine();
        }
    }

    private void EmitBytes(string label, IReadOnlyList<byte> bytes)
    {
        _code.Append(label).AppendLine(":");
        for (var index = 0; index < bytes.Count; index += 32)
        {
            _code.Append("        DEFB ")
                .Append(string.Join(',', bytes.Skip(index).Take(32)))
                .AppendLine();
        }
    }

    private void EmitCompilerScratch()
    {
        if (_helpers.Any(helper => helper is "mul32" or "udiv32" or "signed_div32" or
                "shift_left32" or "shift_right32" or "shift_right_signed32"))
        {
            EmitZeroBytes("cs_math_lhs", 4);
            EmitZeroBytes("cs_math_rhs", 4);
            EmitZeroBytes("cs_math_result", 4);
        }

        if (_helpers.Contains("udiv32") || _helpers.Contains("signed_div32"))
        {
            EmitZeroBytes("cs_math_quotient", 4);
            EmitZeroBytes("cs_math_remainder", 5);
            EmitZeroBytes("cs_math_divisor", 4);
        }

        if (_helpers.Any(helper => helper.StartsWith("shift_", StringComparison.Ordinal)))
            EmitZeroBytes("cs_shift_count", 1);
        if (_helpers.Contains("signed_div32"))
        {
            EmitZeroBytes("cs_dividend_negative", 1);
            EmitZeroBytes("cs_divisor_negative", 1);
            EmitZeroBytes("cs_quotient_negative", 1);
        }

        if (_helpers.Contains("string_equals"))
        {
            EmitZeroBytes("cs_string_left", 2);
            EmitZeroBytes("cs_string_right", 2);
            EmitZeroBytes("cs_string_length_left", 2);
            EmitZeroBytes("cs_string_count", 2);
            EmitZeroBytes("cs_string_pointer_left", 2);
            EmitZeroBytes("cs_string_pointer_right", 2);
            EmitZeroBytes("cs_string_compare_byte", 1);
        }

        if (_helpers.Contains("array_byte") ||
            _helpers.Contains("array_word") ||
            _helpers.Contains("array_byte_index8"))
        {
            EmitZeroBytes("cs_array_pointer", 2);
            EmitZeroBytes("cs_array_length", 2);
            EmitZeroBytes("cs_array_address", 2);
        }
        if (_helpers.Contains("array_byte") || _helpers.Contains("array_word"))
            EmitZeroBytes("cs_array_index", 4);

        if (_usesReturnSlot || _methods.Any(method => !method.ReturnsVoid))
            EmitZeroBytes(_returnSlot, 4);
    }

    private static InitializerExpressionSyntax? GetArrayInitializer(VariableDeclaratorSyntax variable) =>
        variable.Initializer?.Value switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer,
            ImplicitArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer,
            InitializerExpressionSyntax initializer => initializer,
            _ => null
        };

    private static byte[] EncodeArrayElement(object? value, int size)
    {
        var number = System.Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        return size == 1
            ? [(byte)number]
            : [(byte)number, (byte)(number >> 8)];
    }

    private static byte[] EncodeScalar(object? value, ITypeSymbol type)
    {
        var number = ToUInt32(value);
        return Enumerable.Range(0, SizeOf(type))
            .Select(index => (byte)(number >> (index * 8)))
            .ToArray();
    }

    private static int? GetBankPage(IFieldSymbol field)
    {
        var attribute = field.GetAttributes().FirstOrDefault(candidate =>
            candidate.AttributeClass?.ToDisplayString() == "SamCoupe.Sdk.BankedDataAttribute");
        if (attribute is null)
            return null;

        if (attribute.ConstructorArguments is not [{ Value: int page }] || page is < 4 or > 14)
            return -1;
        return page;
    }

    private int? GetBankedCodePage(IMethodSymbol method)
    {
        var attribute = method.GetAttributes().FirstOrDefault(candidate =>
            candidate.AttributeClass?.ToDisplayString() == "SamCoupe.Sdk.BankedCodeAttribute");
        if (attribute is null)
            return null;

        if (attribute.ConstructorArguments.FirstOrDefault().Value is not byte page || page is < 1 or > 15)
        {
            _diagnostics.Add(new CompileDiagnostic(
                "SAM0020",
                "Banked code must use a physical page from 1 through 15.",
                _methodSyntax[method].Identifier.GetLocation().SourceTree?.FilePath ?? string.Empty,
                _methodSyntax[method].Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                _methodSyntax[method].Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1));
            return null;
        }

        if (_dataFields.Values.Any(data => data.BankPage is int dataPage &&
                (page == dataPage || page == dataPage + 1)))
        {
            _diagnostics.Add(new CompileDiagnostic(
                "SAM0021",
                $"Banked code page {page} overlaps a two-page banked-data region.",
                _methodSyntax[method].Identifier.GetLocation().SourceTree?.FilePath ?? string.Empty,
                _methodSyntax[method].Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                _methodSyntax[method].Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1));
            return null;
        }

        return page;
    }

    private void EmitZeroBytes(string label, int size)
    {
        _code.Append(label).AppendLine(":");
        _code.Append("        DEFB ").Append(string.Join(',', Enumerable.Repeat("0", size))).AppendLine();
    }

    private string InternString(string text)
    {
        if (_stringLabels.TryGetValue(text, out var label))
        {
            return label;
        }

        label = $"cs_string_{_stringLabels.Count + 1:D3}";
        _stringLabels.Add(text, label);
        return label;
    }

    private Slot AddVariableSlot(ISymbol symbol)
    {
        var methodIndex = Array.IndexOf(_methods, symbol.ContainingSymbol as IMethodSymbol);
        if (methodIndex < 0)
        {
            methodIndex = _variableSlots.Count;
        }

        var type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            _ => throw new InvalidOperationException($"Unsupported variable symbol: {symbol.Kind}.")
        };
        var slot = new Slot($"cs_v_{methodIndex + 1:D3}_{++_nextSlot:D4}", SizeOf(type), type);
        _slots.Add(slot);
        _variableSlots.Add(symbol, slot);
        return slot;
    }

    private Slot GetVariableSlot(ISymbol symbol)
    {
        if (_variableSlots.TryGetValue(symbol, out var slot))
        {
            return slot;
        }

        Unsupported(symbol.Locations.FirstOrDefault()?.SourceTree?.GetRoot().FindNode(symbol.Locations.First().SourceSpan), $"Storage for '{symbol.Name}' is unavailable.");
        var type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            _ => _compilation.GetSpecialType(SpecialType.System_Int32)
        };
        return NewSlot(type, "missing");
    }

    private Slot GetTargetSlot(IOperation operation)
    {
        return operation switch
        {
            ILocalReferenceOperation local => GetVariableSlot(local.Local),
            IParameterReferenceOperation parameter => GetVariableSlot(parameter.Parameter),
            IFieldReferenceOperation field => GetVariableSlot(field.Field),
            _ => UnsupportedTarget(operation)
        };
    }

    private Slot UnsupportedTarget(IOperation operation)
    {
        Unsupported(operation.Syntax, "Assignments are supported only to local variables and parameters.");
        return NewSlot(operation.Type ?? _compilation.GetSpecialType(SpecialType.System_Int32), "invalid_target");
    }

    private Slot NewSlot(ITypeSymbol? type, string purpose)
    {
        var slot = new Slot($"cs_{purpose}_{++_nextSlot:D5}", Math.Max(1, SizeOf(type)), type);
        _slots.Add(slot);
        return slot;
    }

    private void Copy(Slot source, Slot destination, int size) => Copy(source.Name, destination.Name, size);

    private void Copy(Slot source, string destination, int size) => Copy(source.Name, destination, size);

    private void Copy(string source, Slot destination, int size) => Copy(source, destination.Name, size);

    private void Copy(string source, string destination, int size)
    {
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return;
        }

        for (var index = 0; index < size; index++)
        {
            _code.Append("        LD A,(").Append(source).Append('+').Append(index).AppendLine(")");
            _code.Append("        LD (").Append(destination).Append('+').Append(index).AppendLine("),A");
        }
    }

    private string NewLabel(string prefix) => $"cs_{prefix}_{++_nextLabel:D5}";

    private static int SizeOf(ITypeSymbol? type) => type?.SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
        SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char or SpecialType.System_String => 2,
        SpecialType.System_Int32 or SpecialType.System_UInt32 => 4,
        SpecialType.System_Void => 1,
        _ => type is IArrayTypeSymbol
        { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte or SpecialType.System_UInt16 } ? 2 :
            type?.ToDisplayString() == "SamCoupe.Sdk.SpriteFrame" ? 3 : 4
    };

    private static bool IsSigned(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32;

    private static bool IsSupportedConstantType(ITypeSymbol? type) => type is not null &&
        (type.SpecialType == SpecialType.System_String ||
         type.SpecialType == SpecialType.System_Boolean ||
         type.SpecialType == SpecialType.System_Char ||
         type.SpecialType == SpecialType.System_Byte ||
         type.SpecialType == SpecialType.System_SByte ||
         type.SpecialType == SpecialType.System_Int16 ||
         type.SpecialType == SpecialType.System_UInt16 ||
         type.SpecialType == SpecialType.System_Int32 ||
         type.SpecialType == SpecialType.System_UInt32);

    private static uint ToUInt32(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        return value switch
        {
            bool boolean => boolean ? 1u : 0u,
            char character => character,
            sbyte signedByte => unchecked((uint)signedByte),
            short signedShort => unchecked((uint)signedShort),
            int signedInt => unchecked((uint)signedInt),
            byte unsignedByte => unsignedByte,
            ushort unsignedShort => unsignedShort,
            uint unsignedInt => unsignedInt,
            _ => unchecked((uint)System.Convert.ToInt64(value, CultureInfo.InvariantCulture))
        };
    }

    private void Unsupported(SyntaxNode? syntax, string message)
    {
        var location = syntax?.GetLocation() ?? Location.None;
        CompilerPipeline.AddSyntaxError(_diagnostics, "SAM0020", message, location);
    }

    private sealed record Slot(string Name, int Size, ITypeSymbol? Type);
    private sealed record DataField(string Label, int Length, byte[] Bytes, int ElementSize, int? BankPage);
}
