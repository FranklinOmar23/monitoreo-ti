using FluentAssertions;
using MonitoreoTI.Application.Abstractions;

namespace MonitoreoTI.Application.Tests;

public class TenantContextTests
{
    [Fact]
    public void New_context_has_no_company_and_is_not_superadmin()
    {
        var tenant = new TenantContext();

        tenant.CompanyId.Should().BeNull();
        tenant.IsSuperAdmin.Should().BeFalse();
        tenant.IsSet.Should().BeFalse();
    }

    [Fact]
    public void Company_can_be_set_once()
    {
        var tenant = new TenantContext();

        tenant.SetCompany(7);

        tenant.CompanyId.Should().Be(7);
        tenant.IsSuperAdmin.Should().BeFalse();
        tenant.Invoking(t => t.SetCompany(8)).Should().Throw<InvalidOperationException>();
        tenant.CompanyId.Should().Be(7);
    }

    [Fact]
    public void Superadmin_cannot_switch_to_a_company_in_the_same_scope()
    {
        var tenant = new TenantContext();

        tenant.SetSuperAdmin();

        tenant.Invoking(t => t.SetCompany(1)).Should().Throw<InvalidOperationException>();
        tenant.CompanyId.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Company_id_must_be_positive(int companyId)
    {
        new TenantContext().Invoking(t => t.SetCompany(companyId)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
