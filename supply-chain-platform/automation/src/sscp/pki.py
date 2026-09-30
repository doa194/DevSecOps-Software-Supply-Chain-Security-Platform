"""Local public key infrastructure for the platform.

The bootstrap creates one root certificate authority on the workstation and issues TLS
server certificates for every platform endpoint that carries credentials (Gitea, Harbor,
Vault, Keycloak, MinIO, the Control Plane). Clients trust only this root, so a service
cannot be impersonated on the Docker network. Keys never leave .local/pki.
"""
from __future__ import annotations

import datetime as dt
import ipaddress
from dataclasses import dataclass
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

from sscp import paths

DOMAIN = "sscp.test"
CA_CERT = paths.PKI_DIR / "ca.crt"
CA_KEY = paths.PKI_DIR / "ca.key"
LEAF_VALIDITY = dt.timedelta(days=365)
RENEW_BEFORE = dt.timedelta(days=30)

# Every service that terminates TLS, with the extra names it must answer to.
SERVICES: dict[str, list[str]] = {
    "gitea": [],
    "harbor": [],
    "vault": [],
    "keycloak": [],
    "minio": [],
    "controlplane": [],
    # The commerce gateway inside the cluster, reached by tests and ZAP.
    "commerce": [],
}


@dataclass(frozen=True)
class LeafFiles:
    cert: Path
    key: Path
    chain: Path  # leaf followed by the CA, for servers that want a full chain


def leaf_files(service: str) -> LeafFiles:
    base = paths.PKI_DIR / service
    return LeafFiles(base / "tls.crt", base / "tls.key", base / "fullchain.crt")


def _now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def _write_key(path: Path, key: ec.EllipticCurvePrivateKey) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    )


def _write_cert(path: Path, cert: x509.Certificate) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))


def ensure_ca() -> tuple[x509.Certificate, ec.EllipticCurvePrivateKey]:
    """Creates the root CA once; later runs reuse it so issued trust stays valid."""
    if CA_CERT.exists() and CA_KEY.exists():
        cert = x509.load_pem_x509_certificate(CA_CERT.read_bytes())
        key = serialization.load_pem_private_key(CA_KEY.read_bytes(), password=None)
        return cert, key  # type: ignore[return-value]

    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([
        x509.NameAttribute(NameOID.ORGANIZATION_NAME, "SSCP Local"),
        x509.NameAttribute(NameOID.COMMON_NAME, "SSCP Local Root CA"),
    ])
    now = _now()
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(minutes=5))
        .not_valid_after(now + dt.timedelta(days=3650))
        # pathlen 0: this CA may sign server certificates but never another CA.
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(
            x509.KeyUsage(
                digital_signature=True, key_cert_sign=True, crl_sign=True, content_commitment=False,
                key_encipherment=False, data_encipherment=False, key_agreement=False,
                encipher_only=False, decipher_only=False,
            ),
            critical=True,
        )
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
        .sign(key, hashes.SHA256())
    )
    _write_key(CA_KEY, key)
    _write_cert(CA_CERT, cert)
    return cert, key


def _needs_renewal(path: Path, expected_names: set[str]) -> bool:
    if not path.exists():
        return True
    cert = x509.load_pem_x509_certificate(path.read_bytes())
    if cert.not_valid_after_utc - _now() < RENEW_BEFORE:
        return True
    try:
        san = cert.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
    except x509.ExtensionNotFound:
        return True
    return set(san.get_values_for_type(x509.DNSName)) != expected_names


def ensure_leaf(service: str, extra_names: list[str] | None = None) -> LeafFiles:
    """Issues (or renews) the server certificate for <service>.sscp.test."""
    ca_cert, ca_key = ensure_ca()
    files = leaf_files(service)
    dns_names = {f"{service}.{DOMAIN}", "localhost", *(extra_names or [])}
    if not _needs_renewal(files.cert, dns_names):
        return files

    key = ec.generate_private_key(ec.SECP256R1())
    now = _now()
    cert = (
        x509.CertificateBuilder()
        .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, f"{service}.{DOMAIN}")]))
        .issuer_name(ca_cert.subject)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(minutes=5))
        .not_valid_after(now + LEAF_VALIDITY)
        .add_extension(
            x509.SubjectAlternativeName(
                [x509.DNSName(name) for name in sorted(dns_names)]
                + [x509.IPAddress(ipaddress.ip_address("127.0.0.1"))]
            ),
            critical=False,
        )
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .add_extension(
            x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()), critical=False
        )
        .sign(ca_key, hashes.SHA256())
    )
    _write_key(files.key, key)
    _write_cert(files.cert, cert)
    files.chain.write_bytes(files.cert.read_bytes() + CA_CERT.read_bytes())
    return files


def ensure_all() -> None:
    ensure_ca()
    for service, extra in SERVICES.items():
        ensure_leaf(service, extra)
