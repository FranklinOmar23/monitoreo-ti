using FluentAssertions;
using FluentValidation.TestHelper;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Application.Tests;

public class RemoteInstallRulesTests
{
    [Theory]
    [InlineData(@"CARIBE\soporte", true)]
    [InlineData("soporte@caribe.local", true)]
    [InlineData("soporte", false)]
    [InlineData(@"CARIBE\", false)]
    [InlineData(@"\soporte", false)]
    [InlineData(@"A\B\C", false)]
    [InlineData("so porte@caribe.local", false)]
    [InlineData("", false)]
    public void Exige_una_cuenta_del_dominio(string account, bool valid) =>
        RemoteInstallRules.IsDomainAccount(account).Should().Be(valid);

    [Theory]
    [InlineData("0.3.6", false)]
    [InlineData("0.3.7", true)]
    [InlineData(null, false)]
    public void Instalar_en_otros_equipos_necesita_el_agente_037(string? version, bool supports) =>
        RemoteInstallRules.SupportsInstall(version).Should().Be(supports);

    [Fact]
    public void El_recolector_solo_informa_estados_conocidos_y_registrado_lo_pone_el_servidor()
    {
        RemoteInstallRules.AgentStates.Should().Contain(["conectando", "copiando", "instalando", "instalado", "error", "omitido"]);
        RemoteInstallRules.AgentStates.Should().NotContain("registrado");
        RemoteInstallRules.IsFinal("registrado").Should().BeTrue();
        RemoteInstallRules.IsFinal("instalado").Should().BeFalse();
    }

    [Fact]
    public void El_pedido_valida_ips_tope_y_cuenta()
    {
        var validator = new RemoteInstallInputValidator();
        validator.TestValidate(new RemoteInstallInput(["192.168.4.6"], @"CARIBE\soporte", "x")).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new RemoteInstallInput([], @"CARIBE\soporte", "x")).ShouldHaveValidationErrorFor(x => x.Ips);
        validator.TestValidate(new RemoteInstallInput(["no-ip"], @"CARIBE\soporte", "x")).ShouldHaveAnyValidationError();
        validator.TestValidate(new RemoteInstallInput(Enumerable.Range(1, 51).Select(i => $"10.0.0.{i}").ToList(), @"CARIBE\soporte", "x"))
            .ShouldHaveValidationErrorFor(x => x.Ips);
        validator.TestValidate(new RemoteInstallInput(["192.168.4.6"], "soporte", "x")).ShouldHaveValidationErrorFor(x => x.Cuenta);
        validator.TestValidate(new RemoteInstallInput(["192.168.4.6"], @"CARIBE\soporte", "")).ShouldHaveValidationErrorFor(x => x.Clave);
    }
}

public class ClassicAgentVersionTests
{
    [Theory]
    [InlineData("0.1.0-clasico", "0.3.8", false)]
    [InlineData("0.3.6", "0.3.8", true)]
    [InlineData("0.3.8", "0.3.8", false)]
    public void El_agente_clasico_nunca_recibe_el_instalador_normal(string installed, string available, bool offered) =>
        MonitoreoTI.Application.Remote.VersionRules.IsOlder(installed, available).Should().Be(offered);

    [Fact]
    public void El_agente_clasico_no_recibe_funciones_de_recolector()
    {
        SnmpRules.SupportsPollNow("0.1.0-clasico").Should().BeFalse();
        IpRules.SupportsSweep("0.1.0-clasico").Should().BeFalse();
        FortiGateRules.SupportsApi("0.1.0-clasico").Should().BeFalse();
        RemoteInstallRules.SupportsInstall("0.1.0-clasico").Should().BeFalse();
    }
}
