using FluentAssertions;
using MonitoreoTI.Application.Health;

namespace MonitoreoTI.Application.Tests;

/// <summary>Errores del equipo resueltos desde Centinela (decisión 91).</summary>
public class ErrorGroupRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);

    private static readonly KnownService[] Services =
    [
        new("MSSQLSERVER", "SQL Server (MSSQLSERVER)"),
        new("SQLSERVERAGENT", "Agente SQL Server (MSSQLSERVER)"),
        new("EnergyServer", "The Energy Server Service queencreek"),
        new("Fax", "Fax"),
        new("Spooler", "Cola de impresión"),
    ];

    [Fact]
    public void Sql_memory_errors_restart_the_instance_service_named_by_the_source()
    {
        var explanation = EventCatalog.Explain("MSSQLSERVER", 701);
        explanation!.Fix.Should().Be(EventFix.RestartService);
        ErrorGroupRules.ServiceFor(explanation, "MSSQLSERVER", "There is insufficient system memory…", Services).Should().Be("MSSQLSERVER");

        // Instancia con nombre: mismo diagnóstico; si el agente no informó ese servicio, no hay acción.
        EventCatalog.Explain("MSSQL$CONTABILIDAD", 17300)!.Title.Should().Be(explanation.Title);
        ErrorGroupRules.ServiceFor(EventCatalog.Explain("MSSQL$CONTABILIDAD", 17300), "MSSQL$CONTABILIDAD", null, Services).Should().BeNull();
    }

    [Fact]
    public void Crashed_service_is_found_by_its_display_name_in_the_message()
    {
        var explanation = EventCatalog.Explain("Service Control Manager", 7034);
        ErrorGroupRules.ServiceFor(explanation, "Service Control Manager",
                "The Energy Server Service queencreek service terminated unexpectedly. It has done this 105 time(s).", Services)
            .Should().Be("EnergyServer");
        // Windows en español: el mensaje trae el nombre visible igual.
        ErrorGroupRules.ServiceFor(explanation, "Service Control Manager",
                "El servicio Cola de impresión terminó de forma inesperada.", Services)
            .Should().Be("Spooler");
    }

    [Fact]
    public void Service_names_only_match_whole_words_and_unknown_services_give_no_action()
    {
        var explanation = EventCatalog.Explain("Service Control Manager", 7031);
        ErrorGroupRules.ServiceFor(explanation, "Service Control Manager", "The Faxing2 service terminated unexpectedly.", Services).Should().BeNull();
        ErrorGroupRules.ServiceFor(explanation, "Service Control Manager", "The Algo Raro service terminated unexpectedly.", Services).Should().BeNull();
        // Un error sin reparación en el catálogo nunca ofrece reiniciar.
        ErrorGroupRules.ServiceFor(EventCatalog.Explain("disk", 7), "disk", "Fax", Services).Should().BeNull();
    }

    [Fact]
    public void A_service_name_that_could_break_the_command_is_never_offered()
    {
        KnownService[] tricky = [new("Bad'Name", "Servicio raro")];
        ErrorGroupRules.ServiceFor(EventCatalog.Explain("Service Control Manager", 7034), "Service Control Manager",
            "The Servicio raro service terminated unexpectedly.", tricky).Should().BeNull();
    }

    [Fact]
    public void Resolved_hides_what_happened_before_and_reappears_after()
    {
        var mark = new ErrorGroupMark(5, "MSSQLSERVER", 701, Ignored: false, Now);
        ErrorGroupRules.Hides(mark, Now.AddMinutes(-1)).Should().BeTrue();
        ErrorGroupRules.Hides(mark, Now.AddMinutes(1)).Should().BeFalse();
        ErrorGroupRules.StateOf(mark, Now.AddMinutes(-1)).Should().Be(ErrorGroupRules.Resolved);
        ErrorGroupRules.StateOf(mark, Now.AddMinutes(1)).Should().Be(ErrorGroupRules.Reappeared);
        ErrorGroupRules.StateOf(null, Now).Should().Be(ErrorGroupRules.Open);
    }

    [Fact]
    public void Company_ignore_applies_to_every_machine_and_beats_a_machine_resolution()
    {
        ErrorGroupMark[] marks =
        [
            new(5, "DistributedCOM", 10016, Ignored: false, Now),
            new(null, "distributedcom", 10016, Ignored: true, Now.AddDays(-3)),
        ];
        ErrorGroupRules.MarkFor(marks, 5, "DistributedCOM", 10016)!.Ignored.Should().BeTrue();
        ErrorGroupRules.MarkFor(marks, 9, "DistributedCOM", 10016)!.MachineId.Should().BeNull();
        ErrorGroupRules.Hides(ErrorGroupRules.MarkFor(marks, 9, "DistributedCOM", 10016), Now.AddDays(1)).Should().BeTrue();
        // Otro código del mismo origen no queda ignorado.
        ErrorGroupRules.MarkFor(marks, 9, "DistributedCOM", 10010).Should().BeNull();
        // La marca resuelta de un equipo no vale en otro.
        ErrorGroupRules.MarkFor([new(5, "disk", 7, false, Now)], 9, "disk", 7).Should().BeNull();
    }

    [Fact]
    public void Restart_script_brings_back_running_dependents_and_rejects_bad_names()
    {
        var script = ServiceRepairRules.RestartScript("MSSQLSERVER");
        script.Should().Contain("Restart-Service -Name 'MSSQLSERVER' -Force").And.Contain("-DependentServices").And.Contain("| Start-Service");
        // No se confunde con el comando de "Iniciar servicio" de las alertas.
        ServiceRepairRules.ServiceFromScript(script).Should().BeNull();
        var act = () => ServiceRepairRules.RestartScript("x'; Remove-Item C:\\ -Recurse; '");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dcom_permission_warning_is_flagged_as_noise()
    {
        EventCatalog.Explain("Microsoft-Windows-DistributedCOM", 10016)!.Noise.Should().BeTrue();
        EventCatalog.Explain("MSSQLSERVER", 701)!.Noise.Should().BeFalse();
    }
}
