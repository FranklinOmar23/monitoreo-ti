using FluentAssertions;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Application.Tests;

public class AgentTokensTests
{
    [Fact]
    public void Generated_token_carries_the_company_and_a_hash_of_the_whole_token()
    {
        var (token, hash) = AgentTokens.Generate(7);

        token.Should().StartWith("7.");
        hash.Should().Be(AgentTokens.Hash(token)).And.MatchRegex("^[0-9a-f]{64}$");
        AgentTokens.TryGetCompanyId(token, out var companyId).Should().BeTrue();
        companyId.Should().Be(7);
    }

    [Fact]
    public void Tokens_are_unique()
    {
        AgentTokens.Generate(1).Token.Should().NotBe(AgentTokens.Generate(1).Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sin-punto")]
    [InlineData(".secreto")]
    [InlineData("1.")]
    [InlineData("0.secreto")]
    [InlineData("-3.secreto")]
    [InlineData("abc.secreto")]
    [InlineData("99999999999.secreto")]
    public void Malformed_tokens_are_rejected(string? token)
    {
        AgentTokens.TryGetCompanyId(token, out _).Should().BeFalse();
    }

    [Fact]
    public void Oversized_tokens_are_rejected()
    {
        AgentTokens.TryGetCompanyId("1." + new string('a', AgentTokens.MaxLength), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AA:BB:CC:DD:EE:FF")]
    public void Mac_is_normalized(string input, string expected)
    {
        MacAddress.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("AA:BB:CC:DD:EE:GG")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA.BB.CC.DD.EE.FF")]
    public void Invalid_mac_is_rejected(string? input)
    {
        MacAddress.TryNormalize(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Validator_requires_the_essential_fields()
    {
        var validator = new EnrollRequestValidator();
        var valid = new EnrollRequest("1.x", "CONTA-03", "3C:52:82:A1:00:03", null, null, null, null, null, null, "0.1.0");

        validator.Validate(valid).IsValid.Should().BeTrue();
        validator.Validate(valid with { Hostname = "" }).IsValid.Should().BeFalse();
        validator.Validate(valid with { MacAddress = "no-es-mac" }).IsValid.Should().BeFalse();
        validator.Validate(valid with { AgentVersion = "" }).IsValid.Should().BeFalse();
    }
}
