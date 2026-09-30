# Vault server configuration for the local platform.
# Vault holds runtime/platform secrets and the non-exportable Cosign signing key
# (Transit). Integrated (Raft) storage keeps state in one volume; TLS uses a certificate
# from the local CA so clients can verify they are talking to the real Vault.

ui            = true
disable_mlock = true # containers cannot lock memory without extra privileges

api_addr     = "https://vault.sscp.test:8200"
cluster_addr = "https://vault.sscp.test:8201"

storage "raft" {
  path    = "/vault/file" # owned by the non-root vault user in the image
  node_id = "vault-1"
}

listener "tcp" {
  address         = "0.0.0.0:8200"
  cluster_address = "0.0.0.0:8201"
  tls_cert_file   = "/vault/tls/tls.crt"
  tls_key_file    = "/vault/tls/tls.key"
  tls_min_version = "tls12"

  telemetry {
    # Prometheus in the cluster scrapes /v1/sys/metrics without a token.
    unauthenticated_metrics_access = true
  }
}

telemetry {
  prometheus_retention_time = "60s"
  disable_hostname          = true
}
