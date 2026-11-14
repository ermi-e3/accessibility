using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Infrastructure.Health;
using Accessibility.Infrastructure.Identity;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Storage;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Accessibility.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Database' is required.");
        }

        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AccessLearnDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null)));

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AccessLearnDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "accesslearn.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.LoginPath = "/account/login";
            options.AccessDeniedPath = "/account/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = async context =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(context);

                var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = context.Principal is null ? null : await userManager.GetUserAsync(context.Principal);
                if (user?.IsActive == true)
                {
                    return;
                }

                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(
                AccessLearnPolicies.EducatorOrAdministrator,
                policy => policy.RequireRole(AccessLearnRoles.Educator, AccessLearnRoles.Administrator))
            .AddPolicy(
                AccessLearnPolicies.AdministratorOnly,
                policy => policy.RequireRole(AccessLearnRoles.Administrator));

        services
            .AddOptions<BootstrapAdminOptions>()
            .Bind(configuration.GetSection(BootstrapAdminOptions.SectionName));

        services
            .AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection(ObjectStorageOptions.SectionName))
            .Validate(
                options => Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var uri) &&
                           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
                "ObjectStorage:ServiceUrl must be an absolute HTTP or HTTPS URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.AccessKey), "ObjectStorage:AccessKey is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.SecretKey), "ObjectStorage:SecretKey is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.BucketName), "ObjectStorage:BucketName is required.")
            .Validate(
                options => options.CorsAllowedOrigins.Length > 0 && options.CorsAllowedOrigins.All(origin =>
                    origin == "*" ||
                    (Uri.TryCreate(origin.TrimEnd('/'), UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
                     uri.PathAndQuery == "/")),
                "ObjectStorage:CorsAllowedOrigins must contain '*' or valid HTTPS/loopback origins.")
            .Validate(
                options => options.DefaultReadUrlLifetimeMinutes is >= 1 and <= 10_080,
                "ObjectStorage:DefaultReadUrlLifetimeMinutes must be between 1 and 10080.")
            .ValidateOnStart();
        services
            .AddOptions<ObjectStorageProvisioningOptions>()
            .Bind(configuration.GetSection(ObjectStorageProvisioningOptions.SectionName));

        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ObjectStorageOptions>>().Value;
            return new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                S3ClientConfiguration.Create(options));
        });
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        services.AddSingleton<ObjectStorageProvisioner>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                "database",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(5))
            .AddCheck<ObjectStorageHealthCheck>(
                "object-storage",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
