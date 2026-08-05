using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// The exported CE table must be well-formed XML no matter what the game's strings
/// contain (audit #4 B3).
///
/// <para>Description text is arbitrary game memory — TMap keys, TSet elements,
/// soft-object paths, DataTable row names. A single <c>&amp;</c> anywhere in it used
/// to produce an invalid entity reference, and Cheat Engine rejects the <b>whole
/// document</b>: a multi-thousand-entry export imports as nothing, with no indication
/// which record was at fault. A <c>TMap</c> key like <c>Bow &amp; Arrow</c> is the kind
/// of thing that hits this on an ordinary game.</para>
///
/// <para>These tests parse the output with <see cref="XDocument"/> rather than
/// string-matching for entities. Asserting on <c>"&amp;amp;"</c> would pass for output
/// that is still malformed somewhere else; asking a real parser is the property that
/// actually matters — and it is exactly the check nothing in this suite performed
/// before. <c>CheatTableBuilder</c> escaped its output, <c>CeXmlExportService</c> did
/// not, and no test noticed the difference.</para>
/// </summary>
public class CeXmlEscapingTests
{
    /// <summary>Every XML metacharacter, in one string, in a plausible shape.</summary>
    private const string Nasty = "Bow & Arrow <Rare> \"Legendary\" & 'Cursed'";

    private static LiveFieldValue NastyMapField(string key) => new()
    {
        Name = "LootTable",
        TypeName = "MapProperty",
        Offset = 0x50,
        Size = 80,
        MapCount = 1,
        MapKeyType = "StrProperty",
        MapValueType = "IntProperty",
        MapKeySize = 16,
        MapValueSize = 4,
        MapValueOffset = 16,
        MapDataAddr = "0x9000",
        MapElements = new List<ContainerElementValue>
        {
            new() { Index = 0, Key = key, Value = "100", ValueHex = "64000000" },
        },
    };

    private static string Xml(params LiveFieldValue[] fields) =>
        CeXmlExportService.GenerateInstanceXml(
            "\"Game.exe\"+1000", "MyObj", "UMyClass", fields);

    [Fact]
    public void An_ampersand_alone_breaks_an_unescaped_export()
    {
        // The minimal reproducer, kept first and separate so a regression names the
        // cause instead of pointing at the kitchen-sink string below.
        var doc = XDocument.Parse(Xml(NastyMapField("R&D")));
        Assert.NotNull(doc.Root);
    }

    [Fact]
    public void Every_xml_metacharacter_in_a_map_key_still_yields_well_formed_xml()
    {
        var doc = XDocument.Parse(Xml(NastyMapField(Nasty)));
        Assert.NotNull(doc.Root);
    }

    [Fact]
    public void The_text_survives_the_round_trip_unmangled()
    {
        // Escaping has to be reversible: the user must be able to read the key in CE.
        // A test that only asserted "the document parses" would also pass for output
        // that dropped or corrupted the characters instead of encoding them.
        var doc = XDocument.Parse(Xml(NastyMapField(Nasty)));
        var descriptions = doc.Descendants("Description").Select(d => d.Value).ToList();

        Assert.Contains(descriptions, v => v.Contains("Bow & Arrow"));
        Assert.Contains(descriptions, v => v.Contains("<Rare>"));
    }

    [Fact]
    public void A_less_than_in_a_key_cannot_open_a_phantom_element()
    {
        // '<' is the worse half: '&' yields an invalid entity, but '<' makes the parser
        // start reading a tag, so the damage is not even local to the record.
        var doc = XDocument.Parse(Xml(NastyMapField("HP < 50%")));
        Assert.NotNull(doc.Root);
        Assert.Contains(doc.Descendants("Description").Select(d => d.Value),
            v => v.Contains("HP < 50%"));
    }

    [Fact]
    public void The_hierarchical_emitter_escapes_too()
    {
        // A different code path with its own Description sites — escaping is not
        // supposed to be a property of one emitter.
        var xml = CeXmlExportService.GenerateHierarchicalXml(
            "\"Game.exe\"+1000", "MyObj",
            new List<UE5DumpUI.ViewModels.BreadcrumbItem>(),
            new[] { NastyMapField(Nasty) });

        Assert.NotNull(XDocument.Parse(xml).Root);
    }
}
