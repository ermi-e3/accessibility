namespace Accessibility.Application.Authorization;

public static class AccessLearnScopes
{
    public const string LessonsRead = "lessons:read";
    public const string LessonsWrite = "lessons:write";
    public const string ResourcesWrite = "resources:write";

    public const string LessonsPublish = "lessons:publish";
}

public static class AccessLearnClaimTypes
{
    public const string IntegrationClientId = "accesslearn:integration_client_id";
    public const string Scope = "scope";
}
