using System.ComponentModel.DataAnnotations;

namespace Accessibility.Api.Contracts.V1;

public sealed class CreateLessonRequest
{
    [Required, MaxLength(200)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(2_000)]
    public string? Description { get; init; }

    [Required, MaxLength(35)]
    public string SourceLanguage { get; init; } = string.Empty;
}
