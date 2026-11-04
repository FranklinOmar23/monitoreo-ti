using FluentAssertions;
using MonitoreoTI.Application.Remote;

namespace MonitoreoTI.Application.Tests;

public sealed class CommandSigningTests
{
    /// <summary>El mismo vector está en agent/src/service/executor.test.ts: si cambia aquí, el agente rechazará todo.</summary>
    [Fact]
    public void The_signed_text_is_stable_and_carries_the_content_hash()
    {
        var payload = CommandSigning.CommandPayload(7, 3, "script", "powershell", 60, "2026-10-31T12:00:00Z", "Get-Date");

        payload.Should().Be(
            "monitoreoti/comando/v1\n7\n3\nscript\npowershell\n60\n2026-10-31T12:00:00Z\n"
            + "c1d9af5ecced5022dd0e037ef3d6e96a33b43dea902d1c05c894b7b33cb3c798");
    }

    [Fact]
    public void Timestamps_are_utc_without_fractions()
    {
        CommandSigning.Timestamp(new DateTime(2026, 10, 31, 12, 5, 9, 450, DateTimeKind.Utc)).Should().Be("2026-10-31T12:05:09Z");
    }

    [Theory]
    [InlineData(null, "Get-Service\nRestart-Service Spooler", "Get-Service")]
    [InlineData("Limpiar temporales", "x", "Limpiar temporales")]
    public void A_run_is_titled_by_its_script_or_its_first_line(string? script, string content, string expected) =>
        RemoteRules.Title(script, content).Should().Be(expected);
}
