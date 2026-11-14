using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAccessLearn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asp_net_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_asp_net_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_asp_net_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stored_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    byte_length = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    e_tag = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_stored_files", x => x.id);
                    table.CheckConstraint("ck_stored_files_byte_length", "byte_length > 0");
                });

            migrationBuilder.CreateTable(
                name: "asp_net_role_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_asp_net_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "f_k_asp_net_role_claims_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "asp_net_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_asp_net_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "f_k_asp_net_user_claims_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_logins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_asp_net_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "f_k_asp_net_user_logins_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_asp_net_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "f_k_asp_net_user_roles_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "asp_net_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "f_k_asp_net_user_roles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asp_net_user_tokens",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_asp_net_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "f_k_asp_net_user_tokens_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "integration_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    allowed_origins = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_integration_clients", x => x.id);
                    table.ForeignKey(
                        name: "f_k_integration_clients_asp_net_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "caption_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transcript_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    generator_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    checksum = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_caption_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "f_k_caption_artifacts_stored_files_stored_file_id",
                        column: x => x.stored_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    alt_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_decorative = table.Column<bool>(type: "boolean", nullable: false),
                    review_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_document_images", x => x.id);
                    table.ForeignKey(
                        name: "f_k_document_images_stored_files_stored_file_id",
                        column: x => x.stored_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_outputs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    validation_report_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    generator_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_document_outputs", x => x.id);
                    table.CheckConstraint("ck_document_outputs_source_revision", "source_revision > 0");
                    table.ForeignKey(
                        name: "f_k_document_outputs_stored_files_stored_file_id",
                        column: x => x.stored_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_document_outputs_stored_files_validation_report_file_id",
                        column: x => x.validation_report_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_model_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_documents", x => x.id);
                    table.ForeignKey(
                        name: "f_k_documents_stored_files_normalized_model_file_id",
                        column: x => x.normalized_model_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_documents_stored_files_source_file_id",
                        column: x => x.source_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lesson_publications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    manifest_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checksum = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_lesson_publications", x => x.id);
                    table.UniqueConstraint("a_k_lesson_publications_id_lesson_id", x => new { x.id, x.lesson_id });
                    table.CheckConstraint("ck_lesson_publications_revision", "revision > 0");
                    table.ForeignKey(
                        name: "f_k_lesson_publications_asp_net_users_published_by_user_id",
                        column: x => x.published_by_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_lesson_publications_stored_files_manifest_file_id",
                        column: x => x.manifest_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lessons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    source_language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    draft_revision = table.Column<long>(type: "bigint", nullable: false),
                    current_publication_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_lessons", x => x.id);
                    table.ForeignKey(
                        name: "f_k_lessons_asp_net_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_lessons_lesson_publications_current_publication_id_id",
                        columns: x => new { x.current_publication_id, x.id },
                        principalTable: "lesson_publications",
                        principalColumns: new[] { "id", "lesson_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offline_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    manifest_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    zip_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    checksum = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_offline_packages", x => x.id);
                    table.CheckConstraint("ck_offline_packages_total_bytes", "total_bytes IS NULL OR total_bytes > 0");
                    table.ForeignKey(
                        name: "f_k_offline_packages_lesson_publications_lesson_publication_id",
                        column: x => x.lesson_publication_id,
                        principalTable: "lesson_publications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_offline_packages_stored_files_manifest_file_id",
                        column: x => x.manifest_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_offline_packages_stored_files_zip_file_id",
                        column: x => x.zip_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    duration_milliseconds = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    codec = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    sync_offset_milliseconds = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_media_assets", x => x.id);
                    table.CheckConstraint("ck_media_assets_duration", "duration_milliseconds IS NULL OR duration_milliseconds > 0");
                    table.ForeignKey(
                        name: "f_k_media_assets_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_media_assets_stored_files_stored_file_id",
                        column: x => x.stored_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transcripts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_transcript_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_lesson_revision = table.Column<long>(type: "bigint", nullable: false),
                    review_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_transcripts", x => x.id);
                    table.CheckConstraint("ck_transcripts_revision", "revision > 0 AND source_lesson_revision > 0");
                    table.ForeignKey(
                        name: "f_k_transcripts_asp_net_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_transcripts_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_transcripts_transcripts_source_transcript_id",
                        column: x => x.source_transcript_id,
                        principalTable: "transcripts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "processing_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_job_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    progress_percentage = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_processing_jobs", x => x.id);
                    table.CheckConstraint("ck_processing_jobs_progress", "progress_percentage >= 0 AND progress_percentage <= 100");
                    table.CheckConstraint("ck_processing_jobs_target", "lesson_id IS NOT NULL OR document_id IS NOT NULL OR media_asset_id IS NOT NULL");
                    table.ForeignKey(
                        name: "f_k_processing_jobs_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_processing_jobs_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_processing_jobs_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "f_k_processing_jobs_processing_jobs_parent_job_id",
                        column: x => x.parent_job_id,
                        principalTable: "processing_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transcript_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transcript_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    start_milliseconds = table.Column<long>(type: "bigint", nullable: false),
                    end_milliseconds = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    speaker_label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_transcript_segments", x => x.id);
                    table.CheckConstraint("ck_transcript_segments_timing", "sequence >= 0 AND start_milliseconds >= 0 AND end_milliseconds > start_milliseconds");
                    table.ForeignKey(
                        name: "f_k_transcript_segments_transcripts_transcript_id",
                        column: x => x.transcript_id,
                        principalTable: "transcripts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_asp_net_role_claims_role_id",
                table: "asp_net_role_claims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "role_name_index",
                table: "asp_net_roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_asp_net_user_claims_user_id",
                table: "asp_net_user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_asp_net_user_logins_user_id",
                table: "asp_net_user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_asp_net_user_roles_role_id",
                table: "asp_net_user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "email_index",
                table: "asp_net_users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "user_name_index",
                table: "asp_net_users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_caption_artifacts_stored_file_id",
                table: "caption_artifacts",
                column: "stored_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_caption_artifacts_transcript_id_format_generator_version",
                table: "caption_artifacts",
                columns: new[] { "transcript_id", "format", "generator_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_document_images_document_id_sequence",
                table: "document_images",
                columns: new[] { "document_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_document_images_stored_file_id",
                table: "document_images",
                column: "stored_file_id");

            migrationBuilder.CreateIndex(
                name: "i_x_document_outputs_document_id_source_revision_kind_generator~",
                table: "document_outputs",
                columns: new[] { "document_id", "source_revision", "kind", "generator_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_document_outputs_stored_file_id",
                table: "document_outputs",
                column: "stored_file_id");

            migrationBuilder.CreateIndex(
                name: "i_x_document_outputs_validation_report_file_id",
                table: "document_outputs",
                column: "validation_report_file_id");

            migrationBuilder.CreateIndex(
                name: "i_x_documents_lesson_id",
                table: "documents",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "i_x_documents_normalized_model_file_id",
                table: "documents",
                column: "normalized_model_file_id");

            migrationBuilder.CreateIndex(
                name: "i_x_documents_source_file_id",
                table: "documents",
                column: "source_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_integration_clients_key_prefix",
                table: "integration_clients",
                column: "key_prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_integration_clients_owner_user_id",
                table: "integration_clients",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_lesson_publications_lesson_id_revision",
                table: "lesson_publications",
                columns: new[] { "lesson_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_lesson_publications_manifest_file_id",
                table: "lesson_publications",
                column: "manifest_file_id");

            migrationBuilder.CreateIndex(
                name: "i_x_lesson_publications_published_by_user_id",
                table: "lesson_publications",
                column: "published_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_lessons_current_publication_id_id",
                table: "lessons",
                columns: new[] { "current_publication_id", "id" });

            migrationBuilder.CreateIndex(
                name: "i_x_lessons_owner_user_id_updated_at",
                table: "lessons",
                columns: new[] { "owner_user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "i_x_lessons_status",
                table: "lessons",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "i_x_media_assets_lesson_id",
                table: "media_assets",
                column: "lesson_id",
                unique: true,
                filter: "is_active = TRUE AND role IN ('PrimaryVideo', 'PrimaryAudio')");

            migrationBuilder.CreateIndex(
                name: "i_x_media_assets_stored_file_id",
                table: "media_assets",
                column: "stored_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_offline_packages_lesson_publication_id_profile",
                table: "offline_packages",
                columns: new[] { "lesson_publication_id", "profile" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_offline_packages_manifest_file_id",
                table: "offline_packages",
                column: "manifest_file_id");

            migrationBuilder.CreateIndex(
                name: "i_x_offline_packages_zip_file_id",
                table: "offline_packages",
                column: "zip_file_id");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_document_id",
                table: "processing_jobs",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_idempotency_key",
                table: "processing_jobs",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_lesson_id",
                table: "processing_jobs",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_media_asset_id",
                table: "processing_jobs",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_parent_job_id",
                table: "processing_jobs",
                column: "parent_job_id");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_status_created_at",
                table: "processing_jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "i_x_stored_files_object_key",
                table: "stored_files",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_stored_files_sha256_byte_length",
                table: "stored_files",
                columns: new[] { "sha256", "byte_length" });

            migrationBuilder.CreateIndex(
                name: "i_x_transcript_segments_transcript_id_sequence",
                table: "transcript_segments",
                columns: new[] { "transcript_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_transcript_segments_transcript_id_start_milliseconds",
                table: "transcript_segments",
                columns: new[] { "transcript_id", "start_milliseconds" });

            migrationBuilder.CreateIndex(
                name: "i_x_transcripts_approved_by_user_id",
                table: "transcripts",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_transcripts_lesson_id_language_review_status",
                table: "transcripts",
                columns: new[] { "lesson_id", "language", "review_status" },
                unique: true,
                filter: "review_status = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "i_x_transcripts_lesson_id_language_revision",
                table: "transcripts",
                columns: new[] { "lesson_id", "language", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_transcripts_source_transcript_id",
                table: "transcripts",
                column: "source_transcript_id");

            migrationBuilder.AddForeignKey(
                name: "f_k_caption_artifacts_transcripts_transcript_id",
                table: "caption_artifacts",
                column: "transcript_id",
                principalTable: "transcripts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "f_k_document_images_documents_document_id",
                table: "document_images",
                column: "document_id",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "f_k_document_outputs_documents_document_id",
                table: "document_outputs",
                column: "document_id",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "f_k_documents_lessons_lesson_id",
                table: "documents",
                column: "lesson_id",
                principalTable: "lessons",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "f_k_lesson_publications_lessons_lesson_id",
                table: "lesson_publications",
                column: "lesson_id",
                principalTable: "lessons",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "f_k_lesson_publications_asp_net_users_published_by_user_id",
                table: "lesson_publications");

            migrationBuilder.DropForeignKey(
                name: "f_k_lessons_asp_net_users_owner_user_id",
                table: "lessons");

            migrationBuilder.DropForeignKey(
                name: "f_k_lesson_publications_stored_files_manifest_file_id",
                table: "lesson_publications");

            migrationBuilder.DropForeignKey(
                name: "f_k_lesson_publications_lessons_lesson_id",
                table: "lesson_publications");

            migrationBuilder.DropTable(
                name: "asp_net_role_claims");

            migrationBuilder.DropTable(
                name: "asp_net_user_claims");

            migrationBuilder.DropTable(
                name: "asp_net_user_logins");

            migrationBuilder.DropTable(
                name: "asp_net_user_roles");

            migrationBuilder.DropTable(
                name: "asp_net_user_tokens");

            migrationBuilder.DropTable(
                name: "caption_artifacts");

            migrationBuilder.DropTable(
                name: "document_images");

            migrationBuilder.DropTable(
                name: "document_outputs");

            migrationBuilder.DropTable(
                name: "integration_clients");

            migrationBuilder.DropTable(
                name: "offline_packages");

            migrationBuilder.DropTable(
                name: "processing_jobs");

            migrationBuilder.DropTable(
                name: "transcript_segments");

            migrationBuilder.DropTable(
                name: "asp_net_roles");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "media_assets");

            migrationBuilder.DropTable(
                name: "transcripts");

            migrationBuilder.DropTable(
                name: "asp_net_users");

            migrationBuilder.DropTable(
                name: "stored_files");

            migrationBuilder.DropTable(
                name: "lessons");

            migrationBuilder.DropTable(
                name: "lesson_publications");
        }
    }
}
