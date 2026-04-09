using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Common.Helpers;
using Infrastructure.Common.Middleware.EndpointFilters;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Common.Helpers;

public class EndpointFilterHelperTests
{
    [Fact]
    public void GetAuthEndpointFilters_WithConfig_ReturnsTwoFilters()
    {
        var config = new HttpAuthorizationConfig { Enabled = true };

        var filters = EndpointFilterHelper.GetAuthEndpointFilters(config);

        filters.Should().HaveCount(2);
    }

    [Fact]
    public void GetAuthEndpointFilters_NullConfig_UsesDefaultsAndReturnsTwoFilters()
    {
        var filters = EndpointFilterHelper.GetAuthEndpointFilters(null);

        filters.Should().HaveCount(2);
    }

    [Fact]
    public void GetAuthEndpointFilters_NoArgs_UsesDefaultsAndReturnsTwoFilters()
    {
        var filters = EndpointFilterHelper.GetAuthEndpointFilters();

        filters.Should().HaveCount(2);
    }

    [Fact]
    public void GetAuthEndpointFilters_ReturnsErrorFilterFirst()
    {
        var filters = EndpointFilterHelper.GetAuthEndpointFilters();

        filters[0].Should().BeOfType<HttpErrorEndpointFilter>();
    }

    [Fact]
    public void GetAuthEndpointFilters_ReturnsAuthFilterSecond()
    {
        var filters = EndpointFilterHelper.GetAuthEndpointFilters();

        filters[1].Should().BeOfType<HttpAuthEndpointFilter>();
    }

    [Fact]
    public void GetAuthEndpointFilters_AllFiltersImplementIEndpointFilter()
    {
        var filters = EndpointFilterHelper.GetAuthEndpointFilters();

        filters.Should().AllBeAssignableTo<IEndpointFilter>();
    }

    [Fact]
    public void GetAuthEndpointFilters_WithEnabledConfig_ReturnsFunctionalFilters()
    {
        var config = new HttpAuthorizationConfig
        {
            Enabled = true,
            HttpHeaderName = "X-API-Key",
            AccessKey1 = "test-key",
        };

        var filters = EndpointFilterHelper.GetAuthEndpointFilters(config);

        filters.Should().HaveCount(2);
        filters[0].Should().BeOfType<HttpErrorEndpointFilter>();
        filters[1].Should().BeOfType<HttpAuthEndpointFilter>();
    }
}
