using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Features.Lessons;
using Accessibility.Application.Features.Player;
using Accessibility.Application.Features.Processing;
using Accessibility.Application.Features.Offline;
using Accessibility.Application.Features.Documents;
using Accessibility.Infrastructure.Authorization;
using Accessibility.Infrastructure.Health;
using Accessibility.Infrastructure.Identity;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Processing;
using Accessibility.Infrastructure.Player;
using Accessibility.Infrastructure.Offline;
using Accessibility.Infrastructure.Documents;
using Accessibility.Infrastructure.Storage;
using Amazon.Runtime;
using Amazon.S3;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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
        var hangfireConnectionString = configuration.GetConnectionString("Hangfire");
        if (string.IsNullOrWhiteSpace(hangfireConnectionString))
        {
            throw new InvalidOperationException("Connection string 'Hangfire' is required.");
        }
        var prepareHangfireSchema = configuration.GetValue("Hangfire:PrepareSchemaIfNecessary", false);

        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AccessLearnDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddHangfire(configuration => configuration
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(hangfireConnectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = "hangfire",
                    PrepareSchemaIfNecessary = prepareHangfireSchema,
                    QueuePollInterval = TimeSpan.FromSeconds(5)
                }));
        var processingRuntime = configuration
            .GetSection(ProcessingRuntimeOptions.SectionName)
            .Get<ProcessingRuntimeOptions>() ?? new ProcessingRuntimeOptions();
        if (processingRuntime.WorkerEnabled)
        {
            services.AddHangfireServer(options =>
            {
                options.WorkerCount = processingRuntime.WorkerCount;
                options.Queues = ["processing"];
                options.ServerName = $"accesslearn-processing-{Environment.MachineName}";
            });
        }

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
                policy => policy.RequireRole(AccessLearnRoles.Administrator))
            .AddPolicy(AccessLearnPolicies.LessonsRead, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.LessonsRead));
            })
            .AddPolicy(AccessLearnPolicies.LessonsWrite, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.LessonsWrite));
            })
            .AddPolicy(AccessLearnPolicies.ResourcesWrite, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.ResourcesWrite));
            })
            .AddPolicy(AccessLearnPolicies.ResourcesRead, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.ResourcesRead));
            })
            .AddPolicy(AccessLearnPolicies.DocumentsWrite, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.DocumentsWrite));
            })
            .AddPolicy(AccessLearnPolicies.DocumentsRead, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.DocumentsRead));
            })
            .AddPolicy(AccessLearnPolicies.ProcessingRead, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.ProcessingRead));
            })
            .AddPolicy(AccessLearnPolicies.ProcessingWrite, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.ProcessingWrite));
            })
            .AddPolicy(AccessLearnPolicies.OfflineRead, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.OfflineRead));
            })
            .AddPolicy(AccessLearnPolicies.OfflineWrite, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.OfflineWrite));
            })
            .AddPolicy(AccessLearnPolicies.LessonsPublish, policy =>
            {
                policy.AddAuthenticationSchemes(AccessLearnAuthenticationSchemes.Management);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeOrRoleRequirement(AccessLearnScopes.LessonsPublish));
            });
        services.AddSingleton<IAuthorizationHandler, ScopeOrRoleHandler>();

        services
            .AddOptions<IntegrationApiKeyOptions>()
            .Bind(configuration.GetSection(IntegrationApiKeyOptions.SectionName))
            .Validate(options => options.Pepper.Length >= 32, "ApiKeyAuthentication:Pepper must contain at least 32 characters.")
            .ValidateOnStart();
        services.AddScoped<IIntegrationApiKeyService, IntegrationApiKeyService>();

        services
            .AddOptions<FfprobeOptions>()
            .Bind(configuration.GetSection(FfprobeOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ExecutablePath), "MediaProbe:ExecutablePath is required.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 300, "MediaProbe:TimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();
        services
            .AddOptions<UploadQuarantineOptions>()
            .Bind(configuration.GetSection(UploadQuarantineOptions.SectionName))
            .Validate(
                options => options.MaximumAgeMinutes is >= 1 and <= 10_080,
                "LessonIngest:MaximumAgeMinutes must be between 1 and 10080.")
            .ValidateOnStart();
        services.AddSingleton<TemporaryUploadQuarantine>();
        services.AddSingleton<IUploadQuarantine>(provider =>
            provider.GetRequiredService<TemporaryUploadQuarantine>());
        services.AddHostedService<UploadQuarantineCleanupService>();
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.AddScoped<ILessonIngestService, LessonIngestService>();
        services
            .AddOptions<MediaProcessingOptions>()
            .Bind(configuration.GetSection(MediaProcessingOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.FfmpegPath), "MediaProcessing:FfmpegPath is required.")
            .Validate(options => options.TimeoutMinutes is >= 1 and <= 120, "MediaProcessing:TimeoutMinutes must be between 1 and 120.")
            .ValidateOnStart();
        services
            .AddOptions<ProcessingRuntimeOptions>()
            .Bind(configuration.GetSection(ProcessingRuntimeOptions.SectionName))
            .Validate(options => options.WorkerCount is >= 1 and <= 32, "Processing:WorkerCount must be between 1 and 32.")
            .Validate(
                options => options.WorkspaceMaximumAgeMinutes is >= 30 and <= 10_080,
                "Processing:WorkspaceMaximumAgeMinutes must be between 30 and 10080.")
            .ValidateOnStart();
        services
            .AddOptions<WhisperOptions>()
            .Bind(configuration.GetSection(WhisperOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ExecutablePath), "Whisper:ExecutablePath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ModelPath), "Whisper:ModelPath is required.")
            .Validate(options => options.Threads is >= 1 and <= 64, "Whisper:Threads must be between 1 and 64.")
            .Validate(options => options.TimeoutMinutes is >= 1 and <= 120, "Whisper:TimeoutMinutes must be between 1 and 120.")
            .ValidateOnStart();
        services
            .AddOptions<GoogleTranslationOptions>()
            .Bind(configuration.GetSection(GoogleTranslationOptions.SectionName))
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ProjectId),
                "GoogleTranslation:ProjectId is required when Google Translation is enabled.")
            .ValidateOnStart();
        services.AddSingleton<IMediaProcessingProvider, FfmpegMediaProcessingProvider>();
        services.AddSingleton<ITranscriptionProvider, WhisperCppTranscriptionProvider>();
        services.AddSingleton<ITranslationProvider, GoogleCloudTranslationProvider>();
        services.AddSingleton<ITranscriptFormatService, TranscriptFormatService>();
        services.AddSingleton<IDocxPackageSafetyValidator, DocxPackageSafetyValidator>();
        services.AddSingleton<IOpenXmlDocumentNormalizer, OpenXmlDocumentNormalizer>();
        services.AddSingleton<ISemanticHtmlDocumentRenderer, SemanticHtmlDocumentRenderer>();
        services.AddOptions<DocumentProcessingOptions>()
            .Bind(configuration.GetSection(DocumentProcessingOptions.SectionName));
        services.AddSingleton<IDocumentPdfToolchain, DocumentPdfToolchain>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IDocumentJobExecutor, DocumentJobExecutor>();
        services.AddScoped<IBackgroundJobScheduler, HangfireProcessingJobScheduler>();
        services.AddScoped<IProcessingService, ProcessingService>();
        services.AddScoped<ProcessingJobRunner>();
        services.AddSingleton<ProcessingCancellationRegistry>();
        if (processingRuntime.WorkerEnabled)
        {
            services.AddHostedService<ProcessingJobReconciler>();
            services.AddHostedService<ProcessingWorkspaceCleanupService>();
            services.AddHostedService<OfflinePackageReconciler>();
            services.AddHostedService<OfflinePackageCleanupService>();
        }
        services
            .AddOptions<PlayerDeliveryOptions>()
            .Bind(configuration.GetSection(PlayerDeliveryOptions.SectionName))
            .Validate(
                options => options.SignedUrlMinutes is >= 1 and <= 10_080,
                "PlayerDelivery:SignedUrlMinutes must be between 1 and 10080.")
            .Validate(
                options => Uri.TryCreate(options.PublicApiBaseUrl, UriKind.Absolute, out var uri) &&
                           (uri.Scheme == Uri.UriSchemeHttps ||
                            (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)),
                "PlayerDelivery:PublicApiBaseUrl must use HTTPS or loopback HTTP.")
            .ValidateOnStart();
        services.AddScoped<IPlayerPublicationService, PlayerPublicationService>();
        services
            .AddOptions<OfflineLearningOptions>()
            .Bind(configuration.GetSection(OfflineLearningOptions.SectionName))
            .Validate(options => options.SignedUrlMinutes is >= 1 and <= 10_080,
                "OfflineLearning:SignedUrlMinutes must be between 1 and 10080.")
            .Validate(options => options.MaximumPackageBytes is >= 1_048_576 and <= 1_073_741_824,
                "OfflineLearning:MaximumPackageBytes must be between 1 MiB and 1 GiB.")
            .Validate(options => options.RetentionHours is >= 1 and <= 720,
                "OfflineLearning:RetentionHours must be between 1 and 720.")
            .Validate(options => options.RetentionHours * 60 > options.SignedUrlMinutes + 60,
                "OfflineLearning:RetentionHours must exceed signed URL lifetime by at least 60 minutes.")
            .ValidateOnStart();
        services.AddScoped<IOfflinePackageService, OfflinePackageService>();
        services.AddScoped<IOfflinePackageScheduler, HangfireOfflinePackageScheduler>();
        services.AddScoped<OfflinePackageJobRunner>();

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
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PublicServiceUrl) &&
                           Uri.TryCreate(options.PublicServiceUrl, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttps ||
                             (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)),
                "ObjectStorage:PublicServiceUrl must use HTTPS or loopback HTTP.")
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
        services.AddHostedService<RejectedObjectCleanupService>();

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
