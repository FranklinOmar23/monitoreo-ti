using FluentAssertions;
using FluentValidation.TestHelper;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Application.Tests;

public class FortiGateRulesTests
{
    [Theory]
    [InlineData("https://192.168.3.3:12443", "https://192.168.3.3:12443")]
    [InlineData("192.168.3.3:12443", "https://192.168.3.3:12443")]
    [InlineData("https://192.168.3.3:12443/", "https://192.168.3.3:12443")]
    [InlineData("https://192.168.3.3:443", "https://192.168.3.3")]
    [InlineData("  https://fgt.caribe.local ", "https://fgt.caribe.local")]
    [InlineData("http://10.0.0.1:8080", "http://10.0.0.1:8080")]
    public void Normaliza_la_direccion_de_la_api(string input, string expected) =>
        FortiGateRules.NormalizeUrl(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://192.168.3.3")]
    [InlineData("https://192.168.3.3:12443/api/v2/monitor")]
    [InlineData("https://192.168.3.3:12443/?access_token=x")]
    [InlineData("no es una url")]
    public void Rechaza_direcciones_que_no_son_la_raiz_https(string? input) =>
        FortiGateRules.NormalizeUrl(input).Should().BeNull();

    [Fact]
    public void Sugiere_la_direccion_con_el_puerto_de_administracion()
    {
        FortiGateRules.SuggestedUrl("192.168.3.3").Should().Be("https://192.168.3.3:12443");
        FortiGateRules.SuggestedUrl(null).Should().BeNull();
    }

    [Theory]
    [InlineData("h7k2m9p4q1w8e5r3t6y0u2i4o7p1as", true)]
    [InlineData("corto", false)]
    [InlineData("token con espacios en medio 123", false)]
    public void Valida_el_formato_del_token(string token, bool valid) =>
        FortiGateRules.IsValidToken(token).Should().Be(valid);

    [Theory]
    [InlineData("0.3.5", false)]
    [InlineData("0.3.6", true)]
    [InlineData("0.4.0-beta", true)]
    [InlineData(null, false)]
    public void La_api_necesita_el_agente_036(string? version, bool supports) =>
        FortiGateRules.SupportsApi(version).Should().Be(supports);

    [Fact]
    public void Arma_el_sistema_operativo_sin_repetir_la_version()
    {
        FortiGateRules.OperatingSystem("Windows", "10").Should().Be("Windows 10");
        FortiGateRules.OperatingSystem("Android 14", "14").Should().Be("Android 14");
        FortiGateRules.OperatingSystem(null, "10").Should().BeNull();
    }

    [Fact]
    public void El_formulario_valida_direccion_y_token_solo_si_vienen()
    {
        var validator = new ApiTestInputValidator();
        validator.TestValidate(new ApiTestInput(null, null)).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new ApiTestInput("ftp://x", null)).ShouldHaveValidationErrorFor(x => x.ApiUrl);
        validator.TestValidate(new ApiTestInput("https://192.168.3.3:12443", "mal token")).ShouldHaveValidationErrorFor(x => x.ApiToken);
    }
}
