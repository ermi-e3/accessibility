using System.Globalization;
using System.Threading.RateLimiting;
using Accessibility.Api.Configuration;
using Accessibility.Api.Authorization;
using Accessibility.Api.Errors;
using Accessibility.Api.Identity;
using Accessibility.Api.Localization;
using Accessibility.Api.Multipart;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Infrastructure;
using Accessibility.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Localization;

var builder = WebApplication.CreateBuilder(args);

var localizationSettings = builder.Configuration
    .GetSection(LocalizationSettings.SectionName)
    .Get<LocalizationSettings>() ?? new LocalizationSettings();
var supportedCultures = localizationSettings.SupportedCultures
    .Select(CultureInfo.GetCultureInfo)
    .DistinctBy(culture => culture.Name)
    .ToArray();
if (supportedCultures.All(culture => culture.Name != localizationSettings.DefaultCulture))
{
    throw new InvalidOperationException("Localization:DefaultCulture must be included in SupportedCultures.");
}

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services
    .AddAuthentication()
    .AddPolicyScheme(
        AccessLearnAuthenticationSchemes.Management,
        AccessLearnAuthenticationSchemes.Management,
        options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? AccessLearnAuthenticationSchemes.ApiKey
                    : IdentityConstants.ApplicationScheme;
        })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        AccessLearnAuthenticationSchemes.ApiKey,
        _ => { });
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserContext, HttpUserContext>();
builder.Services.AddScoped<ManagementAntiforgeryFilter>();
builder.Services.AddScoped<IStreamingMultipartAssetReader, StreamingMultipartAssetReader>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ManagementAuthorizationResultHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        {
            var (code, titleKey) = context.ProblemDetails.Status switch
            {
                StatusCodes.Status400BadRequest => ("VALIDATION_ERROR", "ValidationErrorTitle"),
                StatusCodes.Status401Unauthorized => ("AUTHENTICATION_REQUIRED", "AuthenticationRequiredTitle"),
                StatusCodes.Status403Forbidden => ("FORBIDDEN", "ForbiddenErrorTitle"),
                StatusCodes.Status404NotFound => ("RESOURCE_NOT_FOUND", "NotFoundErrorTitle"),
                StatusCodes.Status413PayloadTooLarge => ("FILE_TOO_LARGE", "FileTooLargeTitle"),
                StatusCodes.Status415UnsupportedMediaType => ("UNSUPPORTED_FILE_TYPE", "UnsupportedFileTitle"),
                StatusCodes.Status422UnprocessableEntity => ("FILE_CONTENT_INVALID", "InvalidFileTitle"),
                StatusCodes.Status428PreconditionRequired => ("PRECONDITION_REQUIRED", "PreconditionRequiredTitle"),
                StatusCodes.Status412PreconditionFailed => ("VERSION_CONFLICT", "VersionConflictTitle"),
                StatusCodes.Status429TooManyRequests => ("RATE_LIMITED", "RateLimitedTitle"),
                StatusCodes.Status503ServiceUnavailable => ("SERVICE_UNAVAILABLE", "ServiceUnavailableTitle"),
                _ => ("UNEXPECTED_ERROR", "UnexpectedErrorTitle")
            };
            var localizer = context.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
            context.ProblemDetails.Extensions["code"] = code;
            context.ProblemDetails.Title = localizer[titleKey];
        }
    };
});
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(localizationSettings.DefaultCulture);
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.ApplyCurrentCultureToResponseHeaders = true;
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "accesslearn.antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsSettings.PublicReadPolicy, policy =>
        policy.AllowAnyOrigin()
            .WithMethods(HttpMethods.Get, HttpMethods.Head)
            .WithHeaders("Accept", "Accept-Language", "If-None-Match", "Range")
            .WithExposedHeaders("ETag", "Accept-Ranges", "Content-Range"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 120,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.AddPolicy(RateLimitPolicies.Authentication, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 30,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.AddPolicy(RateLimitPolicies.MediaUpload, _ =>
        RateLimitPartition.GetConcurrencyLimiter(
            "media-upload",
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 2,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Type = "https://httpstatuses.io/429",
                Instance = context.HttpContext.Request.Path
            }
        });
    };
});
builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var localizer = context.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
            var errors = context.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value!.Errors
                        .Select(_ => localizer["InvalidFieldValue"].Value)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            var details = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = localizer["ValidationErrorTitle"],
                Type = "https://httpstatuses.io/400",
                Instance = context.HttpContext.Request.Path
            };
            details.Extensions["code"] = "VALIDATION_ERROR";
            details.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            return new BadRequestObjectResult(details);
        };
    });
builder.Services.AddRazorPages();
builder.Services.AddOpenApi();

var app = builder.Build();

if (args.Contains("--initialize", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseInitializer.InitializeAsync(app.Services, CancellationToken.None);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRequestLocalization();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().RequireCors(CorsSettings.PublicReadPolicy);
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).DisableRateLimiting();
app.MapControllers();
app.MapRazorPages();

await app.RunAsync();

public partial class Program;
