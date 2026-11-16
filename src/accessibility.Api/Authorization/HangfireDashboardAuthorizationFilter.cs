using Accessibility.Application.Authorization;
using Hangfire.Dashboard;

namespace Accessibility.Api.Authorization;

public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        context.GetHttpContext().User.IsInRole(AccessLearnRoles.Administrator);
}
