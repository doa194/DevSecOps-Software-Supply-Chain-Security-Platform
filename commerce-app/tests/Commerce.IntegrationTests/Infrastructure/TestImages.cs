// Container images for integration tests, pinned by digest like every other image the
// platform runs (they match supply-chain-platform/versions.yaml).
namespace Commerce.IntegrationTests.Infrastructure;

internal static class TestImages
{
    public const string Postgres = "postgres:18.6-trixie@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722";
    public const string RabbitMq = "rabbitmq:4.2.9-management-alpine@sha256:cb84d1cee317570eeb9b61d7c64df519399ecb450d16ec93aa035c1d42501475";
    public const string Keycloak = "quay.io/keycloak/keycloak:26.7.4@sha256:82a77884f3af238beab1e7afd63b5f530e1b5c0590bd7aa60b40a40463e29b2c";
    public const string Minio = "cgr.dev/chainguard/minio:latest@sha256:bd014394a80898e68c149f2311fdf8d5a2c2f3bb2c33b9327ae6d02b4b065ae1";
}
