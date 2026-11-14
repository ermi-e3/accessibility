using System.ComponentModel.DataAnnotations;
using Accessibility.Api.Controllers;
using Xunit;

namespace Accessibility.Foundation.Tests.Api;

public sealed class LoginRequestMetadataTests
{
    [Fact]
    public void ValidationAttributesAreAttachedToRecordConstructorParameters()
    {
        var constructor = typeof(LoginRequest).GetConstructors().Single();
        var parameters = constructor.GetParameters();

        Assert.Contains(parameters[0].GetCustomAttributes(false), attribute => attribute is RequiredAttribute);
        Assert.Contains(parameters[0].GetCustomAttributes(false), attribute => attribute is EmailAddressAttribute);
        Assert.Contains(parameters[1].GetCustomAttributes(false), attribute => attribute is RequiredAttribute);
        Assert.Empty(typeof(LoginRequest).GetProperty(nameof(LoginRequest.Email))!
            .GetCustomAttributes(typeof(ValidationAttribute), inherit: false));
        Assert.Empty(typeof(LoginRequest).GetProperty(nameof(LoginRequest.Password))!
            .GetCustomAttributes(typeof(ValidationAttribute), inherit: false));
    }
}
