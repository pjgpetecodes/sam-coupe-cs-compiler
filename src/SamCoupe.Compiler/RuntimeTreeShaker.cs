using System.Text;
using System.Text.RegularExpressions;

namespace SamCoupe.Compiler;

/// <summary>Links the shipped flat runtime by label references and instruction fallthrough.</summary>
public static class RuntimeTreeShaker
{
    private static readonly Regex Label = new(@"^\s*([A-Za-z_]\w*)\s*:", RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"\b[A-Za-z_]\w*\b", RegexOptions.CultureInvariant);
    private static readonly Regex Terminal = new(@"^(RET|RETI|RETN)$|^(JP|JR)\s+[^,]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Data = new(@"^(EQU|DEFB|DEFW|DEFS|DM|DB|DW|DS|ASSERT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Shake(string assembly, string runtime, bool emitSourceComments = false)
    {
        if (!Regex.IsMatch(assembly, @"\bCALL\s+sam_enter_banked_code\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            runtime = Regex.Replace(runtime,
                @"(?m)^(\s*)CALL\s+sam_restore_lower_mapping\s*$",
                "$1LD A,(sam_rom_lmpr)\n$1OUT (sam_lmpr),A",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var prefix = new StringBuilder();
        var blocks = new List<Block>();
        Block? current = null;
        var sourceLine = 0;
        using var reader = new StringReader(runtime);
        while (reader.ReadLine() is { } line)
        {
            sourceLine++;
            var code = CodeOnly(line);
            string sourceComment = emitSourceComments && code.Length != 0
                ? $"; runtime/sam/hardware.asm:{sourceLine}{Environment.NewLine}"
                : "";
            var match = Label.Match(code);
            if (match.Success)
            {
                current = new Block(match.Groups[1].Value);
                blocks.Add(current);
                code = code[match.Length..].Trim();
            }

            if (current is null)
                prefix.Append(sourceComment).AppendLine(line);
            else
            {
                current.Text.Append(sourceComment).AppendLine(line);
                if (code.Length != 0)
                    current.Code.Add(code);
            }
        }

        var byName = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
            if (!byName.TryAdd(block.Name, block))
                throw new InvalidDataException($"Duplicate runtime label: {block.Name}.");
        if (!byName.ContainsKey("sam_start") || !byName.ContainsKey("sam_runtime_end"))
            throw new InvalidDataException("Runtime must define sam_start and sam_runtime_end.");

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            foreach (var code in block.Code)
            {
                // An indirect transfer needs explicit address-taken roots, not guessing.
                if (Regex.IsMatch(code, @"^(JP|CALL)\s+\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new InvalidDataException($"Indirect runtime transfer cannot be tree-shaken safely: {code}.");
                foreach (Match token in Identifier.Matches(code))
                    if (byName.TryGetValue(token.Value, out var dependency))
                        block.Dependencies.Add(dependency);
            }

            var last = block.Code.LastOrDefault();
            if (index + 1 < blocks.Count &&
                (last is null || (!Terminal.IsMatch(last) && !Data.IsMatch(last))))
                block.Dependencies.Add(blocks[index + 1]);
        }

        var roots = new Stack<Block>();
        roots.Push(byName["sam_start"]);
        roots.Push(byName["sam_runtime_end"]); // Always retain the resident/stack ASSERT.
        using var sourceReader = new StringReader(assembly);
        while (sourceReader.ReadLine() is { } line)
            foreach (Match token in Identifier.Matches(CodeOnly(line)))
                if (byName.TryGetValue(token.Value, out var root))
                    roots.Push(root);

        var live = new HashSet<Block>();
        while (roots.TryPop(out var block))
            if (live.Add(block))
                foreach (var dependency in block.Dependencies)
                    roots.Push(dependency);

        var output = new StringBuilder(prefix.ToString());
        foreach (var block in blocks)
            if (live.Contains(block))
                output.Append(block.Text);
        return output.ToString();
    }

    private static string CodeOnly(string line)
    {
        var output = new StringBuilder();
        var quoted = false;
        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                output.Append(' ');
            }
            else if (!quoted)
            {
                if (character == ';')
                    break;
                output.Append(character);
            }
        }
        return output.ToString().Trim();
    }

    private sealed class Block(string name)
    {
        public string Name { get; } = name;
        public StringBuilder Text { get; } = new();
        public List<string> Code { get; } = [];
        public HashSet<Block> Dependencies { get; } = [];
    }
}
