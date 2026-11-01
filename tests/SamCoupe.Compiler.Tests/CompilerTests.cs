using System.Diagnostics;
using System.Text.Json;
using SamCoupe.Compiler;
using Xunit;

namespace SamCoupe.Compiler.Tests;

public sealed class CompilerTests
{
    [Fact]
    public void Emits_optional_source_location_comments_without_changing_sequence_points()
    {
        const string source = """
            static class Program
            {
                static void Main()
                {
                    return;
                }
            }
            """;
        var compiler = new CSharpCompiler();
        var defaultResult = compiler.Compile([new SourceFile("Program.cs", source)]);
        var commentedResult = compiler.Compile([new SourceFile("Program.cs", source)],
            new CompileOptions { EmitSourceComments = true });

        Assert.True(defaultResult.Success, string.Join("\n", defaultResult.Diagnostics.Select(d => d.Message)));
        Assert.True(commentedResult.Success, string.Join("\n", commentedResult.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("; Program.cs:", defaultResult.AssemblyText);
        Assert.Contains("; Program.cs:5 (Main)\ncs_seq_00001:", commentedResult.AssemblyText.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(defaultResult.SequencePoints.ToArray(), commentedResult.SequencePoints.ToArray());
    }

    [Fact]
    public void Emits_source_comments_when_sequence_points_are_disabled()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("Program.cs", """
            static class Program
            {
                static void Main()
                {
                    return;
                }
            }
            """)], new CompileOptions { EmitSourceComments = true, EmitSequencePoints = false });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("; Program.cs:5 (Main)", result.AssemblyText);
        Assert.Empty(result.SequencePoints);
        Assert.DoesNotContain("cs_seq_", result.AssemblyText);
    }

    [Fact]
    public void Clear_sprite_handles_constant_and_dynamic_named_arguments()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("Clear.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    Sam.ClearSprite(height: 2, offset: 1, widthBytes: 3);
                    Clear(4, 5, 6);
                }
                static void Clear(ushort offset, byte width, byte height)
                {
                    Sam.ClearSprite(height: height, widthBytes: width, offset: offset);
                }
            }
            """)]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        const string clear = """

            sam_clear_sprite:
                    PUSH BC
                    LD A,L
                    CALL sam_test_append
                    POP BC
                    PUSH BC
                    LD A,B
                    CALL sam_test_append
                    POP BC
                    LD A,C
                    JP sam_test_append
            """;
        Assert.Equal(new byte[] { 1, 3, 2, 4, 5, 6 },
            AssembleAndRun(result.AssemblyText, runtime: RuntimeStub + clear)[..6]);
    }

    [Theory]
    [InlineData("new byte[] { 0x11, 0x22 }")]
    [InlineData("{ 0x11, 0x22 }")]
    [InlineData("new[] { (byte)0x11, (byte)0x22 }")]
    public void Resident_CSharp_data_and_typed_frames_preserve_native_arguments(string initializer)
    {
        var source = $$"""
            using SamCoupe.Sdk;
            static class Program
            {
                static readonly byte[] Pixels = {{initializer}};
                static void Main()
                {
                    Draw(Pixels);
                    SpriteFrame frame = Sam.SelectSpriteFrame(Pixels, 7);
                    Sam.DrawMaskedSprite(frame, 0, 1, 1);
                }
                static void Draw(byte[] pixels) { Sam.DrawOpaqueSprite(pixels, 0); }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("Resident.cs", source)]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        const string drawing = """

            sam_draw_opaque_sprite:
                    LD A,(DE)
                    PUSH DE
                    CALL sam_test_append
                    POP DE
                    INC DE
                    LD A,(DE)
                    JP sam_test_append
            sam_draw_masked_sprite:
                    JP sam_test_append
            """;
        Assert.Equal(new byte[] { 0x11, 0x22, 7 },
            AssembleAndRun(result.AssemblyText, runtime: RuntimeStub + drawing)[..3]);
        Assert.Contains("DEFW 2", result.AssemblyText);
    }

    [Fact]
    public void Executes_mutable_static_state_and_checked_byte_and_word_arrays()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                static byte ticks = 2;
                static readonly byte[] Pixels = new byte[] { 10, 20 };
                static readonly ushort[] Words = new ushort[] { 0x1234, 0xABCD };
                static void Main()
                {
                    ticks++;
                    byte pixelIndex = 1;
                    ushort wordIndex = 0;
                    Pixels[pixelIndex] = (byte)(Pixels[0] + ticks);
                    Words[wordIndex]++;
                    Sam.WriteScreenByte(0, Pixels[pixelIndex]);
                    Sam.WriteScreenByte(1, (byte)Words[wordIndex]);
                    Sam.WriteScreenByte(2, (byte)(Words[wordIndex] >> 8));
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("MutableArrays.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(new byte[] { 13, 0x35, 0x12 }, actual[..3]);
        Assert.Contains("cs_array_byte_address:", result.AssemblyText);
        Assert.Contains("cs_array_byte_index8_address:", result.AssemblyText);
        Assert.Contains("cs_array_word_address:", result.AssemblyText);
    }

    [Fact]
    public void Byte_indexed_arrays_keep_native_bounds_checks()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                static readonly byte[] Pixels = new byte[] { 10, 20 };
                static byte index = 2;
                static void Main() { Sam.WriteScreenByte(0, Pixels[index]); }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("ByteIndexBounds.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(14, actual[240]);
        Assert.Contains("cs_array_byte_index8_bounds:", result.AssemblyText);
    }

    [Fact]
    public void Narrow_cast_zero_extends_byte_array_values_before_wide_arithmetic()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                static readonly byte[] Rows = new byte[] { 111 };
                static readonly byte[] Columns = new byte[] { 24 };
                static void Main()
                {
                    ushort offset = (ushort)(Rows[0] * 128 + Columns[0]);
                    Sam.WriteScreenByte(0, (byte)(offset >> 8));
                    Sam.WriteScreenByte(1, (byte)offset);
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("NarrowArrayMath.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Equal(new byte[] { 0x37, 0x98 }, AssembleAndRun(result.AssemblyText)[..2]);
    }

    [Fact]
    public void Compares_byte_variables_in_their_unsigned_domain()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    byte high = 200;
                    byte low = 4;
                    byte score = 0;
                    if (high > low) score++;
                    if (high <= low) score = 9;
                    if (low < high) score++;
                    if (high >= low) score++;
                    if (high != low) score++;
                    if (high == high) score++;
                    if (high <= high) score++;
                    if (high >= high) score++;
                    Sam.WriteScreenByte(0, score);
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("ByteComparisons.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(7, actual[0]);
    }

    [Fact]
    public void Narrows_unchecked_byte_arithmetic_with_explicit_casts()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    byte value = 250;
                    byte added = (byte)(value + 10);
                    byte subtracted = (byte)(2 - value);
                    byte doubled = (byte)(value * 2);
                    Sam.WriteScreenByte(0, added);
                    Sam.WriteScreenByte(1, subtracted);
                    Sam.WriteScreenByte(2, doubled);
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("NarrowByteMath.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(new byte[] { 4, 8, 244 }, actual[..3]);
        Assert.DoesNotContain("cs_mul32:", result.AssemblyText);
    }

    [Fact]
    public void Omits_unreachable_methods_data_and_compiler_scratch()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                static readonly byte[] NeverLoaded = new byte[] { 222, 173, 190, 239 };
                static void Main() { Sam.Print("ok"); }
                static byte Unused() { return NeverLoaded[0]; }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("TreeShake.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.DoesNotContain("222,173,190,239", result.AssemblyText);
        Assert.DoesNotContain("cs_array_byte_address:", result.AssemblyText);
        Assert.DoesNotContain("cs_math_lhs:", result.AssemblyText);
        Assert.DoesNotContain("cs_method_002:", result.AssemblyText);
        Assert.DoesNotContain("sam_draw_rle_masked_sprite", result.AssemblyText);
        Assert.DoesNotContain("cs_return_value:", result.AssemblyText);
    }

    [Fact]
    public void Banked_methods_use_selected_page_and_keep_paging_runtime_only_when_reachable()
    {
        var compiler = new CSharpCompiler();
        var result = compiler.Compile([new SourceFile("Banked.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main() { Draw(Answer()); }
                static void Draw(byte value) { Sam.WriteScreenByte(0, value); }
                [BankedCode(3)]
                static byte Answer() { return 42; }
                [BankedCode(11)]
                static byte Unused() { return 99; }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("DUMP 3,0", result.AssemblyText);
        Assert.Contains("cs_call_cs_method_", result.AssemblyText);
        Assert.DoesNotContain("DUMP 11,0", result.AssemblyText);
        Assert.DoesNotContain(result.SequencePoints, point => point.Method == "Unused");
        Assert.Contains("CALL sam_enter_banked_code", result.AssemblyText);

        var runtime = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hardware.asm"));
        var linkedBankedRuntime = RuntimeTreeShaker.Shake(result.AssemblyText, runtime);
        Assert.Contains("sam_enter_banked_code:", linkedBankedRuntime);
        Assert.Contains("sam_leave_banked_code:", linkedBankedRuntime);
        Assert.Contains("sam_active_code_page:", linkedBankedRuntime);

        var unbanked = compiler.Compile([new SourceFile("Plain.cs", """
            static class Program { static void Main() { } }
            """)]);
        Assert.True(unbanked.Success, string.Join(Environment.NewLine, unbanked.Diagnostics.Select(d => d.Message)));
        var linkedPlainRuntime = RuntimeTreeShaker.Shake(unbanked.AssemblyText, runtime);
        Assert.DoesNotContain("sam_enter_banked_code:", linkedPlainRuntime);
        Assert.DoesNotContain("sam_leave_banked_code:", linkedPlainRuntime);
        Assert.DoesNotContain("sam_active_code_page:", linkedPlainRuntime);
    }

    [Fact]
    public void Short_circuit_conditions_skip_unneeded_method_calls()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                static byte Calls;
                static void Main()
                {
                    if (Left() && Right())
                        Sam.WriteScreenByte(0, 99);
                    if (Right() || Left())
                        Sam.WriteScreenByte(1, Calls);
                }
                static bool Left() { Calls++; return false; }
                static bool Right() { Calls++; return true; }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("ShortCircuit.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Equal(new byte[] { 0, 2, 0 }, AssembleAndRun(result.AssemblyText)[..3]);
    }

    [Fact]
    public void Emits_return_scratch_for_reachable_value_returning_intrinsics()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("IntrinsicResult.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    if (Sam.IsKeyPressed(0x7FFE, 1))
                        Sam.WriteScreenByte(0, 1);
                }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("CALL sam_is_key_pressed", result.AssemblyText);
        Assert.Contains("cs_return_value:", result.AssemblyText);
    }

    [Fact]
    public void Emits_only_the_requested_one_pixel_sprite_shift_intrinsic()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("PixelShift.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static readonly byte[] Source = new byte[] { 1, 0, 0 };
                static readonly byte[] Destination = new byte[] { 1, 0, 0 };
                static void Main() { Sam.ShiftMaskedSpriteOnePixel(Source, Destination); }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("CALL sam_shift_masked_sprite_one_pixel", result.AssemblyText);
        Assert.Contains("sam_shift_source", result.AssemblyText);
    }

    [Fact]
    public void Emits_screen_copy_intrinsic_without_the_unreferenced_sprite_shift()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("ScreenCopy.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static readonly byte[] Row = new byte[] { 1, 0, 0 };
                static void Main() { Sam.CopyBytesToScreen(Row, 0, 0, 1); }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("CALL sam_copy_bytes_to_screen", result.AssemblyText);
        Assert.Contains("sam_screen_source", result.AssemblyText);
        Assert.DoesNotContain("sam_shift_masked_sprite_one_pixel:", result.AssemblyText);
    }

    [Fact]
    public void Emits_typed_room_decoders_and_banked_screen_copy_calls()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("BankedRoom.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData(8)]
                static readonly byte[] Encoded = new byte[] { 0x80, 1, 0 };
                [BankedData(8)]
                static readonly byte[] Runs = new byte[] { 0, 0 };
                [BankedData(6)]
                static readonly byte[] Background = new byte[] { 1, 0 };
                [BankedData(12)]
                static readonly byte[] Collision = new byte[] { 1, 0 };
                static void Main()
                {
                    Sam.DecodeRleBackground(Encoded, 8, Background, 6);
                    Sam.ExpandByteRuns(Runs, 8, Collision, 12);
                    Sam.CopyBankedBytesToScreen(Background, 6, 0, 0, 128, 192);
                }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("CALL sam_decode_rle_background", result.AssemblyText);
        Assert.Contains("CALL sam_expand_byte_runs", result.AssemblyText);
        Assert.Contains("CALL sam_copy_banked_rows_to_screen", result.AssemblyText);
        Assert.Contains("DUMP 8,2", result.AssemblyText);
        Assert.Contains("DUMP 6,2", result.AssemblyText);
        Assert.DoesNotContain("DUMP 12,2", result.AssemblyText);
        Assert.Contains("CALL sam_initialize_banked_runs", result.AssemblyText);
    }

    [Theory]
    [InlineData(4, 1, 6)]
    [InlineData(5, 1, 6)]
    [InlineData(8, 1, 4)]
    [InlineData(5, 16380, 6)]
    [InlineData(5, 16381, 8)]
    public void Startup_screen_avoids_every_physical_asset_page(int page, int length, int screen)
    {
        var bytes = string.Join(",", Enumerable.Repeat("85", length));
        var result = new CSharpCompiler().Compile([new SourceFile("Startup.cs", $$"""
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData({{page}})]
                static readonly byte[] Data = new byte[] { {{bytes}} };
                static void Main() { Sam.MapMemoryPage({{page}}); Sam.SetBorderColor(Data[0]); Sam.RestoreMemoryPage(); }
            }
            """)]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Contains($"cs_initial_screen_page: EQU {screen}", result.AssemblyText);
    }

    [Fact]
    public void Startup_screen_avoids_banked_code_and_rejects_exhausted_ram()
    {
        var declarations = string.Join("\n", new[] { 4, 6, 8, 10, 12, 14 }
            .Select(page => $"[BankedCode({page})] static void M{page}() {{ Sam.SetBorderColor(1); }}"));
        var calls = string.Join(" ", new[] { 4, 6, 8, 10, 12, 14 }.Select(page => $"M{page}();"));
        var result = new CSharpCompiler().Compile([new SourceFile("Startup.cs", $$"""
            using SamCoupe.Sdk;
            static class Program { {{declarations}} static void Main() { {{calls}} } }
            """)]);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("no free 24 KB"));
    }

    [Theory]
    [InlineData(16380, false)]
    [InlineData(16381, true)]
    public void Banked_group_spanning_loader_envelope_is_deferred(int length, bool deferred)
    {
        var bytes = string.Join(",", Enumerable.Repeat("85", length));
        var result = new CSharpCompiler().Compile([new SourceFile("LoaderBoundary.cs", $$"""
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData(11)]
                static readonly byte[] Data = new byte[] { {{bytes}} };
                static void Main() { Sam.MapMemoryPage(11); Sam.SetBorderColor(Data[0]); Sam.RestoreMemoryPage(); }
            }
            """)]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(deferred, result.AssemblyText.Contains("CALL sam_initialize_banked_runs", StringComparison.Ordinal));
        Assert.Equal(!deferred, result.AssemblyText.Contains("DUMP 11,2", StringComparison.Ordinal));
    }

    [Fact]
    public void Emits_the_typed_transparent_image_compositor()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("TransparentImage.cs", """
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
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("CALL sam_compose_transparent_image", result.AssemblyText);
        Assert.Contains("sam_compose_destination_page", result.AssemblyText);
    }

    [Fact]
    public void Emits_the_packed_outline_kernel_with_explicit_border_bytes()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("Outline.cs", """
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

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("CALL sam_draw_packed_outline", result.AssemblyText);
        Assert.Contains("sam_outline_top_bottom", result.AssemblyText);
        Assert.Contains("sam_outline_left", result.AssemblyText);
        Assert.Contains("sam_outline_right", result.AssemblyText);
    }

    [Fact]
    public void Emits_typed_SAA1099_register_writes_to_both_full_port_addresses()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("Saa1099.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    Sam.WriteSaaRegister(8, 33);
                }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("LD BC,0x01FF", result.AssemblyText);
        Assert.Contains("OUT (C),A", result.AssemblyText);
        Assert.Contains("LD B,0", result.AssemblyText);
        Assert.DoesNotContain("CALL sam_write_saa", result.AssemblyText);
    }

    [Fact]
    public void Compares_promoted_byte_fields_without_truncating_general_ints()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("ByteBounds.cs", """
            static class Program
            {
                static byte X;
                static int FullWidth;
                static void Main()
                {
                    if (X < 31) return;
                    if (X >= 60) return;
                    if (FullWidth < 31) return;
                }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("CP 31", result.AssemblyText);
        Assert.Contains("CP 60", result.AssemblyText);
        Assert.Contains("cs_cmp_less_", result.AssemblyText);
    }

    [Fact]
    public void Begins_double_buffering_on_explicit_pages_without_changing_argument_order()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("ScreenPages.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main() { Sam.BeginDoubleBuffer(10, 14); }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("CALL sam_begin_double_buffer_pages", result.AssemblyText);
        const string pages = """

            sam_begin_double_buffer_pages:
                    LD (0x7000),A
                    LD A,B
                    LD (0x7001),A
                    RET
            """;
        Assert.Equal(new byte[] { 10, 14 },
            AssembleAndRun(result.AssemblyText, runtime: RuntimeStub + pages)[..2]);
        Assert.DoesNotContain("sam_shift_masked_sprite_one_pixel:", result.AssemblyText);
        Assert.DoesNotContain("sam_begin_double_buffer:", result.AssemblyText);
    }

    [Fact]
    public void Emits_typed_packed_row_collision_intrinsic()
    {
        var result = new CSharpCompiler().Compile([new SourceFile("PackedRow.cs", """
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData(12)]
                static readonly byte[] Mask = new byte[] { 0x00, 0xF0, 0x0F };
                static void Main()
                {
                    Sam.MapMemoryPage(12);
                    bool solid = Sam.AnySolidPixelInPackedRow(Mask, 0, 1, 3);
                    Sam.RestoreMemoryPage();
                    if (solid)
                        Sam.WriteScreenByte(0, 1);
                }
            }
            """)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("CALL sam_any_solid_pixel_row", result.AssemblyText);
        Assert.Contains("LD B,A", result.AssemblyText);
        Assert.Contains("LD C,A", result.AssemblyText);
        Assert.DoesNotContain("DUMP 12,2", result.AssemblyText);
        Assert.Contains("CALL sam_initialize_banked_runs", result.AssemblyText);
    }

    [Fact]
    public void Emits_banked_data_in_its_selected_dump_and_only_when_reachable()
    {
        const string source = """
            using SamCoupe.Sdk;
            static class Program
            {
                [BankedData(8)]
                static readonly byte[] Active = new byte[] { 1, 2, 3 };
                [BankedData(10)]
                static readonly byte[] Unused = new byte[] { 4, 5, 6 };
                static void Main()
                {
                    Sam.MapMemoryPage(8);
                    byte value = Active[1];
                    Sam.RestoreMemoryPage();
                    Sam.WriteScreenByte(0, value);
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("BankedArrays.cs", source)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("DUMP 8,2", result.AssemblyText);
        Assert.Contains("ORG 2\n        DUMP 8,2", result.AssemblyText.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("DUMP 10,0", result.AssemblyText);
        Assert.Contains("sam_map_memory_page", result.AssemblyText);
        Assert.Contains("sam_restore_memory_page", result.AssemblyText);
        Assert.DoesNotContain("4,5,6", result.AssemblyText);
    }

    [Theory]
    [InlineData("PrintingText.cs")]
    [InlineData("ScreenWrites.cs")]
    public void Executes_first_tutorial_ports(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TutorialSources", name);
        var result = new CSharpCompiler().Compile([new SourceFile(path, File.ReadAllText(path))]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var actual = AssembleAndRun(result.AssemblyText, name == "PrintingText.cs" ? 48 : 68);

        if (name == "PrintingText.cs")
        {
            var expected = Enumerable.Range(32, 64).Select(value => (byte)value)
                .Concat(System.Text.Encoding.ASCII.GetBytes("Howsaboutis, then!!!")).ToArray();
            Assert.Equal(expected, actual[..expected.Length]);
            Assert.Equal(0, actual[expected.Length]);
            Assert.Contains("        CP 96", result.AssemblyText);
        }
        else
        {
            var expected = new byte[512];
            foreach (var offset in new[] { 0, 1, 256, 257 })
                expected[offset] = 0xFF;
            foreach (var offset in new[] { 128, 129, 384, 385 })
                expected[offset] = 0x44;
            Assert.Equal(expected, actual);
            Assert.DoesNotContain("cs_arg_", result.AssemblyText);
            Assert.DoesNotContain("cs_tmp_", result.AssemblyText);
        }
    }

    [Fact]
    public void Constant_intrinsics_preserve_conversions_named_arguments_and_traps()
    {
        const string program = """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    Sam.WriteScreenByte(value: unchecked((byte)511), offset: 1 + 2);
                    Sam.Print("A" + "B");
                    Sam.Print("");
                    Sam.Rom.PrintChar((char)127);
                    Sam.PrintChar((char)128);
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("Constants.cs", program)]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(new byte[] { 65, 66, 127, 255 }, actual[..4]);
        Assert.Equal(6, actual[240]);
        Assert.Contains("        LD HL,3\n        LD A,255\n        CALL sam_write_screen_byte",
            result.AssemblyText.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(127, 128)]
    [InlineData(128, 128)]
    [InlineData(254, 255)]
    [InlineData(255, 255)]
    [InlineData(255, 256)]
    [InlineData(0, -1)]
    public void Byte_comparison_branches_preserve_promoted_int_ordering(int value, int limit)
    {
        var program = $$"""
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    byte value = {{value}};
                    if (Less(value)) Sam.WriteScreenByte(0, 1);
                    else Sam.WriteScreenByte(0, 2);
                    if (value < {{limit}}) Sam.WriteScreenByte(1, 1);
                    else Sam.WriteScreenByte(1, 2);
                    while (value < {{limit}})
                    {
                        Sam.WriteScreenByte(2, 1);
                        break;
                    }
                    for (; value < {{limit}};)
                    {
                        Sam.WriteScreenByte(3, 1);
                        break;
                    }
                    int wide = 256;
                    if (wide < 1) Sam.WriteScreenByte(4, 1);
                    value = 255;
                    value++;
                    Sam.WriteScreenByte(5, value);
                    value--;
                    Sam.WriteScreenByte(6, value);
                }
                static bool Less(byte value)
                {
                    if (value < {{limit}}) return true;
                    return false;
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("Comparison.cs", program)]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var actual = AssembleAndRun(result.AssemblyText);
        byte branch = value < limit ? (byte)1 : (byte)2;
        byte entered = value < limit ? (byte)1 : (byte)0;
        Assert.Equal(new byte[] { branch, branch, entered, entered, 0, 0, 255 }, actual[..7]);
        if (limit is >= 0 and <= 255)
            Assert.Contains($"        CP {limit}", result.AssemblyText);
    }

    [Fact]
    public void Full_width_comparisons_do_not_compare_only_the_boolean_result_width()
    {
        const string program = """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    int first = 256;
                    int second = 1;
                    if (first < second) Sam.WriteScreenByte(0, 1);
                    if (first > second) Sam.WriteScreenByte(1, 1);
                    second = 0;
                    if (first == second) Sam.WriteScreenByte(2, 1);
                    if (first != second) Sam.WriteScreenByte(3, 1);
                    first = int.MinValue;
                    if (first < second) Sam.WriteScreenByte(4, 1);
                    uint unsigned = 0x80000000u;
                    uint zero = 0;
                    if (unsigned > zero) Sam.WriteScreenByte(5, 1);
                    if (unsigned <= zero) Sam.WriteScreenByte(6, 1);
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("WideComparison.cs", program)]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(new byte[] { 0, 1, 0, 1, 1, 1, 0 }, actual[..7]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    public void Byte_character_fast_path_keeps_ascii_guard_for_locals_and_parameters(int value)
    {
        var program = $$"""
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    byte character = {{value}};
                    Sam.PrintChar((char)character);
                    Print(character);
                }
                static void Print(byte character)
                {
                    Sam.Rom.PrintChar((char)character);
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("ByteChar.cs", program)]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var actual = AssembleAndRun(result.AssemblyText);
        if (value < 128)
        {
            Assert.Equal(new byte[] { (byte)value, (byte)value }, actual[..2]);
            Assert.Equal(0, actual[240]);
        }
        else
        {
            Assert.Equal(0, actual[0]);
            Assert.Equal(6, actual[240]);
        }
        Assert.Contains("        LD H,0", result.AssemblyText);
    }

    [Fact]
    public void Compiles_and_executes_control_flow_calls_and_32_bit_arithmetic_on_z80()
    {
        const string program = """
            using SamCoupe.Sdk;

            static class Program
            {
                static int Sum(int limit)
                {
                    int sum = 0;
                    for (int i = 0; i < limit; i++)
                    {
                        if (i == 2) continue;
                        if (i > 5) break;
                        sum += i;
                    }

                    return sum;
                }

                static bool IsPositive(int value)
                {
                    return value > 0;
                }

                static void Main()
                {
                    int sum = Sum(10);
                    int productLeft = 123456;
                    int productRight = 321;
                    int product = productLeft * productRight;
                    int dividend = -100000;
                    int signedDivisor = 3;
                    int quotient = dividend / signedDivisor;
                    int remainder = dividend % signedDivisor;
                    int shiftCount = 33;
                    int shift = 1 << shiftCount;
                    int divisor = 0;
                    byte shortCircuit = 0;
                    if (IsPositive(0) && 1 / divisor > 0)
                    {
                        shortCircuit = 1;
                    }

                    uint left = uint.MaxValue;
                    uint right = 2147483648u;
                    byte unsignedGreater = 0;
                    if (left > right) unsignedGreater = 1;

                    int negative = -1;
                    int zero = 0;
                    byte signedLess = 0;
                    if (negative < zero) signedLess = 1;

                    int decrement = 100;
                    int subtrahend = 42;
                    int difference = decrement - subtrahend;
                    int mask = 0x1234;
                    int bits = ((mask & 0x00FF) ^ 0x0001) | 0x0002;
                    int signedShiftCount = 2;
                    int signedShift = negative >> signedShiftCount;
                    uint shiftInput = 0x80000000u;
                    int unsignedShiftCount = 31;
                    uint unsignedShift = shiftInput >> unsignedShiftCount;

                    byte orShortCircuit = 0;
                    if (IsPositive(1) || 1 / divisor > 0) orShortCircuit = 1;

                    int countdown = 3;
                    int whileSum = 0;
                    while (countdown > 0)
                    {
                        whileSum += countdown;
                        countdown--;
                    }

                    int wrap = int.MaxValue;
                    wrap++;

                    byte small = 250;
                    byte ten = 10;
                    int promoted = small + ten;
                    sbyte signedByte = -2;
                    int signExtension = signedByte;
                    ushort unsignedShort = 0x8001;
                    int zeroExtension = unsignedShort;
                    int truncateSource = 511;
                    byte truncated = (byte)truncateSource;
                    int compoundShift = 3;
                    int compoundShiftCount = 2;
                    compoundShift <<= compoundShiftCount;

                    Sam.WriteScreenByte(0, (byte)sum);
                    Sam.WriteScreenByte(1, (byte)(product >> 24));
                    Sam.WriteScreenByte(2, (byte)quotient);
                    Sam.WriteScreenByte(3, (byte)remainder);
                    Sam.WriteScreenByte(4, (byte)shift);
                    Sam.WriteScreenByte(5, shortCircuit);
                    Sam.WriteScreenByte(6, unsignedGreater);
                    Sam.WriteScreenByte(7, signedLess);
                    Sam.WriteScreenByte(8, (byte)(wrap >> 24));
                    Sam.WriteScreenByte(9, (byte)(left / 2u >> 24));
                    Sam.WriteScreenByte(10, (byte)difference);
                    Sam.WriteScreenByte(11, (byte)bits);
                    Sam.WriteScreenByte(12, (byte)signedShift);
                    Sam.WriteScreenByte(13, (byte)unsignedShift);
                    Sam.WriteScreenByte(14, orShortCircuit);
                    Sam.WriteScreenByte(15, (byte)whileSum);
                    Sam.WriteScreenByte(16, (byte)promoted);
                    Sam.WriteScreenByte(17, (byte)(promoted >> 8));
                    Sam.WriteScreenByte(18, (byte)signExtension);
                    Sam.WriteScreenByte(19, (byte)(signExtension >> 8));
                    Sam.WriteScreenByte(20, (byte)zeroExtension);
                    Sam.WriteScreenByte(21, (byte)(zeroExtension >> 8));
                    Sam.WriteScreenByte(22, truncated);
                    Sam.WriteScreenByte(23, (byte)compoundShift);
                }
            }
            """;

        var result = new CSharpCompiler().Compile([new SourceFile("Program.cs", program)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.File}({d.Line},{d.Column}) {d.Code}: {d.Message}")));
        Assert.Contains("        JP sam_start", result.AssemblyText);
        Assert.Contains("cs_resident_end:", result.AssemblyText);
        Assert.True(result.MaxCallDepth >= 2);
        Assert.True(result.StackBytesRequired >= result.MaxCallDepth * 2 + 128);
        Assert.NotEmpty(result.SequencePoints);

        var actual = AssembleAndRun(result.AssemblyText);
        byte[] expected =
        [
            unchecked((byte)13),
            unchecked((byte)(123456 * 321 >> 24)),
            unchecked((byte)(-100000 / 3)),
            unchecked((byte)(-100000 % 3)),
            unchecked((byte)(1 << 33)),
            0,
            1,
            1,
            unchecked((byte)(int.MinValue >> 24)),
            unchecked((byte)(uint.MaxValue / 2u >> 24)),
            58,
            0x37,
            unchecked((byte)(-1 >> 2)),
            1,
            1,
            6,
            4,
            1,
            254,
            255,
            1,
            128,
            255,
            12
        ];
        Assert.Equal(expected, actual[..expected.Length]);
        Assert.Equal(0, actual[240]);
    }

    [Fact]
    public void Sign_extends_narrow_signed_values_for_assignments_arguments_and_returns()
    {
        const string program = """
            using SamCoupe.Sdk;

            static class Program
            {
                static void Main()
                {
                    sbyte value = -2;
                    int assigned = 0;
                    assigned = value;
                    int argument = Identity(value);
                    int returned = ReturnNarrowValue();

                    Sam.WriteScreenByte(0, (byte)assigned);
                    Sam.WriteScreenByte(1, (byte)(assigned >> 8));
                    Sam.WriteScreenByte(2, (byte)argument);
                    Sam.WriteScreenByte(3, (byte)(argument >> 8));
                    Sam.WriteScreenByte(4, (byte)returned);
                    Sam.WriteScreenByte(5, (byte)(returned >> 8));
                }

                static int Identity(int value)
                {
                    return value;
                }

                static int ReturnNarrowValue()
                {
                    sbyte value = -2;
                    return value;
                }
            }
            """;

        var result = new CSharpCompiler().Compile([new SourceFile("SignExtension.cs", program)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.File}({d.Line},{d.Column}) {d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(new byte[] { 254, 255, 254, 255, 254, 255 }, actual[..6]);
    }

    [Fact]
    public void Preserves_nested_same_callee_arguments_and_left_to_right_evaluation()
    {
        const string program = """
            using SamCoupe.Sdk;

            static class Program
            {
                static void Main()
                {
                    int nested = F(1, F(2, 3));
                    int counter = 1;
                    int ordered = F(counter++, F(counter++, counter++));
                    int named = F(second: F(2, 3), first: 1);

                    Sam.WriteScreenByte(0, (byte)nested);
                    Sam.WriteScreenByte(1, (byte)ordered);
                    Sam.WriteScreenByte(2, (byte)counter);
                    Sam.WriteScreenByte(3, (byte)named);
                }

                static int F(int first, int second)
                {
                    return first * 10 + second;
                }
            }
            """;

        var result = new CSharpCompiler().Compile([new SourceFile("NestedArguments.cs", program)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.File}({d.Line},{d.Column}) {d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(new byte[] { 33, 33, 4, 33 }, actual[..4]);
        Assert.Equal(0, actual[240]);
    }

    [Fact]
    public void Rejects_unsupported_64_bit_promotions_and_recursive_methods()
    {
        const string program = """
            static class Program
            {
                static void Main()
                {
                    long value = 1L;
                    int result = (int)value;
                    result = Recurse(result);
                }

                static int Recurse(int value)
                {
                    return Recurse(value);
                }
            }
            """;

        var result = new CSharpCompiler().Compile([new SourceFile("Unsupported.cs", program)]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SAM0021" && diagnostic.File == "Unsupported.cs");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SAM0018" && diagnostic.Line > 0);
    }

    [Fact]
    public void Compiles_and_executes_the_text_sample_string_indexer()
    {
        const string program = """
            using SamCoupe.Sdk;

            static class Program
            {
                static void Main()
                {
                    string message = "C# on " + "SAM Coupe!";
                    PrintMessage(message);
                    Sam.WaitForKey();
                }

                static void PrintMessage(string text)
                {
                    Sam.Rom.Print(text);
                    Sam.Rom.PrintChar('\r');
                    for (int index = 0; index < text.Length; index++)
                        Sam.Rom.PrintChar(text[index]);
                    Sam.Rom.PrintChar('\r');
                }
            }
            """;

        var result = new CSharpCompiler().Compile([new SourceFile("Text/Program.cs", program)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.File}({d.Line},{d.Column}) {d.Code}: {d.Message}")));
        Assert.Contains("CALL sam_print_char", result.AssemblyText);
        Assert.Contains("cs_trap_string_bounds:", result.AssemblyText);
        var actual = AssembleAndRun(result.AssemblyText);
        var expected = System.Text.Encoding.ASCII.GetBytes("C# on SAM Coupe!\rC# on SAM Coupe!\r");
        Assert.Equal(expected, actual[..expected.Length]);
        Assert.Equal(0, actual[240]);
    }

    [Fact]
    public void Rejects_missing_entry_point_with_stable_diagnostic()
    {
        var result = new CSharpCompiler().Compile(
            [new SourceFile("Library.cs", "static class Library { static int Value() { return 7; } }")]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SAM0002");
    }

    [Fact]
    public void Binds_rom_clear_and_wait_to_only_the_verified_runtime_intrinsics()
    {
        const string program = """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    Sam.Rom.ClearScreen();
                    Sam.Rom.WaitForKey();
                }
            }
            """;

        var result = new CSharpCompiler().Compile([new SourceFile("Rom.cs", program)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("CALL sam_rom_clear_screen", result.AssemblyText);
        Assert.Contains("CALL sam_rom_wait_for_key", result.AssemblyText);
    }

    [Fact]
    public void Compares_strings_by_contents_and_supports_null_equality()
    {
        const string program = """
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    string first = "same";
                    string second = "same";
                    string? missing = null;
                    byte contentsEqual = 0;
                    byte nullEqual = 0;
                    if (first == second) contentsEqual = 1;
                    if (missing == null) nullEqual = 1;
                    Sam.WriteScreenByte(0, contentsEqual);
                    Sam.WriteScreenByte(1, nullEqual);
                }
            }
            """;

        var result = new CSharpCompiler().Compile([new SourceFile("Strings.cs", program)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal([1, 1], actual[..2]);
        Assert.Equal(0, actual[240]);
    }

    [Theory]
    [InlineData("string? text = null; Sam.Print(text);", 1)]
    [InlineData("string text = \"A\"; Sam.PrintChar(text[1]);", 2)]
    public void Emits_visible_runtime_traps_for_invalid_string_access(string body, byte faultCode)
    {
        var program = $$"""
            using SamCoupe.Sdk;
            static class Program
            {
                static void Main()
                {
                    {{body}}
                }
            }
            """;
        var result = new CSharpCompiler().Compile([new SourceFile("Trap.cs", program)]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var actual = AssembleAndRun(result.AssemblyText);
        Assert.Equal(faultCode, actual[240]);
    }

    [Fact]
    public void Enforces_conservative_call_stack_budget()
    {
        const string program = """
            static class Program
            {
                static void Main() { First(); }
                static void First() { Second(); }
                static void Second() { }
            }
            """;

        var result = new CSharpCompiler().Compile(
            [new SourceFile("Stack.cs", program)],
            new CompileOptions { StackLimitBytes = 135 });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SAM0019");
    }

    private static byte[] AssembleAndRun(string assemblyText, int? mainByteBudget = null, string runtime = RuntimeStub)
    {
        var pyz80 = Environment.GetEnvironmentVariable("PYZ80") ?? "pyz80";
        var python = Environment.GetEnvironmentVariable("PYTHON") ?? "python";
        var runner = Path.Combine(AppContext.BaseDirectory, "z80-runner.py");
        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, $"z80-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        try
        {
            var sourcePath = Path.Combine(artifactDirectory, "program.asm");
            var runtimePath = Path.Combine(artifactDirectory, "sam-runtime.asm");
            var objectPath = Path.Combine(artifactDirectory, "program.bin");
            var mapPath = Path.Combine(artifactDirectory, "program.map");
            File.WriteAllText(sourcePath, assemblyText);
            File.WriteAllText(runtimePath, runtime);

            var assembler = new ProcessStartInfo(pyz80)
            {
                WorkingDirectory = artifactDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            assembler.ArgumentList.Add($"--obj={objectPath}");
            assembler.ArgumentList.Add($"--mapfile={mapPath}");
            assembler.ArgumentList.Add("--exportfile=program.symbols");
            assembler.ArgumentList.Add(sourcePath);
            RunProcess(assembler, "pyz80 assembly failed");

            if (mainByteBudget is { } budget)
            {
                var exported = File.ReadAllText(Path.Combine(artifactDirectory, "program.symbols"));
                var symbols = System.Text.RegularExpressions.Regex.Matches(exported, @"V(\w+)\s+p\d+\s+I(\d+)")
                    .ToDictionary(match => match.Groups[1].Value,
                        match => int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                var mainBytes = symbols["cs_trap_string_bounds"] - symbols["cs_method_001"];
                Assert.InRange(mainBytes, 1, budget);
            }

            var emulator = new ProcessStartInfo(python)
            {
                WorkingDirectory = artifactDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            emulator.ArgumentList.Add(runner);
            emulator.ArgumentList.Add(objectPath);
            var output = RunProcess(emulator, $"Z80 execution failed; map: {mapPath}");
            return (JsonSerializer.Deserialize<int[]>(output.Trim())
                    ?? throw new InvalidOperationException("The Z80 runner returned no output bytes."))
                .Select(value => checked((byte)value))
                .ToArray();
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("KEEP_Z80_ARTIFACTS") != "1")
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }

    private static string RunProcess(ProcessStartInfo startInfo, string failureMessage)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{failureMessage}: process timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{failureMessage}:{Environment.NewLine}{error}{output}");
        }

        return output;
    }

    private const string RuntimeStub = """
        sam_start:
                LD SP,0xF000
                LD HL,0x7000
                LD (0x7200),HL
                CALL cs_main
                HALT
        sam_trap:
                LD (0x70F0),A
                HALT
        sam_write_screen_byte:
                PUSH HL
                LD DE,0x7000
                ADD HL,DE
                LD (HL),A
                POP HL
                RET
        sam_rom_clear_screen:
                RET
        sam_print:
                LD A,(HL)
                LD (0x7202),A
                INC HL
                INC HL
        sam_print_loop:
                LD A,(0x7202)
                OR A
                RET Z
                LD A,(HL)
                PUSH HL
                CALL sam_test_append
                POP HL
                INC HL
                LD A,(0x7202)
                DEC A
                LD (0x7202),A
                JP sam_print_loop
        sam_print_char:
                LD A,L
                JP sam_test_append
        sam_test_append:
                LD HL,(0x7200)
                LD (HL),A
                INC HL
                LD (0x7200),HL
                RET
        sam_wait_for_key:
                RET
        sam_rom_wait_for_key:
                RET
        """;
}
