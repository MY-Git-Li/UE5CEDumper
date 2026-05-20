using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class InvokeScriptTests
{
    // --- FunctionInfoModel.DecodeFunctionFlags ---

    [Fact]
    public void DecodeFunctionFlags_Native_ReturnsNative()
    {
        var result = FunctionInfoModel.DecodeFunctionFlags(0x0000_0400);
        Assert.Contains("Native", result);
    }

    [Fact]
    public void DecodeFunctionFlags_BlueprintCallable_Returns()
    {
        var result = FunctionInfoModel.DecodeFunctionFlags(0x0400_0000);
        Assert.Contains("BlueprintCallable", result);
    }

    [Fact]
    public void DecodeFunctionFlags_MultipleFlags_ReturnsAll()
    {
        // Native(0x400) | BlueprintCallable(0x4000000) | Static(0x2000)
        var result = FunctionInfoModel.DecodeFunctionFlags(0x0400_2400);
        Assert.Contains("Native", result);
        Assert.Contains("BlueprintCallable", result);
        Assert.Contains("Static", result);
    }

    [Fact]
    public void DecodeFunctionFlags_Zero_ReturnsEmpty()
    {
        var result = FunctionInfoModel.DecodeFunctionFlags(0);
        Assert.Equal("", result);
    }

    // --- InvokeScriptGenerator: Mailbox-based scripts ---

    [Fact]
    public void Generate_NoParams_ProducesDirectInvoke()
    {
        var func = new FunctionInfoModel
        {
            Name = "openShop",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("ShopKeeper_C", "openShop", func);

        Assert.Contains("[ENABLE]", script);
        Assert.Contains("[DISABLE]", script);
        Assert.Contains("OWNER_CLASS", script);
        Assert.Contains("'ShopKeeper_C'", script);
        Assert.Contains("'openShop'", script);
        // Mailbox-based invocation
        Assert.Contains("g_invokeMailbox", script);
        Assert.Contains("waitDone", script);
        // No form creation for zero-param functions
        Assert.DoesNotContain("createForm", script);
        // No executeCodeEx (mailbox uses ReadProcessMemory/WriteProcessMemory)
        Assert.DoesNotContain("executeCodeEx", script);
        Assert.DoesNotContain("dllCall", script);
    }

    [Fact]
    public void Generate_WithParams_ProducesForm()
    {
        var func = new FunctionInfoModel
        {
            Name = "addMoney",
            NumParms = 3,
            ParmsSize = 6,
            Params = new List<FunctionParamModel>
            {
                new() { Name = "Amount", TypeName = "IntProperty", Size = 4, Offset = 0 },
                new() { Name = "SkipCounting", TypeName = "BoolProperty", Size = 1, Offset = 4 },
                new() { Name = "Success", TypeName = "BoolProperty", Size = 1, Offset = 5, IsOut = true },
            },
        };

        var script = InvokeScriptGenerator.Generate("playerCharacterBP_C", "addMoney", func);

        Assert.Contains("createForm", script);
        Assert.Contains("'Amount", script);
        Assert.Contains("'SkipCounting", script);
        Assert.Contains("'Success", script);
        // Mailbox param writes use PD (params_data base)
        Assert.Contains("writeInteger(PD +", script);
        Assert.Contains("writeBytes(PD +", script);
        Assert.Contains("FIRE", script);
    }

    [Fact]
    public void Generate_ReturnParam_ExcludedFromForm()
    {
        var func = new FunctionInfoModel
        {
            Name = "getValue",
            ReturnType = "IntProperty",
            Params = new List<FunctionParamModel>
            {
                new() { Name = "ReturnValue", TypeName = "IntProperty", Size = 4, Offset = 0, IsReturn = true },
            },
        };

        var script = InvokeScriptGenerator.Generate("TestClass", "getValue", func);

        // Return param should not appear in form — treated as no-param direct invoke
        Assert.DoesNotContain("createForm", script);
        Assert.Contains("waitDone", script);
    }

    [Fact]
    public void Generate_PointerParam_UsesHexParsing()
    {
        var func = new FunctionInfoModel
        {
            Name = "setTarget",
            ParmsSize = 8,
            Params = new List<FunctionParamModel>
            {
                new() { Name = "Target", TypeName = "ObjectProperty", Size = 8, Offset = 0 },
            },
        };

        var script = InvokeScriptGenerator.Generate("AI_C", "setTarget", func);

        Assert.Contains("writeQword", script); // 8-byte pointer write
        Assert.Contains("0x0", script);        // default for pointer
    }

    [Fact]
    public void Generate_ObjectParamWithKnownClass_LabelShowsExpectedType()
    {
        // Stage 1 (Invoke param picker): when the DLL surfaces the param's
        // expected UClass via FObjectPropertyBase::PropertyClass, the
        // generated CE-side script label should self-document it so the
        // user knows what kind of pointer to provide.
        var func = new FunctionInfoModel
        {
            Name = "setTarget",
            ParmsSize = 8,
            Params = new List<FunctionParamModel>
            {
                new() {
                    Name = "Target", TypeName = "ObjectProperty", Size = 8, Offset = 0,
                    ObjectClassName = "AActor",
                },
            },
        };

        var script = InvokeScriptGenerator.Generate("AI_C", "setTarget", func);

        // Label should include "UObject*: AActor" so users see the expected
        // class right inside the CE-form dialog without having to look it up.
        Assert.Contains("UObject*: AActor", script);
    }

    [Fact]
    public void Generate_ObjectParamWithoutKnownClass_LabelOmitsColonSuffix()
    {
        // Stage 1 backward-compat: when the DLL pre-dates the obj_class field
        // (or the param genuinely lacks a constraint), the label should fall
        // back to the original "[UObject*, ...]" form — no spurious colon.
        var func = new FunctionInfoModel
        {
            Name = "setTarget",
            ParmsSize = 8,
            Params = new List<FunctionParamModel>
            {
                new() {
                    Name = "Target", TypeName = "ObjectProperty", Size = 8, Offset = 0,
                    // ObjectClassName left default ""
                },
            },
        };

        var script = InvokeScriptGenerator.Generate("AI_C", "setTarget", func);

        // No ": " suffix between the type tag and the size tag.
        Assert.DoesNotContain("UObject*:", script);
    }

    [Fact]
    public void Generate_FloatParam_UsesTonumber()
    {
        var func = new FunctionInfoModel
        {
            Name = "setSpeed",
            ParmsSize = 4,
            Params = new List<FunctionParamModel>
            {
                new() { Name = "Speed", TypeName = "FloatProperty", Size = 4, Offset = 0 },
            },
        };

        var script = InvokeScriptGenerator.Generate("Character_C", "setSpeed", func);

        Assert.Contains("writeFloat", script); // float write
        Assert.Contains("0.0", script);        // default for float
    }

    [Fact]
    public void Generate_SpecialCharsInName_Escaped()
    {
        var func = new FunctionInfoModel
        {
            Name = "K2_OnReset",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("BP_Base'_C", "K2_OnReset", func);

        // Single quote in class name should be escaped for Lua
        Assert.Contains("BP_Base\\'_C", script);
    }

    [Fact]
    public void Generate_UsesLfLineEndings()
    {
        var func = new FunctionInfoModel
        {
            Name = "test",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("TestClass", "test", func);

        Assert.Contains("\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void Generate_UsesAsciiOnly()
    {
        var func = new FunctionInfoModel
        {
            Name = "test",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("TestClass", "test", func);

        Assert.DoesNotContain("\u2014", script);
        Assert.DoesNotContain("\u2192", script);
        Assert.All(script, c => Assert.True(c < 128, $"Non-ASCII char found: U+{(int)c:X4}"));
    }

    [Fact]
    public void Generate_UsesSingleQuotesForLuaStrings()
    {
        var func = new FunctionInfoModel
        {
            Name = "openShop",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("ShopKeeper_C", "openShop", func);

        Assert.Contains("'ShopKeeper_C'", script);
        Assert.Contains("'openShop'", script);
        Assert.DoesNotContain("\"ShopKeeper_C\"", script);
        Assert.DoesNotContain("\"openShop\"", script);
    }

    // --- Mailbox-specific tests ---

    [Fact]
    public void Generate_UsesMailboxApproach()
    {
        var func = new FunctionInfoModel
        {
            Name = "test",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("TestClass", "test", func);

        // Should find mailbox symbol
        Assert.Contains("g_invokeMailbox", script);
        // Should use mailbox helpers
        Assert.Contains("writeMbStr", script);
        Assert.Contains("waitDone", script);
        Assert.Contains("readErr", script);
        // Should NOT use executeCodeEx or DLL call helpers
        Assert.DoesNotContain("executeCodeEx", script);
        Assert.DoesNotContain("dllCall", script);
        Assert.DoesNotContain("dllCallPtr", script);
        Assert.DoesNotContain("cstr(", script);
        // Should NOT contain third-party CE plugin references
        Assert.DoesNotContain("UE_InvokeActorEvent", script);
        Assert.DoesNotContain("UE_GetAllObjectsOfClass", script);
        Assert.DoesNotContain("UE_GetFunctionsOfObject", script);
    }

    [Fact]
    public void Generate_MailboxCommands_CorrectSequence()
    {
        var func = new FunctionInfoModel
        {
            Name = "openShop",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("ShopKeeper_C", "openShop", func);

        // CMD_FIND_INSTANCE = 2
        Assert.Contains("writeInteger(mb + 0x000, 2)", script);
        // CMD_FIND_FUNCTION = 3
        Assert.Contains("writeInteger(mb + 0x000, 3)", script);
        // CMD_INVOKE = 1
        Assert.Contains("writeInteger(mb + 0x000, 1)", script);
        // Reads instance + function addresses from mailbox
        Assert.Contains("readQword(mb + 0x010)", script);  // instanceAddr
        Assert.Contains("readQword(mb + 0x018)", script);  // ufuncPtr
        // Reads result code
        Assert.Contains("readInteger(mb + 0x008)", script); // result
    }

    [Fact]
    public void Generate_NoExecuteCodeEx()
    {
        var func = new FunctionInfoModel
        {
            Name = "openShop",
            Params = new(),
        };

        var script = InvokeScriptGenerator.Generate("ShopKeeper_C", "openShop", func);

        // The whole point of mailbox: no executeCodeEx / CreateRemoteThread
        Assert.DoesNotContain("executeCodeEx", script);
        Assert.DoesNotContain("CreateRemoteThread", script);
        // Comment explains this
        Assert.Contains("shared memory mailbox", script);
    }

    [Fact]
    public void Generate_ParamOffsets_WrittenCorrectly()
    {
        var func = new FunctionInfoModel
        {
            Name = "doThing",
            ParmsSize = 13,
            Params = new List<FunctionParamModel>
            {
                new() { Name = "X", TypeName = "IntProperty", Size = 4, Offset = 0 },
                new() { Name = "Y", TypeName = "FloatProperty", Size = 4, Offset = 4 },
                new() { Name = "Flag", TypeName = "BoolProperty", Size = 1, Offset = 8 },
                new() { Name = "Ptr", TypeName = "ObjectProperty", Size = 8, Offset = 9, IsReturn = true },
            },
        };

        var script = InvokeScriptGenerator.Generate("TestClass", "doThing", func);

        // Should write params at correct offsets via PD (params_data base)
        Assert.Contains("writeInteger(PD + 0,", script);
        Assert.Contains("writeFloat(PD + 4,", script);
        Assert.Contains("writeBytes(PD + 8,", script);
        // Return param should NOT be written
        Assert.DoesNotContain("PD + 9", script);
    }

    [Fact]
    public void Generate_ParamBuffer_IncludesParmsSize()
    {
        var func = new FunctionInfoModel
        {
            Name = "addMoney",
            ParmsSize = 42,
            Params = new List<FunctionParamModel>
            {
                new() { Name = "Amount", TypeName = "IntProperty", Size = 4, Offset = 0 },
            },
        };

        var script = InvokeScriptGenerator.Generate("TestClass", "addMoney", func);

        // PARMS_SIZE embedded in script
        Assert.Contains("PARMS_SIZE   = 42", script);
        // Mailbox zero-fill uses PD base
        Assert.Contains("PD + i, 0", script);
    }

    // --- InputParams property ---

    [Fact]
    public void InputParams_ExcludesReturnParam()
    {
        var func = new FunctionInfoModel
        {
            Params = new List<FunctionParamModel>
            {
                new() { Name = "A", IsReturn = false },
                new() { Name = "B", IsReturn = false },
                new() { Name = "ReturnValue", IsReturn = true },
            },
        };

        var input = func.InputParams.ToList();
        Assert.Equal(2, input.Count);
        Assert.DoesNotContain(input, p => p.Name == "ReturnValue");
    }

    // ==================================================================
    // BakedScriptGenerator -- non-interactive AA Script export (todo 3a)
    //
    // The generator produces a script that depends on
    // ue5_invoke_helper.lua being embedded in the user's .CT. Tests
    // assert both the structural shape (loader, PARAMS table, invoke
    // call, cleanup) and the literal-rendering correctness for each
    // supported UE type.
    // ==================================================================

    private static IReadOnlyList<BakedParamValue> NoBakedValues
        => Array.Empty<BakedParamValue>();

    [Fact]
    public void BakedGenerate_NoParams_ProducesEmptyParamsTableAndDirectInvoke()
    {
        var script = BakedScriptGenerator.Generate(
            "Player_C", "openShop", parmsSize: 0, NoBakedValues);

        // The fast-path comment + empty table
        Assert.Contains("(no input params -- direct invoke)", script);
        Assert.Contains("local PARAMS = {}", script);
        // Helper invoke with parmsSize=0
        Assert.Contains(
            "invokeUFunction('Player_C', 'openShop', 0, PARAMS)",
            script);
    }

    [Fact]
    public void BakedGenerate_StructureBlocks_AllPresent()
    {
        var script = BakedScriptGenerator.Generate(
            "PlayerCharacter", "AddMoney", 5,
            new[] { new BakedParamValue("Amount", "IntProperty", 4, 0, "1000") });

        // [ENABLE]/[DISABLE] block markers
        Assert.Contains("[ENABLE]", script);
        Assert.Contains("[DISABLE]", script);
        Assert.Contains("{$lua}", script);
        Assert.Contains("{$asm}", script);
        Assert.Contains("if syntaxcheck then return end", script);

        // Helper loader uses findTableFile -- no fs fallback per design
        Assert.Contains("findTableFile('ue5_invoke_helper.lua')", script);
        Assert.Contains("Table -> Add File...", script);
        // Loader bails cleanly on missing file
        Assert.Contains("if memrec then memrec.Active = false end", script);

        // Cleanup: silent on success, close lua engine
        Assert.Contains("synchronize(function() getLuaEngine().Close() end)",
                        script);

        // Should NOT contain anything that would prompt user (no createForm)
        Assert.DoesNotContain("createForm", script);
    }

    [Fact]
    public void BakedGenerate_IntParam_RendersDecimalLiteral()
    {
        var script = BakedScriptGenerator.Generate(
            "C", "F", 4,
            new[] { new BakedParamValue("Amount", "IntProperty", 4, 0, "1000") });

        Assert.Contains("type='int32', offset=0, value=1000", script);
        Assert.Contains("-- int32 4B", script);
    }

    [Fact]
    public void BakedGenerate_FloatParam_RendersDecimalLiteral()
    {
        var script = BakedScriptGenerator.Generate(
            "C", "F", 4,
            new[] { new BakedParamValue("Speed", "FloatProperty", 4, 0, "3.14") });

        Assert.Contains("type='float', offset=0, value=3.14", script);
        Assert.Contains("-- float 4B", script);
    }

    [Fact]
    public void BakedGenerate_BoolTrueVariants_AllRenderAs1()
    {
        foreach (var input in new[] { "true", "1", "yes", "on", "TRUE", "True" })
        {
            var script = BakedScriptGenerator.Generate(
                "C", "F", 1,
                new[] { new BakedParamValue("b", "BoolProperty", 1, 0, input) });
            Assert.Contains("type='bool', offset=0, value=1", script);
        }
    }

    [Fact]
    public void BakedGenerate_BoolFalseVariants_AllRenderAs0()
    {
        foreach (var input in new[] { "false", "0", "no", "off", "FALSE" })
        {
            var script = BakedScriptGenerator.Generate(
                "C", "F", 1,
                new[] { new BakedParamValue("b", "BoolProperty", 1, 0, input) });
            Assert.Contains("type='bool', offset=0, value=0", script);
        }
    }

    [Fact]
    public void BakedGenerate_ObjectPointer_RendersAsHexLiteral()
    {
        var script = BakedScriptGenerator.Generate(
            "C", "F", 8,
            new[] { new BakedParamValue("Target", "ObjectProperty", 8, 0,
                "0x7FF6CD120000") });

        // Helper uses 'pointer' type for object/class/name/soft/weak/lazy/iface/uint64
        Assert.Contains("type='pointer', offset=0, value=0x7FF6CD120000",
                        script);
    }

    [Fact]
    public void BakedGenerate_ZeroPointer_RendersAsPlainZero()
    {
        // 0 looks cleaner than 0x0; the helper's writeQword accepts both.
        var script = BakedScriptGenerator.Generate(
            "C", "F", 8,
            new[] { new BakedParamValue("Target", "ObjectProperty", 8, 0, "0") });
        Assert.Contains("type='pointer', offset=0, value=0", script);
    }

    [Fact]
    public void BakedGenerate_NegativeInt_PreservesSign()
    {
        var script = BakedScriptGenerator.Generate(
            "C", "RemoveMoney", 4,
            new[] { new BakedParamValue("Delta", "IntProperty", 4, 0, "-1000") });

        Assert.Contains("type='int32', offset=0, value=-1000", script);
    }

    [Fact]
    public void BakedGenerate_HexInputForInt_PreservesHexForm()
    {
        // User typed 0xFF for an enum -- preserve the hex form so it's
        // self-documenting in the generated script.
        var script = BakedScriptGenerator.Generate(
            "C", "F", 4,
            new[] { new BakedParamValue("Mask", "IntProperty", 4, 0, "0xFF") });

        Assert.Contains("type='int32', offset=0, value=0xFF", script);
    }

    [Fact]
    public void BakedGenerate_MultipleParams_AllRenderedAtCorrectOffsets()
    {
        var values = new[]
        {
            new BakedParamValue("Amount",     "IntProperty",   4, 0, "1000"),
            new BakedParamValue("bShowToast", "BoolProperty",  1, 4, "true"),
            new BakedParamValue("Source",     "ObjectProperty",8, 8, "0xDEADBEEF"),
        };
        var script = BakedScriptGenerator.Generate("Player_C", "AddMoney", 16, values);

        Assert.Contains("type='int32', offset=0, value=1000", script);
        Assert.Contains("type='bool', offset=4, value=1", script);
        Assert.Contains("type='pointer', offset=8, value=0xDEADBEEF", script);
        Assert.Contains("invokeUFunction('Player_C', 'AddMoney', 16, PARAMS)", script);
    }

    [Fact]
    public void BakedGenerate_FlattenedStructSubFields_AllRendered()
    {
        // The dialog is responsible for flattening structs into
        // BakedParamValue entries; the generator just sees scalars at
        // the absolute offsets. Verify a 3-field FVector style input
        // emits 3 rows with parent.sub names.
        var values = new[]
        {
            new BakedParamValue("Location.X", "FloatProperty", 4, 16, "100.5"),
            new BakedParamValue("Location.Y", "FloatProperty", 4, 20, "200.5"),
            new BakedParamValue("Location.Z", "FloatProperty", 4, 24, "0"),
        };
        var script = BakedScriptGenerator.Generate("Pawn_C", "Teleport", 28, values);

        Assert.Contains("name='Location.X'", script);
        Assert.Contains("name='Location.Y'", script);
        Assert.Contains("name='Location.Z'", script);
        Assert.Contains("offset=16, value=100.5", script);
        Assert.Contains("offset=20, value=200.5", script);
        Assert.Contains("offset=24, value=0", script);
    }

    [Fact]
    public void BakedGenerate_UnparseableInput_EmitsTodoMarkerNotMangledLiteral()
    {
        // User typed garbage like "see comment" -- generator should not
        // silently emit "see comment" as a Lua expression. The
        // --[[unparsed:...]] 0 fallback flags it explicitly.
        var script = BakedScriptGenerator.Generate(
            "C", "F", 4,
            new[] { new BakedParamValue("Amount", "IntProperty", 4, 0,
                "see TODO comment") });

        Assert.Contains("--[[unparsed:see TODO comment]] 0", script);
        // The user's text is preserved verbatim so they can find + fix it
        Assert.Contains("see TODO comment", script);
    }

    [Fact]
    public void BakedGenerate_ClassOrFuncWithApostrophe_EscapedInLuaLiteral()
    {
        // A class named "Player'sActor_C" would close the Lua single-quoted
        // string mid-name without escaping. EscapeLua adds a backslash.
        var script = BakedScriptGenerator.Generate(
            "Player'sActor_C", "O'Brien", 0, NoBakedValues);

        Assert.Contains(@"'Player\'sActor_C'", script);
        Assert.Contains(@"'O\'Brien'", script);
        // And the invoke call must still be valid
        Assert.Contains(@"invokeUFunction('Player\'sActor_C', 'O\'Brien'",
                        script);
    }

    [Fact]
    public void BakedGenerate_UnparseableContainsCommentClose_EscapedToAvoidEarlyTermination()
    {
        // If the user typed "]]" it would close the --[[...]] comment
        // early and the trailing "0" would become orphaned syntax.
        // EscapeLuaComment splits ]] into ] ] to neutralise the close.
        var script = BakedScriptGenerator.Generate(
            "C", "F", 4,
            new[] { new BakedParamValue("X", "IntProperty", 4, 0, "abc]]def") });

        Assert.Contains("--[[unparsed:abc] ]def]] 0", script);
        Assert.DoesNotContain("--[[unparsed:abc]]def", script);
    }

    // ==================================================================
    // BakedScriptGenerator.MapToHelperType -- type mapping table
    // ==================================================================

    [Theory]
    [InlineData("BoolProperty",         "bool")]
    [InlineData("ByteProperty",         "byte")]
    [InlineData("Int8Property",         "byte")]
    [InlineData("Int16Property",        "int16")]
    [InlineData("UInt16Property",       "int16")]
    [InlineData("IntProperty",          "int32")]
    [InlineData("UInt32Property",       "int32")]
    [InlineData("EnumProperty",         "int32")]
    [InlineData("Int64Property",        "int64")]
    [InlineData("UInt64Property",       "pointer")]
    [InlineData("FloatProperty",        "float")]
    [InlineData("DoubleProperty",       "double")]
    [InlineData("ObjectProperty",       "pointer")]
    [InlineData("ClassProperty",        "pointer")]
    [InlineData("NameProperty",         "pointer")]
    [InlineData("SoftObjectProperty",   "pointer")]
    [InlineData("WeakObjectProperty",   "pointer")]
    [InlineData("InterfaceProperty",    "pointer")]
    [InlineData("StructProperty",       "fstruct")]
    [InlineData("StrProperty",          "fstring")]
    [InlineData("TextProperty",         "ftext")]
    [InlineData("ArrayProperty",        "tarray")]
    [InlineData("MapProperty",          "tmap")]
    [InlineData("SetProperty",          "tset")]
    [InlineData("DelegateProperty",     "delegate")]
    [InlineData("UnknownProperty",      "int32")]  // genuine fallback
    public void BakedGenerate_MapToHelperType_AllKnownTypes(
        string ueType, string expectedHelper)
    {
        Assert.Equal(expectedHelper, BakedScriptGenerator.MapToHelperType(ueType));
    }

    [Theory]
    [InlineData("StrProperty",          true)]
    [InlineData("TextProperty",         true)]
    [InlineData("ArrayProperty",        true)]
    [InlineData("StructProperty",       true)]
    [InlineData("DelegateProperty",     true)]
    [InlineData("IntProperty",          false)]
    [InlineData("DoubleProperty",       false)]
    [InlineData("ObjectProperty",       false)]
    public void BakedGenerate_IsComplexReturnType_ClassifiesCorrectly(
        string ueType, bool expectedComplex)
    {
        Assert.Equal(expectedComplex, BakedScriptGenerator.IsComplexReturnType(ueType));
    }

    [Fact]
    public void BakedGenerate_VerifyReturnOn_FStringReturn_PrintsHexDumpHint()
    {
        // FString return: helper has no scalar decoder, so verify mode
        // emits a hint pointing at the After: dump instead of pretending
        // to read a 4-byte int from a 16-byte FString header.
        var script = BakedScriptGenerator.Generate(
            "KismetSystemLibrary", "GetGameName", 16,
            Array.Empty<BakedParamValue>(),
            returnParam: new BakedParamValue("ReturnValue", "StrProperty", 16, 0, ""),
            verifyReturn: true);

        Assert.DoesNotContain("readUFunctionReturn", script);
        Assert.Contains("complex return; see After: dump above", script);
        Assert.Contains("(fstring@0, size=16B)", script);
    }

    // ==================================================================
    // HelperLuaResource -- embedded resource discovery
    // ==================================================================

    [Fact]
    public void HelperLuaResource_Read_ReturnsNonEmptyContent()
    {
        var content = HelperLuaResource.Read();
        Assert.NotNull(content);
        Assert.NotEmpty(content);
        // Sentinel string from the helper -- if this fails the embedded
        // resource was replaced with the wrong file.
        Assert.Contains("ue5_invoke_helper.lua v", content);
    }

    [Fact]
    public void HelperLuaResource_Read_ContainsRequiredPublicAPI()
    {
        var content = HelperLuaResource.Read();
        // The two functions the generator's output depends on
        Assert.Contains("function invokeUFunction(", content);
        Assert.Contains("function readUFunctionReturn(", content);
        // Re-declaration guard pattern
        Assert.Contains("if not invokeUFunction then", content);
        Assert.Contains("registerLuaFunctionHighlight('invokeUFunction')", content);
    }

    // ==================================================================
    // BakedScriptGenerator -- Verify Return Value toggle
    //
    // The toggle on InvokeParamDialog flips two behaviours in the
    // generator: emit a Before/After raw-byte dump + decoded return print,
    // and skip the synchronize-close so the user can read the print.
    // Default (verify=false) MUST keep the silent-on-success / auto-close
    // contract -- the production "ship a one-shot cheat" flow depends on
    // that to not leave engine windows open.
    // ==================================================================

    [Fact]
    public void BakedGenerate_VerifyReturnOff_SilentOnSuccessAndAutoCloses()
    {
        var script = BakedScriptGenerator.Generate(
            "C", "F", 16,
            new[] { new BakedParamValue("A", "DoubleProperty", 8, 0, "8") },
            returnParam: new BakedParamValue("ReturnValue", "DoubleProperty", 8, 8, ""),
            verifyReturn: false);

        // Default: no diagnostic dump, no decoded print on success
        Assert.DoesNotContain("Before", script);
        Assert.DoesNotContain("readUFunctionReturn", script);
        // Auto-close branch present
        Assert.Contains("synchronize(function() getLuaEngine().Close() end)",
                        script);
    }

    [Fact]
    public void BakedGenerate_VerifyReturnOn_EmitsDumpAndDecodedPrintAndKeepsEngineOpen()
    {
        var script = BakedScriptGenerator.Generate(
            "KismetMathLibrary", "exp", 16,
            new[] { new BakedParamValue("A", "DoubleProperty", 8, 0, "8") },
            returnParam: new BakedParamValue("ReturnValue", "DoubleProperty", 8, 8, ""),
            verifyReturn: true);

        // Before/After dump scaffolding
        Assert.Contains("local _mb_dbg", script);
        Assert.Contains("UE5_INVOKE_PARAMS_OFFSET", script);
        Assert.Contains("_dumpHex('[Invoke] Before')", script);
        Assert.Contains("_dumpHex('[Invoke] After ')", script);
        // Robust mailbox resolution: mirror helper's getAddressSafe + module-
        // prefixed fallback. Bare getAddress() returned garbage on the user's
        // CE setup which then crashed the dump with format(nil). Don't
        // regress this -- both code paths must remain.
        Assert.Contains("getAddressSafe('g_invokeMailbox')", script);
        Assert.Contains("getAddressSafe('UE5Dumper.g_invokeMailbox')", script);
        // Nil-guard on individual byte reads so a single unreadable byte
        // doesn't crash the whole dump call.
        Assert.Contains("readByte(_PD_dbg + i) or 0", script);
        // Friendly fallback when symbol resolution fails entirely.
        Assert.Contains("mailbox unresolved", script);

        // Decoded read uses the supplied offset + helper type
        Assert.Contains("readUFunctionReturn(8, 'double')", script);
        // Print line includes class::func + the param's display label
        Assert.Contains("[Invoke] OK: KismetMathLibrary::exp", script);
        Assert.Contains("ReturnValue (double@8)", script);
        // Float-style format spec for double
        Assert.Contains("%.10g", script);

        // Auto-close branch suppressed -- engine stays open for the user
        // to read the print output.
        Assert.DoesNotContain("getLuaEngine().Close()", script);
        // Memrec disable still happens (so the row stops re-firing)
        Assert.Contains("if memrec then memrec.Active = false end", script);
    }

    [Fact]
    public void BakedGenerate_VerifyReturnOn_VoidReturn_PrintsCompletionWithoutRead()
    {
        var script = BakedScriptGenerator.Generate(
            "C", "DoStuff", 4,
            new[] { new BakedParamValue("Flag", "BoolProperty", 1, 0, "true") },
            returnParam: null,
            verifyReturn: true);

        // No read call (no return slot to read)
        Assert.DoesNotContain("readUFunctionReturn", script);
        // Still prints a completion notice on success
        Assert.Contains("(void return)", script);
        Assert.DoesNotContain("getLuaEngine().Close()", script);
    }

    [Fact]
    public void BakedGenerate_VerifyReturnOn_PointerReturn_TranslatesToQword()
    {
        // Helper's readUFunctionReturn doesn't recognise 'pointer' (would
        // default to int32 = 4-byte read on the 8-byte slot). Generator
        // sends 'qword' on the wire while still showing 'pointer' as the
        // display label, and renders the value as 0x%X.
        var script = BakedScriptGenerator.Generate(
            "C", "GetPlayer", 8,
            Array.Empty<BakedParamValue>(),
            returnParam: new BakedParamValue("ReturnValue", "ObjectProperty", 8, 0, ""),
            verifyReturn: true);

        Assert.Contains("readUFunctionReturn(0, 'qword')", script);
        Assert.Contains("(pointer@0)", script);
        Assert.Contains("0x%X", script);
    }

    [Fact]
    public void BakedGenerate_VerifyReturnOn_DumpWindowCappedAt32Bytes()
    {
        // Big parmsSize (struct return etc.) shouldn't flood the engine
        // output with a 200-byte hex dump per invoke. Cap is min(parmsSize, 32).
        var script = BakedScriptGenerator.Generate(
            "C", "F", parmsSize: 200,
            Array.Empty<BakedParamValue>(),
            returnParam: null,
            verifyReturn: true);

        Assert.Contains("local _DUMP_LEN = 32", script);
    }

    [Fact]
    public void BakedGenerate_VerifyReturnOn_DumpWindowFloorIs8()
    {
        // Tiny single-byte param shouldn't degenerate to a 1-byte dump
        // -- floor at 8 keeps the line readable.
        var script = BakedScriptGenerator.Generate(
            "C", "F", parmsSize: 1,
            Array.Empty<BakedParamValue>(),
            returnParam: null,
            verifyReturn: true);

        Assert.Contains("local _DUMP_LEN = 8", script);
    }
}
