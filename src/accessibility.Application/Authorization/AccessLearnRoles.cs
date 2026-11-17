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

    public const string ResourcesRead = "ResourcesRead";

    public const string DocumentsWrite = "DocumentsWrite";

    public const string DocumentsRead = "DocumentsRead";

    public const string ProcessingRead = "ProcessingRead";

    public const string ProcessingWrite = "ProcessingWrite";

    public const string OfflineRead = "OfflineRead";

    public const string OfflineWrite = "OfflineWrite";

    public const string LessonsPublish = "LessonsPublish";
}
