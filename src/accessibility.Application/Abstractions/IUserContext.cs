namespace Accessibility.Application.Abstractions;

public interface IUserContext
{
    Guid? UserId { get; }

    Guid? IntegrationClientId { get; }

    bool IsAuthenticated { get; }

    bool IsIntegrationClient { get; }

    bool IsInRole(string role);

    bool HasScope(string scope);
}
