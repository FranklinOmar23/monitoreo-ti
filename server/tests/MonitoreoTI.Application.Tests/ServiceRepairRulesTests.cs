using FluentAssertions;
using MonitoreoTI.Application.Health;

namespace MonitoreoTI.Application.Tests;

public class ServiceRepairRulesTests
{
    [Theory]
    [InlineData("Spooler", true)]
    [InlineData("MSSQL$SQLEXPRESS", true)]
    [InlineData("Some Service.1", true)]
    [InlineData("x'; Remove-Item C:\\ -Recurse; '", false)]
    [InlineData("a\nb", false)]
    [InlineData("", false)]
    public void Only_plain_service_names_are_accepted(string name, bool valid)
    {
        ServiceRepairRules.IsValidName(name).Should().Be(valid);
    }

    [Fact]
    public void The_service_comes_from_the_alert_code()
    {
        ServiceRepairRules.ServiceFromAlertCode("servicio:Spooler").Should().Be("Spooler");
        ServiceRepairRules.ServiceFromAlertCode("disco:C:").Should().BeNull();
        ServiceRepairRules.ServiceFromAlertCode("servicio:mal'nombre").Should().BeNull();
        ServiceRepairRules.ServiceFromAlertCode(null).Should().BeNull();
    }

    [Fact]
    public void The_command_starts_the_service_and_can_be_recognized_later()
    {
        var script = ServiceRepairRules.Script("Spooler");

        script.Should().Contain("Start-Service -Name 'Spooler'");
        ServiceRepairRules.ServiceFromScript(script).Should().Be("Spooler");
        ServiceRepairRules.ServiceFromScript("Start-Service -Name 'Spooler'; Remove-Item x").Should().BeNull();
        FluentActions.Invoking(() => ServiceRepairRules.Script("x'y")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("sppsvc", true)]
    [InlineData("edgeupdate", true)]
    [InlineData("GoogleUpdaterService128.0", true)]
    [InlineData("Spooler", false)]
    public void Services_that_stop_by_themselves_are_not_problems(string name, bool stopsByItself)
    {
        ServiceRepairRules.StopsByItself(name).Should().Be(stopsByItself);
    }

    [Fact]
    public void A_stopped_self_stopping_service_opens_no_alert()
    {
        var machine = new MachineSnapshot(1, "PC", false, DateTime.UtcNow, [], [], [],
            [new StoppedService("sppsvc", "Software Protection"), new StoppedService("Spooler", "Cola de impresión")], []);

        var findings = HealthRules.Evaluate(machine, new RuleSet(new Dictionary<string, RuleThreshold>()), DateTime.UtcNow);

        findings.Where(f => f.Metric == "servicio_detenido").Select(f => f.Code).Should().Equal("servicio:Spooler");
    }
}
