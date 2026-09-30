"""CI helper used by the platform-owned pipelines inside the security, build and trust zones.

It turns the zone's standing Vault AppRole (bound to the runner's address) into
short-lived credentials, fetches exact source commits, and talks to the Security Control
Plane. Standard library only, so the job image needs nothing beyond Python.
"""
