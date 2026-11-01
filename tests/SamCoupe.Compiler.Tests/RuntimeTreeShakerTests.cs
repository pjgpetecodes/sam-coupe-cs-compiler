using System.Diagnostics;
using SamCoupe.Compiler;
using Xunit;

namespace SamCoupe.Compiler.Tests;

public sealed class RuntimeTreeShakerTests
{
    [Fact]
    public void Halt_resumes_at_the_following_label_after_an_interrupt()
    {
        var linked = RuntimeTreeShaker.Shake("", """
            sam_start:
                EI
                HALT
            resumed:
                DI
                RET
            unused:
                RET
            sam_runtime_end:
                ASSERT sam_runtime_end < 0xE000
            """);
        Assert.Contains("resumed:", linked);
        Assert.DoesNotContain("unused:", linked);
    }

    [Fact]
    public void Follows_branches_aliases_fallthrough_and_address_taken_data()
    {
        const string runtime = """
            sam_start:
                CALL entry
                JP exit
            unused:
                CALL unused_data
                RET
            entry:
            alias:
                LD HL,table
                JR Z,branch
            fallthrough:
                RET
            branch:
                RET
            table:
                DEFW branch
            unused_data:
                DM "entry; unused"
            exit:
                RET
            sam_runtime_end:
                ASSERT sam_runtime_end < 0xE000
            """;
        var linked = RuntimeTreeShaker.Shake("; unused\n DM \"unused\"", runtime);
        foreach (var label in new[] { "entry", "alias", "fallthrough", "branch", "table", "exit", "sam_runtime_end" })
            Assert.Contains(label + ":", linked);
        Assert.DoesNotContain("unused:", linked);
        Assert.DoesNotContain("unused_data:", linked);
        Assert.Equal(linked, RuntimeTreeShaker.Shake("", runtime));
    }

    [Fact]
    public void Rejects_indirect_transfers_and_invalid_runtime_structure()
    {
        Assert.Throws<InvalidDataException>(() => RuntimeTreeShaker.Shake("", "sam_start:\n JP (HL)\nsam_runtime_end:\n"));
        Assert.Throws<InvalidDataException>(() => RuntimeTreeShaker.Shake("", "sam_start:\n RET\nsam_start:\n RET\nsam_runtime_end:\n"));
        Assert.Throws<InvalidDataException>(() => RuntimeTreeShaker.Shake("", "sam_start:\n RET\n"));
    }

    [Fact]
    public void Adds_source_line_comments_to_retained_runtime_code()
    {
        var linked = RuntimeTreeShaker.Shake("", """
            ; runtime header
            sam_start:
                RET
            sam_runtime_end:
                ASSERT sam_runtime_end < 0xE000
            """, emitSourceComments: true).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("; runtime header\n; runtime/sam/hardware.asm:2\nsam_start:", linked);
        Assert.Contains("; runtime/sam/hardware.asm:3\n    RET", linked);
        Assert.Contains("; runtime/sam/hardware.asm:5\n    ASSERT", linked);
    }

    [Fact]
    public void Retains_only_the_room_kernel_selected_by_the_program()
    {
        var compiled = new CSharpCompiler().Compile([new SourceFile("Program.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData(8)]
                static readonly byte[] Encoded = new byte[] { 0 };
                [BankedData(6)]
                static readonly byte[] Background = new byte[] { 0 };
                static void Main() { Sam.DecodeRleBackground(Encoded, 8, Background, 6); }
            }
            """)]);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics.Select(d => d.Message)));

        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hardware.asm"));
        var linked = RuntimeTreeShaker.Shake(compiled.AssemblyText, template);

        Assert.Contains("sam_decode_rle_background:", linked);
        Assert.DoesNotContain("sam_expand_byte_runs:", linked);
        Assert.DoesNotContain("sam_copy_banked_rows_to_screen:", linked);
        Assert.DoesNotContain("sam_draw_rle_masked_sprite:", linked);
    }

    [Fact]
    public void Retains_transparent_composition_only_when_the_program_uses_it()
    {
        const string program = """
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData(8)]
                static readonly byte[] Image = new byte[] { 1, 0 };
                [BankedData(6)]
                static readonly byte[] Background = new byte[] { 1, 0 };
                static void Main()
                {
                    Sam.ComposeTransparentImage(Image, 8, Background, 6, 0, 0, 1, 1, 1);
                }
            }
            """;
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hardware.asm"));
        var compiled = new CSharpCompiler().Compile([new SourceFile("Program.cs", program)]);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics.Select(d => d.Message)));
        var linked = RuntimeTreeShaker.Shake(compiled.AssemblyText, template);
        Assert.Contains("sam_compose_transparent_image:", linked);
        Assert.Contains("sam_compose_row_buffer:", linked);
        Assert.DoesNotContain("sam_decode_rle_background:", linked);

        var unused = new CSharpCompiler().Compile([new SourceFile("Program.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main() { }
                static void Compose(byte[] image, byte[] background)
                {
                    Sam.ComposeTransparentImage(image, 8, background, 6, 0, 0, 1, 1, 1);
                }
            }
            """)]);
        Assert.True(unused.Success, string.Join(Environment.NewLine, unused.Diagnostics.Select(d => d.Message)));
        var unusedLinked = RuntimeTreeShaker.Shake(unused.AssemblyText, template);
        Assert.DoesNotContain("sam_compose_transparent_image:", unusedLinked);
        Assert.DoesNotContain("sam_compose_row_buffer:", unusedLinked);
    }

    [Fact]
    public void Retains_only_the_packed_outline_kernel_when_requested()
    {
        var compiled = new CSharpCompiler().Compile([new SourceFile("Program.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData(6)]
                static readonly byte[] Background = new byte[] { 1, 0 };
                static void Main()
                {
                    Sam.DrawPackedOutline(Background, 6, 0, 24, 32, 0x77, 0x70, 0x07);
                }
            }
            """)]);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics.Select(d => d.Message)));
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hardware.asm"));
        var linked = RuntimeTreeShaker.Shake(compiled.AssemblyText, template);
        Assert.Contains("sam_draw_packed_outline:", linked);
        Assert.DoesNotContain("sam_compose_transparent_image:", linked);
        Assert.DoesNotContain("sam_decode_rle_background:", linked);
    }

    [Theory]
    [InlineData("", false, false, false, false)]
    [InlineData("Sam.WaitForKey();", false, false, true, false)]
    [InlineData("Sam.Rom.ClearScreen();", false, true, true, false)]
    [InlineData("Sam.Rom.Print(\"test\");", true, false, true, false)]
    [InlineData("Sam.WriteScreenByte(0, 255);", true, false, true, false)]
    [InlineData("Sam.Rom.PrintChar('A'); Sam.Rom.WaitForKey();", true, true, true, false)]
    [InlineData("Sam.ShiftMaskedSpriteOnePixel(Source, Destination);", true, false, true, true)]
    public void Assembles_real_runtime_dependency_closures(string body, bool print, bool rom, bool keyboard, bool shift)
    {
        var compiled = new CSharpCompiler().Compile([new SourceFile("Program.cs", $$"""
            using SamCoupe.Sdk;
            static class Program
            {
                static readonly byte[] Source = new byte[] { 1, 0, 0 };
                static readonly byte[] Destination = new byte[] { 1, 0, 0 };
                static void Main() { {{body}} }
            }
            """)]);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics.Select(d => d.Message)));
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hardware.asm"));
        var linked = RuntimeTreeShaker.Shake(compiled.AssemblyText, template);
        Assert.Equal(print, linked.Contains("sam_print:", StringComparison.Ordinal));
        Assert.Equal(rom, linked.Contains("sam_rom_dispatch:", StringComparison.Ordinal));
        Assert.Equal(keyboard, linked.Contains("sam_key_state:", StringComparison.Ordinal));
        Assert.Equal(shift, linked.Contains("sam_shift_masked_sprite_one_pixel:", StringComparison.Ordinal));
        Assert.Contains("sam_exit:", linked);
        Assert.Contains("sam_clear_screen:", linked); // Startup always clears the screen.
        Assert.Contains("ASSERT  sam_runtime_end < sam_stack_floor", linked);
        Assert.True(linked.Length < template.Length);

        var directory = Path.Combine(AppContext.BaseDirectory, "out", "sam-runtime-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "program.asm"), compiled.AssemblyText);
            File.WriteAllText(Path.Combine(directory, "sam-runtime.asm"), linked);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYZ80") ?? "pyz80")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("--obj=program.bin");
            start.ArgumentList.Add("program.asm");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(20_000), "Assembler timed out.");
            Assert.True(process.ExitCode == 0, error + output);
            Assert.True(new FileInfo(Path.Combine(directory, "program.bin")).Length > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
