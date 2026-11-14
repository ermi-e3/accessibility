namespace Accessibility.Application.Authorization;

public static class AccessLearnRoles
{
    public const string Educator = "Educator";
    public const string Administrator = "Administrator";
}

public static class AccessLearnPolicies
{
    public const string EducatorOrAdministrator = "EducatorOrAdministrator";
    public const string AdministratorOnly = "AdministratorOnly";

    public const string LessonsRead = "LessonsRead";

    public const string LessonsWrite = "LessonsWrite";

    public const string ResourcesWrite = "ResourcesWrite";
}
