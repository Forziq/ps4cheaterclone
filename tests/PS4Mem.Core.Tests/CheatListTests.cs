using PS4Mem.Core.Cheats;
using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Tests;

/// <summary>Cheat lists per title ID: the file store, the file format and add/edit/delete (T1.6 m1).</summary>
public sealed class CheatListTests : IDisposable
{
    private const string Title = "CUSA11711";
    private readonly string _dir = Directory.CreateTempSubdirectory("ps4mem-cheats-").FullName;
    private readonly CheatStore _store;
    private readonly CheatList _list;

    public CheatListTests()
    {
        _store = new CheatStore(Path.Combine(_dir, "cheats"));
        _list = new CheatList(_store);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("CUSA11711", "CUSA11711")]
    [InlineData(" cusa11711 ", "CUSA11711")]
    [InlineData("LAPY-2001_1", "LAPY-2001_1")]
    public void Title_ids_are_upper_cased(string input, string expected) =>
        Assert.Equal(expected, CheatStore.NormalizeTitleId(input));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("../settings")]
    [InlineData("CUSA 11711")]
    [InlineData("a/b")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456")] // 33 characters
    public void Bad_title_ids_are_refused_so_they_can_never_leave_the_folder(string? titleId)
    {
        Assert.Throws<ArgumentException>(() => CheatStore.NormalizeTitleId(titleId));
        Assert.Throws<ArgumentException>(() => _list.Get(titleId!));
    }

    [Fact]
    public void A_game_without_a_file_has_an_empty_list_and_no_file_is_made()
    {
        var table = _list.Get("cusa11711");

        Assert.Equal(Title, table.TitleId);
        Assert.Empty(table.Cheats);
        Assert.False(Directory.Exists(_store.Root));
        Assert.Empty(_store.TitleIds());
    }

    [Fact]
    public void Add_saves_the_cheat_in_the_title_id_file_with_a_canonical_value()
    {
        var cheat = _list.Add(Title, new CheatInput(" Money ", "0x2001A3F10", "u32", "0x3E7", Hex: false,
            Section: "", SectionStart: "0x200000000", Game: "ac3_engine_orbis_fn.elf"));

        Assert.Matches("^[0-9a-f]{8}$", cheat.Id);
        Assert.Equal("Money", cheat.Description);
        Assert.Equal(0x2001A3F10UL, cheat.Address);
        Assert.Equal(ScanValueType.UInt32, cheat.Type);
        Assert.Equal("999", cheat.Value);
        Assert.Equal(0x1A3F10UL, cheat.SectionOffset);
        Assert.Equal([0xE7, 0x03, 0, 0], cheat.ValueBytes());

        Assert.True(File.Exists(Path.Combine(_store.Root, "CUSA11711.json")));
        Assert.Equal([Title], _store.TitleIds());
        var loaded = new CheatStore(_store.Root).Load(Title);
        Assert.Equal("ac3_engine_orbis_fn.elf", loaded.Game);
        Assert.Equal(cheat, Assert.Single(loaded.Cheats));
    }

    [Theory]
    [InlineData("f32", "1,5", false, "1.5")]
    [InlineData("f32", "3F800000", true, "1")]
    [InlineData("i16", "-2", false, "-2")]
    [InlineData("u8", "FF", true, "255")]
    [InlineData("f64", "0.1", false, "0.1")]
    [InlineData("bytes", "de ad BEEF", false, "DE AD BE EF")]
    [InlineData("string", "  Ezio ", false, "  Ezio ")]
    public void Values_are_stored_in_their_canonical_form(string type, string value, bool hex, string expected)
    {
        var cheat = _list.Add(Title, new CheatInput(Address: "1000", Type: type, Value: value, Hex: hex));
        Assert.Equal(expected, cheat.Value);
        Assert.Equal(cheat, _list.Get(Title).Find(cheat.Id));
    }

    [Theory]
    [InlineData(null, "u32", "1", typeof(ArgumentException))]
    [InlineData("zz", "u32", "1", typeof(ArgumentException))]
    [InlineData("0x1000", "u33", "1", typeof(ArgumentException))]
    [InlineData("0x1000", "u8", "256", typeof(FormatException))]
    [InlineData("0x1000", "u32", "", typeof(FormatException))]
    [InlineData("0x1000", "bytes", "12 ?? 34", typeof(FormatException))]
    public void Bad_cheats_are_refused_and_nothing_is_saved(string? address, string type, string value, Type error)
    {
        var e = Assert.ThrowsAny<Exception>(() => _list.Add(Title, new CheatInput("x", address, type, value)));
        Assert.IsAssignableFrom(error, e);
        Assert.Empty(_store.TitleIds());
    }

    [Fact]
    public void Update_changes_only_the_given_fields()
    {
        var cheat = _list.Add(Title, new CheatInput("Money", "0x2001A3F10", "u32", "999", SectionStart: "0x200000000", Section: "heap"));

        var changed = _list.Update(Title, cheat.Id, new CheatInput(Value: "FFFF", Hex: true));
        Assert.Equal(cheat with { Value = "65535" }, changed);

        changed = _list.Update(Title, cheat.Id, new CheatInput(Description: "Cash", Address: "0x2001A3F14", Section: "", SectionStart: ""));
        Assert.Equal(cheat with { Value = "65535", Description = "Cash", Address = 0x2001A3F14, Section = null, SectionStart = null }, changed);
        Assert.Null(changed.SectionOffset);
        Assert.Equal(changed, Assert.Single(_list.Get(Title).Cheats));
    }

    [Fact]
    public void A_new_type_keeps_the_old_value_text_when_it_fits()
    {
        var cheat = _list.Add(Title, new CheatInput("Ammo", "0x1000", "u32", "300"));

        Assert.Equal("300", _list.Update(Title, cheat.Id, new CheatInput(Type: "u16")).Value);
        Assert.Throws<FormatException>(() => _list.Update(Title, cheat.Id, new CheatInput(Type: "u8")));
        Assert.Equal(ScanValueType.UInt16, _list.Get(Title).Find(cheat.Id)!.Type);
        Assert.Equal("44", _list.Update(Title, cheat.Id, new CheatInput(Type: "u8", Value: "44")).Value);
    }

    [Fact]
    public void Delete_removes_one_cheat_and_unknown_ids_are_not_found()
    {
        var a = _list.Add(Title, new CheatInput("A", "0x1000", "u32", "1"));
        var b = _list.Add(Title, new CheatInput("B", "0x1004", "u32", "2"));
        Assert.NotEqual(a.Id, b.Id);

        Assert.Equal(a, _list.Delete(Title, a.Id));
        Assert.Equal([b], _list.Get(Title).Cheats);
        Assert.Throws<CheatNotFoundException>(() => _list.Delete(Title, a.Id));
        Assert.Throws<CheatNotFoundException>(() => _list.Update(Title, "nope", new CheatInput(Value: "1")));
    }

    [Fact]
    public void Each_game_has_its_own_file()
    {
        _list.Add("CUSA00001", new CheatInput("A", "0x1000", "u32", "1"));
        _list.Add("CUSA00002", new CheatInput("B", "0x2000", "u32", "2"));

        Assert.Equal(["CUSA00001", "CUSA00002"], _store.TitleIds());
        Assert.Equal("A", Assert.Single(_list.Get("CUSA00001").Cheats).Description);
        Assert.Equal("B", Assert.Single(_list.Get("CUSA00002").Cheats).Description);
    }

    [Fact]
    public void A_broken_file_is_reported_and_never_overwritten()
    {
        Directory.CreateDirectory(_store.Root);
        var path = _store.PathFor(Title);
        File.WriteAllText(path, "{ not json");

        var e = Assert.Throws<CheatFileException>(() => _list.Get(Title));
        Assert.Contains(path, e.Message);
        Assert.Throws<CheatFileException>(() => _list.Add(Title, new CheatInput("A", "0x1000", "u32", "1")));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void The_file_is_readable_json_with_hex_addresses()
    {
        _list.Add(Title, new CheatInput("Money", "0x2001A3F10", "u32", "999", SectionStart: "0x200000000", Section: ""));

        var json = File.ReadAllText(_store.PathFor(Title));
        Assert.Contains("\"format\": 1", json);
        Assert.Contains("\"titleId\": \"CUSA11711\"", json);
        Assert.Contains("\"address\": \"0x2001A3F10\"", json);
        Assert.Contains("\"sectionStart\": \"0x200000000\"", json);
        Assert.Contains("\"type\": \"u32\"", json);
        Assert.DoesNotContain("locked", json);
    }

    [Fact]
    public void Parse_fills_in_missing_and_repeated_ids_and_takes_the_title_id_given()
    {
        var table = CheatFormat.Parse("""
            { "cheats": [
                { "address": "1000", "type": "u32", "value": "1" },
                { "id": "aaaa0000", "address": "0x1004", "type": "i8", "value": "-1" },
                { "id": "aaaa0000", "address": "0x1008", "type": "f32", "value": "2.5" } ] }
            """, Title);

        Assert.Equal(Title, table.TitleId);
        Assert.Equal(3, table.Cheats.Select(c => c.Id).Distinct().Count());
        Assert.Equal("aaaa0000", table.Cheats[1].Id);
        Assert.Equal([0x1000UL, 0x1004, 0x1008], table.Cheats.Select(c => c.Address));
    }

    [Theory]
    [InlineData("""[1, 2]""", "Not a cheat file")]
    [InlineData("""{"format": 2, "cheats": []}""", "format 2")]
    [InlineData("""{"titleId": "CUSA00005", "cheats": []}""", "for CUSA00005, not CUSA11711")]
    [InlineData("""{"cheats": [{"description": "Money", "type": "u32", "value": "1"}]}""", "Cheat 1 ('Money'): the address is missing")]
    [InlineData("""{"cheats": [{"address": "1", "type": "u32", "value": "1"}, {"address": "1", "type": "u8", "value": "999"}]}""", "Cheat 2:")]
    [InlineData("""{"cheats": [{"address": "1", "value": "1"}]}""", "the type is missing")]
    public void Parse_refuses_bad_files_with_a_message_naming_the_problem(string json, string message)
    {
        var e = Assert.Throws<FormatException>(() => CheatFormat.Parse(json, Title));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Export_then_import_into_another_folder_gives_the_same_list()
    {
        _list.Add(Title, new CheatInput("Money", "0x2001A3F10", "u32", "999", SectionStart: "0x200000000", Section: "", Game: "AC3"));
        _list.Add(Title, new CheatInput("Name", "0x3000", "string", "Ezio"));
        var json = _list.Export("cusa11711");

        var other = new CheatList(new CheatStore(Path.Combine(_dir, "other")));
        var import = other.Import(Title, json, CheatImportMode.Merge);

        Assert.Equal((2, 0, 0), (import.Added, import.Replaced, import.Removed));
        Assert.Equal(_list.Get(Title).Cheats, other.Get(Title).Cheats);
        Assert.Equal("AC3", other.Get(Title).Game);
    }

    [Fact]
    public void Merge_adds_new_cheats_and_replaces_ones_with_the_same_id()
    {
        var money = _list.Add(Title, new CheatInput("Money", "0x1000", "u32", "999"));
        var json = $$"""
            { "titleId": "CUSA11711", "cheats": [
                { "id": "{{money.Id}}", "description": "Money 2", "address": "0x1000", "type": "u32", "value": "5" },
                { "description": "Ammo", "address": "0x2000", "type": "u16", "value": "99" } ] }
            """;

        var import = _list.Import(Title, json, CheatImportMode.Merge);
        Assert.Equal((1, 1, 0), (import.Added, import.Replaced, import.Removed));
        Assert.Equal(["Money 2", "Ammo"], _list.Get(Title).Cheats.Select(c => c.Description));

        // An exported file has every id, so importing it again adds nothing.
        var again = _list.Import(Title, _list.Export(Title), CheatImportMode.Merge);
        Assert.Equal((0, 2, 0), (again.Added, again.Replaced, again.Removed));
        Assert.Equal(2, _list.Get(Title).Cheats.Count);
    }

    [Fact]
    public void Replace_makes_the_file_the_whole_list()
    {
        _list.Add(Title, new CheatInput("Money", "0x1000", "u32", "999"));
        _list.Add(Title, new CheatInput("Ammo", "0x2000", "u32", "1"));

        var import = _list.Import(Title, """{"cheats": [{"address": "0x3000", "type": "f32", "value": "1.5"}]}""", CheatImportMode.Replace);

        Assert.Equal((1, 0, 2), (import.Added, import.Replaced, import.Removed));
        var cheat = Assert.Single(_list.Get(Title).Cheats);
        Assert.Equal(0x3000UL, cheat.Address);
        Assert.Equal("1.5", cheat.Value);
    }

    [Fact]
    public void A_bad_import_changes_nothing()
    {
        _list.Add(Title, new CheatInput("Money", "0x1000", "u32", "999"));
        var before = File.ReadAllText(_store.PathFor(Title));

        Assert.Throws<FormatException>(() => _list.Import(Title,
            """{"cheats": [{"address": "0x3000", "type": "u32", "value": "1"}, {"address": "0x3004", "type": "u8", "value": "300"}]}""",
            CheatImportMode.Replace));
        Assert.Throws<FormatException>(() => _list.Import(Title, """{"titleId": "CUSA00001", "cheats": []}""", CheatImportMode.Merge));
        Assert.Throws<FormatException>(() => _list.Import(Title, "not json", CheatImportMode.Merge));

        Assert.Equal(before, File.ReadAllText(_store.PathFor(Title)));
    }
}
