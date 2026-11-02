using System.Collections.Immutable;

namespace SamCoupe.Compiler;

public sealed record SourceFile(string Path, string Text);

public sealed record CompileDiagnostic(
    string Code,
    string Message,
    string File,
    int Line = 0,
    int Column = 0,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error);

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record SequencePoint(
    string Label,
    string File,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Method);

public sealed record OptimizationSite(string Kind, string File, int Line, int Column);
public sealed record EmissionSpan(string Name, string Category, string StartLabel, string EndLabel, int? BankPage = null);
public sealed record CompilationPerformance(
    ImmutableArray<OptimizationSite> Optimizations,
    ImmutableArray<EmissionSpan> Spans,
    ImmutableArray<string> PreparedKernels);

public sealed class CompileResult
{
    internal CompileResult(
        bool success,
        string assemblyText,
        ImmutableArray<CompileDiagnostic> diagnostics,
        ImmutableArray<SequencePoint> sourceMap,
        int maxCallDepth,
        int stackBytesRequired)
    {
        Success = success;
        AssemblyText = assemblyText;
        Diagnostics = diagnostics;
        SequencePoints = sourceMap;
        MaxCallDepth = maxCallDepth;
        StackBytesRequired = stackBytesRequired;
    }

    public bool Success { get; }
    public string AssemblyText { get; }
    public ImmutableArray<CompileDiagnostic> Diagnostics { get; }
    public ImmutableArray<SequencePoint> SequencePoints { get; }
    public ImmutableArray<SequencePoint> SourceMap => SequencePoints;
    public int MaxCallDepth { get; }
    public int StackBytesRequired { get; }
    public CompilationPerformance Performance { get; internal init; } = new([], [], []);
}

public sealed record CompileOptions
{
    public string RuntimeInclude { get; init; } = "sam-runtime.asm";
    public bool EmitSequencePoints { get; init; } = true;
    public bool EmitSourceComments { get; init; }
    public int StackLimitBytes { get; init; } = 0x1F00;
}

public sealed class CSharpCompiler
{
    public CompileResult Compile(IEnumerable<SourceFile> sourceFiles) =>
        Compile(sourceFiles, new CompileOptions());

    public CompileResult Compile(IEnumerable<SourceFile> sourceFiles, CompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(options);
        return CompilerPipeline.Compile(sourceFiles.ToArray(), options);
    }

}
