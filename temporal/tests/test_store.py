import re
from decimal import Decimal
from pathlib import Path

import pytest
from pydantic import ValidationError

from approval.catalogue import STEPS
from approval.models import ChainInput, SaveIdentities, SavePolicy, StartRequest
from approval.store import Conflict, Store


def draft(**changes) -> SavePolicy:
    return SavePolicy.model_validate(
        {
            "id": "policy",
            "clientId": "client",
            "defaultStages": [{"name": "review", "roles": ["Finance", "Legal"]}],
            **changes,
        }
    )


@pytest.fixture
def store(tmp_path):
    value = Store(tmp_path)
    value.save_identities(
        SaveIdentities(clientId="client", assignments={"Finance": "finance", "Legal": "legal"})
    )
    return value


def test_save_is_immutable_and_unchanged_content_does_not_version(store):
    first = store.save(draft())
    before = (store.directory / "policies.json").read_bytes()
    same = store.save(draft(baseVersion=1))
    assert same == first
    assert (store.directory / "policies.json").read_bytes() == before
    second = store.save(
        draft(baseVersion=1, defaultStages=[{"name": "finance", "roles": ["Finance"]}])
    )
    assert second.version == 2
    assert store.policies().policies[0] == first
    assert store.resolve("client", None, "B07", Decimal("7500")).version == 2
    store.activate("policy", 1)
    assert store.resolve("client", None, "B07", Decimal("7500")).version == 1


@pytest.mark.parametrize(
    ("amount", "included"), [("4999.99", False), ("5000", False), ("5000.01", True), ("7500", True)]
)
def test_budget_routing_is_strict_decimal_and_serializes_its_reasons(store, amount, included):
    store.save(
        draft(
            defaultStages=[
                {"name": "Internal", "roles": ["Legal"]},
                {"name": "Finance", "roles": ["Finance"], "budgetGreaterThan": "5000"},
            ]
        )
    )
    snapshot = store.resolve("client", None, "B07", Decimal(amount))
    assert snapshot.routing.currency == "USD"
    assert snapshot.routing.budget_amount == Decimal(amount)
    assert snapshot.routing.stages[0].included
    assert snapshot.routing.stages[1].included == included
    assert snapshot.routing.stages[1].stage.budget_greater_than == Decimal("5000")
    assert (">" if included else "<=") in snapshot.routing.stages[1].reason
    assert type(snapshot).model_validate_json(snapshot.model_dump_json()) == snapshot
    assert [s.name for s in snapshot.stages] == (
        ["Internal", "Finance"] if included else ["Internal"]
    )


def test_threshold_only_edit_versions_and_does_not_mutate_original_route(store):
    stages = [
        {"name": "Internal", "roles": ["Legal"]},
        {"name": "Finance", "roles": ["Finance"], "budgetGreaterThan": "5000"},
    ]
    store.save(draft(defaultStages=stages))
    original = store.resolve("client", None, "B07", Decimal("7500"))
    changed = [stages[0], {**stages[1], "budgetGreaterThan": "10000"}]
    second = store.save(draft(baseVersion=1, defaultStages=changed))
    assert second.version == 2
    next_run = store.resolve("client", None, "B07", Decimal("7500"))
    assert original.version == 1 and original.routing.stages[1].included
    assert next_run.version == 2 and not next_run.routing.stages[1].included
    assert store.save(draft(baseVersion=2, defaultStages=changed)).version == 2


def test_skipped_role_does_not_require_identity_or_participate_in_self_review(store):
    store.save(
        draft(
            defaultStages=[
                {"name": "Internal", "roles": ["Legal"]},
                {"name": "Finance", "roles": ["Finance"], "budgetGreaterThan": "10000"},
            ]
        )
    )
    store.save_identities(SaveIdentities(clientId="client", assignments={"Legal": "legal"}))
    snapshot = store.resolve("client", None, "B07", Decimal("7500"))
    ChainInput(planId="plan", version=1, requester="finance", correlationId="corr", policy=snapshot)
    with pytest.raises(Conflict, match="Finance"):
        store.resolve("client", None, "B07", Decimal("10000.01"))


@pytest.mark.parametrize("threshold", ["-1", "0.001", "NaN", "Infinity", "1000000000000"])
def test_invalid_thresholds_are_rejected(threshold):
    with pytest.raises(ValidationError):
        draft(
            defaultStages=[
                {"name": "Internal", "roles": ["Legal"]},
                {"name": "Finance", "roles": ["Finance"], "budgetGreaterThan": threshold},
            ]
        )


def test_all_conditional_chain_is_rejected():
    with pytest.raises(ValidationError, match="unconditional"):
        draft(defaultStages=[{"name": "Finance", "roles": ["Finance"], "budgetGreaterThan": "0"}])


def test_stale_save_and_scope_change_are_conflicts(store):
    store.save(draft())
    with pytest.raises(Conflict, match="changed"):
        store.save(draft())
    with pytest.raises(Conflict, match="scope"):
        store.save(draft(baseVersion=1, clientId="different"))


def test_default_and_override_narrower_owner_precedence(store):
    store.save(
        draft(
            overrides=[{"workflowCode": "B07", "stages": [{"name": "special", "roles": ["Legal"]}]}]
        )
    )
    assert store.resolve("client", None, "B07", Decimal("7500")).source == "client override"
    assert store.resolve("client", None, "B08", Decimal("7500")).source == "client default"
    store.save(draft(id="account", accountId="account"))
    assert store.resolve("client", "account", "B07", Decimal("7500")).source == "account default"
    store.save(draft(id="agency", clientId="agency-default"))
    store.save_identities(
        SaveIdentities(
            clientId="agency-default", assignments={"Finance": "finance", "Legal": "legal"}
        )
    )
    assert store.resolve("other", None, "B07", Decimal("7500")).source == "agency default"


def test_identity_edits_do_not_version_or_mutate_a_resolved_snapshot(store):
    store.save(draft())
    snapshot = store.resolve("client", None, "B07", Decimal("7500"))
    store.save_identities(
        SaveIdentities(clientId="client", assignments={"Finance": "new-finance", "Legal": "legal"})
    )
    assert snapshot.stages[0].approvals[0].approver == "finance"
    assert (
        store.resolve("client", None, "B07", Decimal("7500")).stages[0].approvals[0].approver
        == "new-finance"
    )
    assert len(store.policies().policies) == 1


def test_missing_identity_or_policy_is_explicit(store):
    with pytest.raises(Conflict, match="No active"):
        store.resolve("client", None, "B07", Decimal("7500"))
    store.save(draft(defaultStages=[{"name": "owner", "roles": ["Owner"]}]))
    with pytest.raises(Conflict, match="Owner"):
        store.resolve("client", None, "B07", Decimal("7500"))


@pytest.mark.parametrize("requester", ["finance", "FINANCE"])
def test_self_review_is_rejected_at_start(store, requester):
    store.save(draft())
    with pytest.raises(ValidationError, match="Requester holds"):
        ChainInput(
            planId="plan",
            version=1,
            requester=requester,
            correlationId="corr",
            policy=store.resolve("client", None, "B07", Decimal("7500")),
        )


def test_person_in_two_stages_is_rejected(store):
    store.save(
        draft(
            defaultStages=[
                {"name": "one", "roles": ["Finance"]},
                {"name": "two", "roles": ["Legal"]},
            ]
        )
    )
    store.save_identities(
        SaveIdentities(clientId="client", assignments={"Finance": "same", "Legal": "SAME"})
    )
    with pytest.raises(ValidationError, match="both"):
        ChainInput(
            planId="plan",
            version=1,
            requester="requester",
            correlationId="corr",
            policy=store.resolve("client", None, "B07", Decimal("7500")),
        )


@pytest.mark.parametrize(
    "stages",
    [
        [],
        [{"name": "one", "roles": []}],
        [{"name": "one", "roles": ["Finance", "FINANCE"]}],
        [{"name": "one", "roles": ["Finance"], "timeoutMinutes": 0}],
        [{"name": "one", "roles": ["Finance"], "timeoutMinutes": 1441}],
        [{"name": "one", "roles": ["Finance"]}, {"name": "ONE", "roles": ["Legal"]}],
    ],
)
def test_bad_chain_shapes_are_rejected(stages):
    with pytest.raises(ValidationError):
        draft(defaultStages=stages)


def test_corrupt_store_is_not_silently_reset(store):
    (store.directory / "policies.json").write_text("broken")
    with pytest.raises(ValidationError):
        store.policies()


def test_catalogue_matches_current_durable_source():
    source = Path("../src/Approval.Console/BusinessStepCatalogue.cs").read_text()
    assert set(STEPS) == set(re.findall(r'new\("([A-Z][0-9]{2})"', source))


@pytest.mark.parametrize("value", ["bad'id", "bad/id", "", "x" * 81])
def test_untrusted_keys_are_rejected(value):
    with pytest.raises(ValidationError):
        StartRequest(
            instanceId=value,
            clientId="client",
            workflowCode="B07",
            planId="plan",
            version=1,
            requester="requester",
        )
