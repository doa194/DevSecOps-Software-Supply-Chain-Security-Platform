"""The pinning policy must reject tag-only image references and unverified downloads."""
from sscp import pins

DIGEST = "sha256:" + "a" * 64


def test_digest_pinned_images_pass():
    versions = {"images": {"vault": f"hashicorp/vault:2.1.1@{DIGEST}"}, "kubernetes": {"nodeImage": f"kindest/node@{DIGEST}"}}

    assert pins.find_unpinned_images(versions) == []


def test_tag_only_image_is_reported():
    versions = {"images": {"vault": "hashicorp/vault:2.1.1"}, "kubernetes": {"nodeImage": f"kindest/node@{DIGEST}"}}

    violations = pins.find_unpinned_images(versions)

    assert [v.name for v in violations] == ["vault"]


def test_unpinned_cluster_node_image_is_reported():
    versions = {"images": {}, "kubernetes": {"nodeImage": "kindest/node:v1.36.4"}}

    assert [v.name for v in pins.find_unpinned_images(versions)] == ["kubernetes.nodeImage"]


def test_download_without_checksum_is_reported():
    versions = {"tools": {"kind": {"url": "https://example.test/kind", "sha256": None}, "pythonMinimum": "3.12"}}

    assert pins.find_unverified_tools(versions) == ["kind: download has no SHA-256 checksum"]
