using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sscp.ControlPlane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTrustModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "trust");

            migrationBuilder.CreateTable(
                name: "artifacts",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    build_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    deployable = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    commit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    candidate_repository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    digest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    trusted_repository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    latest_decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "trust",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    details = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "builds",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    source_repository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    commit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pull_request = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    pipeline_run_ids = table.Column<List<long>>(type: "bigint[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_builds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    git_ops_revision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sync_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    health_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    images = table.Column<string>(type: "jsonb", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "evidence",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    build_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    commit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    deployable = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    artifact_digest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    execution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    execution_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    submitted_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    pipeline_run_id = table.Column<long>(type: "bigint", nullable: false),
                    job_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    gate_passed = table.Column<bool>(type: "boolean", nullable: true),
                    gate_detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    report_content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    report_object_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    report_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    report_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    tool_database_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tool_database_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    tool_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    tool_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotions",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    to = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pipeline_run_id = table.Column<long>(type: "bigint", nullable: false),
                    promoted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "releases",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    tag = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    commit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    build_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    pipeline_run_id = table.Column<long>(type: "bigint", nullable: true),
                    git_ops_commit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    artifact_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_releases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "risk_exceptions",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    deployable = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    finding_fingerprint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    compensating_controls = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    approver = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_exceptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "signatures",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    key_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    signature_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    attestation_references = table.Column<string>(type: "jsonb", nullable: false),
                    pipeline_run_id = table.Column<long>(type: "bigint", nullable: false),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signatures", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trust_decisions",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    policy_version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    evaluated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    results = table.Column<string>(type: "jsonb", nullable: false),
                    applied_exceptions = table.Column<string>(type: "jsonb", nullable: false),
                    evidence_used = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trust_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "findings",
                schema: "trust",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    package = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    installed_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    fixed_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    fix_available = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_findings_evidence_evidence_id",
                        column: x => x.evidence_id,
                        principalSchema: "trust",
                        principalTable: "evidence",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_build_id_deployable",
                schema: "trust",
                table: "artifacts",
                columns: new[] { "build_id", "deployable" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_digest",
                schema: "trust",
                table: "artifacts",
                column: "digest");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_subject_type_subject_id",
                schema: "trust",
                table: "audit_log",
                columns: new[] { "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_builds_application_commit_kind",
                schema: "trust",
                table: "builds",
                columns: new[] { "application", "commit", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_release_id",
                schema: "trust",
                table: "deployments",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_application_kind",
                schema: "trust",
                table: "evidence",
                columns: new[] { "application", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_build_id",
                schema: "trust",
                table: "evidence",
                column: "build_id");

            migrationBuilder.CreateIndex(
                name: "ix_findings_evidence_id",
                schema: "trust",
                table: "findings",
                column: "evidence_id");

            migrationBuilder.CreateIndex(
                name: "ix_findings_fingerprint",
                schema: "trust",
                table: "findings",
                column: "fingerprint");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_artifact_id",
                schema: "trust",
                table: "promotions",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_releases_application_tag",
                schema: "trust",
                table: "releases",
                columns: new[] { "application", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_risk_exceptions_application_status",
                schema: "trust",
                table: "risk_exceptions",
                columns: new[] { "application", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_signatures_artifact_id",
                schema: "trust",
                table: "signatures",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_trust_decisions_artifact_id",
                schema: "trust",
                table: "trust_decisions",
                column: "artifact_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "builds",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "deployments",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "findings",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "promotions",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "releases",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "risk_exceptions",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "signatures",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "trust_decisions",
                schema: "trust");

            migrationBuilder.DropTable(
                name: "evidence",
                schema: "trust");
        }
    }
}
