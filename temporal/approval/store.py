from __future__ import annotations

import os
import tempfile
import threading
from datetime import UTC, datetime
from pathlib import Path
from typing import TypeVar

from approval.models import (
    ActivePolicy,
    Identity,
    IdentityRegistry,
    Model,
    Policy,
    Registry,
    ResolvedStage,
    Role,
    SaveIdentities,
    SavePolicy,
    Snapshot,
)

T = TypeVar("T", bound=Model)


class Conflict(Exception):
    """A user-correctable conflict with a saved policy or role assignment."""


class Store:
    """Single API process owns these files; workflows receive snapshots, never paths."""

    def __init__(self, directory: Path) -> None:
        self.directory = directory
        self.lock = threading.RLock()

    def _read(self, name: str, model: type[T]) -> T:
        path = self.directory / name
        try:
            return model.model_validate_json(path.read_text())
        except FileNotFoundError:
            return model.model_validate({})

    def _write(self, name: str, value: Model) -> None:
        self.directory.mkdir(parents=True, exist_ok=True)
        fd, path = tempfile.mkstemp(prefix=".registry-", dir=self.directory)
        try:
            with os.fdopen(fd, "w") as stream:
                stream.write(value.model_dump_json(by_alias=True, indent=2))
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(path, self.directory / name)
        finally:
            if os.path.exists(path):
                os.unlink(path)

    def policies(self) -> Registry:
        with self.lock:
            return self._read("policies.json", Registry)

    def identities(self) -> IdentityRegistry:
        with self.lock:
            return self._read("identities.json", IdentityRegistry)

    @staticmethod
    def _activate(registry: Registry, policy: Policy) -> Registry:
        by_key = {(p.id, p.version): p for p in registry.policies}
        retained = [
            item
            for item in registry.active
            if (by_key[item.id, item.version].client_id, by_key[item.id, item.version].account_id)
            != (policy.client_id, policy.account_id)
        ]
        return Registry(
            policies=registry.policies,
            active=[*retained, ActivePolicy(id=policy.id, version=policy.version)],
        )

    def save(self, request: SavePolicy) -> Policy:
        with self.lock:
            registry = self.policies()
            latest = max(
                (p for p in registry.policies if p.id == request.id),
                key=lambda p: p.version,
                default=None,
            )
            if (latest.version if latest else 0) != request.base_version:
                raise Conflict("Policy changed since it was loaded. Reload before saving.")
            if latest and (latest.client_id, latest.account_id) != (
                request.client_id,
                request.account_id,
            ):
                raise Conflict("An existing policy scope cannot change. Create a new policy ID.")
            definition = request.model_dump(exclude={"base_version"})
            definition["overrides"] = sorted(
                definition["overrides"], key=lambda o: o["workflow_code"]
            )
            if latest and latest.model_dump(exclude={"version", "saved_at"}) == definition:
                return latest
            policy = Policy(
                **definition, version=request.base_version + 1, saved_at=datetime.now(UTC)
            )
            updated = Registry(policies=[*registry.policies, policy], active=registry.active)
            self._write("policies.json", self._activate(updated, policy))
            return policy

    def activate(self, policy_id: str, version: int) -> Policy:
        with self.lock:
            registry = self.policies()
            policy = next(
                (p for p in registry.policies if p.id == policy_id and p.version == version), None
            )
            if policy is None:
                raise KeyError("Policy version was not found.")
            self._write("policies.json", self._activate(registry, policy))
            return policy

    def save_identities(self, request: SaveIdentities) -> IdentityRegistry:
        with self.lock:
            retained = [
                i
                for i in self.identities().assignments
                if (i.client_id, i.account_id) != (request.client_id, request.account_id)
            ]
            result = IdentityRegistry(
                assignments=[
                    *retained,
                    *[
                        Identity(
                            client_id=request.client_id,
                            account_id=request.account_id,
                            role=role,
                            actor=actor,
                        )
                        for role, actor in request.assignments.items()
                    ],
                ]
            )
            self._write("identities.json", result)
            return result

    def resolve(self, client: str, account: str | None, code: str) -> Snapshot:
        with self.lock:
            registry = self.policies()
            active = {
                (p.client_id, p.account_id): p
                for p in registry.policies
                if ActivePolicy(id=p.id, version=p.version) in registry.active
            }
            owners = [("client", client, None), ("agency", "agency-default", None)]
            if account is not None:
                owners.insert(0, ("account", client, account))
            assignments = self.identities().assignments

            def actor_for(role: str) -> str:
                for scope in [(client, account), (client, None), ("agency-default", None)]:
                    match = next(
                        (
                            i
                            for i in assignments
                            if (i.client_id, i.account_id) == scope and i.role == role
                        ),
                        None,
                    )
                    if match:
                        return match.actor
                raise Conflict(f"Assign a demo user to role {role} before starting.")

            for level, owner, owner_account in owners:
                policy = active.get((owner, owner_account))
                if policy is None:
                    continue
                override = next((o for o in policy.overrides if o.workflow_code == code), None)
                stages = override.stages if override else policy.default_stages
                if stages is None:
                    continue
                return Snapshot(
                    id=policy.id,
                    client_id=client,
                    account_id=account,
                    workflow_code=code,
                    version=policy.version,
                    source=f"{level} {'override' if override else 'default'}",
                    stages=[
                        ResolvedStage(
                            name=stage.name,
                            timeout_minutes=stage.timeout_minutes,
                            approvals=[
                                Role(role=role, approver=actor_for(role)) for role in stage.roles
                            ],
                        )
                        for stage in stages
                    ],
                )
            raise Conflict("No active policy supplies a default chain or override for this step.")
