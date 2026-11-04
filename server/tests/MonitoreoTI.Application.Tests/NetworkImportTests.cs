using FluentAssertions;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Application.Tests;

public class CsvTableTests
{
    [Fact]
    public void Headers_are_compared_without_accents_or_case()
    {
        var table = CsvTable.Parse("\uFEFFCódigo;Ubicación;Posición U\r\nP-1;Caja;3\r\n");
        table.Columns.Should().Equal("codigo", "ubicacion", "posicion_u");
        table.Rows.Single().Get("posicion_u").Should().Be("3");
    }

    [Fact]
    public void Quotes_keep_separators_and_line_breaks()
    {
        var table = CsvTable.Parse("codigo,notas\n\"P-1\",\"Uno, dos\"\"tres\"\"\nsigue\"\nP-2,ok\n");
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Get("notas").Should().Be("Uno, dos\"tres\"\nsigue");
        table.Rows[1].Line.Should().Be(4);
    }

    [Fact]
    public void Blank_lines_and_empty_values_are_skipped()
    {
        var table = CsvTable.Parse("codigo,ubicacion\n\nP-1,\n ,  \n");
        table.Rows.Should().ContainSingle();
        table.Rows[0].Get("ubicacion").Should().BeNull();
        table.Rows[0].Line.Should().Be(3);
    }

    [Fact]
    public void Tabs_are_detected() =>
        CsvTable.Parse("a\tb\n1\t2").Rows.Single().Get("b").Should().Be("2");
}

public class HistoryTextTests
{
    private const string Moved = """{"antes":{"nombre":"SW-1","gabineteId":5,"posicionU":38},"despues":{"nombre":"SW-1","gabineteId":6,"posicionU":20}}""";

    [Fact]
    public void Edits_list_only_what_changed_with_names()
    {
        var changes = HistoryText.Changes(Moved, (field, id) => field == "gabineteId" && id == "6" ? "GAB-B02" : null);
        changes.Should().BeEquivalentTo(new[]
        {
            new HistoryChange("Gabinete", "#5", "GAB-B02"),
            new HistoryChange("U", "38", "20"),
        });
    }

    [Fact]
    public void Creations_have_no_changes_and_use_the_saved_name()
    {
        const string created = """{"antes":null,"despues":{"desde":"PP-1 5","hasta":"Roseta P-2","etiqueta":"R1"}}""";
        HistoryText.Changes(created, (_, _) => null).Should().BeEmpty();
        HistoryText.Subject(created).Should().Be("PP-1 5 → Roseta P-2");
        HistoryText.Subject("""{"antes":{"marca":"Cisco","modelo":"C9300"},"despues":null}""").Should().Be("Cisco C9300");
    }

    [Fact]
    public void Unknown_actions_keep_their_key() => HistoryText.Title("x.y").Should().Be("x.y");
}
