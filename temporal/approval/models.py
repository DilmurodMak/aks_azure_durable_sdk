from __future__ import annotations

from datetime import datetime
from decimal import Decimal
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, model_validator
from pydantic.alias_generators import to_camel

Key = Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9-]{1,80}$")]
Actor = Annotated[str, StringConstraints(strip_whitespace=True, min_length=1, max_length=100)]
Timeout = Annotated[float, Field(gt=0, le=1440, allow_inf_nan=False)]
Money = Annotated[
    Decimal, Field(ge=0, le=Decimal("999999999999.99"), decimal_places=2, allow_inf_nan=False)
]
Outcome = Literal["Approved", "Rejected", "Returned", "Timeout", "Canceled"]
ENDING = {"Rejected", "Returned", "Timeout"}


class Model(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel, populate_by_name=True, extra="forbid", frozen=True
    )


class Stage(Model):
    name: Key
    roles: list[Key] = Field(min_length=1, max_length=10)
    timeout_minutes: Timeout = 15
    budget_greater_than: Money | None = None

    @model_validator(mode="after")
    def unique_roles(self) -> Stage:
        if len({role.casefold() for role in self.roles}) != len(self.roles):
            raise ValueError("Roles within a stage must be unique.")
        return self


def validate_stages(stages: list[Stage] | list[ResolvedStage]) -> None:
    if not 1 <= len(stages) <= 10:
        raise ValueError("A chain needs 1-10 stages.")
    if len({stage.name.casefold() for stage in stages}) != len(stages):
        raise ValueError("Stage names must be unique.")


class StepOverride(Model):
    workflow_code: Key
    stages: list[Stage]

    @model_validator(mode="after")
    def valid_chain(self) -> StepOverride:
        validate_stages(self.stages)
        if not any(stage.budget_greater_than is None for stage in self.stages):
            raise ValueError("A chain needs at least one unconditional stage.")
        return self


class StageRoute(Model):
    stage: Stage
    included: bool
    reason: str


class BudgetRouting(Model):
    budget_amount: Money
    currency: Literal["USD"] = "USD"
    stages: list[StageRoute]


def evaluate_budget(amount: Money, stages: list[Stage]) -> BudgetRouting:
    routes = []
    for stage in stages:
        threshold = stage.budget_greater_than
        included = threshold is None or amount > threshold
        reason = "Included: unconditional."
        if threshold is not None:
            reason = (
                f"{'Included' if included else 'Skipped'}: USD {amount} "
                f"{'>' if included else '<='} USD {threshold}."
            )
        routes.append(StageRoute(stage=stage, included=included, reason=reason))
    if not any(route.included for route in routes):
        raise ValueError("At least one unconditional approval stage is required.")
    return BudgetRouting(budget_amount=amount, stages=routes)


class PolicyDefinition(Model):
    id: Key
    client_id: Key
    account_id: Key | None = None
    default_stages: list[Stage] | None = None
    overrides: list[StepOverride] = Field(default_factory=list, max_length=50)

    @model_validator(mode="after")
    def valid_definition(self) -> PolicyDefinition:
        if self.default_stages is not None:
            validate_stages(self.default_stages)
            if not any(stage.budget_greater_than is None for stage in self.default_stages):
                raise ValueError("A chain needs at least one unconditional stage.")
        if self.default_stages is None and not self.overrides:
            raise ValueError("Provide a default chain or a step override.")
        if len({item.workflow_code for item in self.overrides}) != len(self.overrides):
            raise ValueError("Each business step can have only one override.")
        return self


class SavePolicy(PolicyDefinition):
    base_version: int = Field(default=0, ge=0)


class Policy(PolicyDefinition):
    version: int = Field(ge=1)
    saved_at: datetime


class ActivePolicy(Model):
    id: Key
    version: int


class Registry(Model):
    policies: list[Policy] = Field(default_factory=list)
    active: list[ActivePolicy] = Field(default_factory=list)


class Identity(Model):
    client_id: Key
    account_id: Key | None = None
    role: Key
    actor: Actor


class SaveIdentities(Model):
    client_id: Key
    account_id: Key | None = None
    assignments: dict[Key, Actor] = Field(max_length=100)

    @model_validator(mode="after")
    def unique_roles(self) -> SaveIdentities:
        if len({role.casefold() for role in self.assignments}) != len(self.assignments):
            raise ValueError("Role assignments must be unique.")
        return self


class IdentityRegistry(Model):
    assignments: list[Identity] = Field(default_factory=list)


class Role(Model):
    role: Key
    approver: Actor


class ResolvedStage(Model):
    name: Key
    timeout_minutes: Timeout
    approvals: list[Role] = Field(min_length=1, max_length=10)


class Snapshot(Model):
    id: Key
    client_id: Key
    account_id: Key | None
    workflow_code: Key
    version: int = Field(ge=1)
    stages: list[ResolvedStage]
    source: str
    routing: BudgetRouting

    @model_validator(mode="after")
    def valid_chain(self) -> Snapshot:
        validate_stages(self.stages)
        for stage in self.stages:
            if len({role.role.casefold() for role in stage.approvals}) != len(stage.approvals):
                raise ValueError("Roles within a stage must be unique.")
        return self


class StartRequest(Model):
    instance_id: Key
    client_id: Key
    account_id: Key | None = None
    workflow_code: Key
    plan_id: Key
    version: int = Field(ge=1)
    requester: Actor
    budget_amount: Money
    correlation_id: Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9_-]{1,80}$")] | None = (
        None
    )
    apply_decision_delay_seconds: int = Field(default=0, ge=0, le=60)


class ChainInput(Model):
    plan_id: Key
    version: int = Field(ge=1)
    requester: Actor
    correlation_id: str
    policy: Snapshot
    apply_decision_delay_seconds: int = Field(default=0, ge=0, le=60)

    @model_validator(mode="after")
    def separation_of_duties(self) -> ChainInput:
        first_stage: dict[str, str] = {}
        for stage in self.policy.stages:
            for role in stage.approvals:
                actor = role.approver.casefold()
                if actor == self.requester.casefold():
                    raise ValueError(
                        f"Requester holds required role {role.role} in stage {stage.name}."
                    )
                if actor in first_stage and first_stage[actor] != stage.name:
                    raise ValueError(
                        f"{role.approver} reviews in both {first_stage[actor]} and {stage.name}."
                    )
                first_stage[actor] = stage.name
        return self


class Decision(Model):
    approver: str
    outcome: str
    version: int
    comment: str | None = Field(default=None, max_length=1000)


class DecisionForm(Model):
    acting_as: Actor
    outcome: str
    version: int = Field(ge=1)
    comment: str | None = Field(default=None, max_length=1000)


def comment_problem(outcome: str, comment: str | None) -> str | None:
    if outcome in {"Reject", "Return"} and not (comment and comment.strip()):
        return "A comment is required to reject or return a plan."
    return None


def refusal(request: StepInput, decision: Decision) -> str | None:
    if decision.approver.casefold() == request.chain.requester.casefold():
        return "Self-approval is not allowed."
    if decision.approver.casefold() != request.approver.casefold():
        return "This review is assigned to another user."
    if decision.version != request.chain.version:
        return "The decision has a stale plan version."
    if decision.outcome not in {"Approve", "Reject", "Return"}:
        return "Outcome must be Approve, Reject or Return."
    return comment_problem(decision.outcome, decision.comment)


class StepInput(Model):
    parent_instance_id: Key
    chain: ChainInput
    stage: Key
    stage_number: int = Field(ge=1, le=10)
    role: Key
    approver: Actor
    timeout_minutes: Timeout


class StepResult(Model):
    status: Outcome
    version: int
    decided_by: str | None = None
    decided_at: datetime
    comment: str | None = None


class StepRecord(StepResult):
    instance_id: str
    stage: str
    role: str
    approver: str


class ChainResult(Model):
    status: Outcome
    version: int
    policy_id: str
    policy_version: int
    steps: list[StepRecord]
    stages_not_started: list[str]
    receipt: str | None


class Progress(Model):
    state: str
    stage: str
    stage_number: int
    completed: int = 0
    total: int = 1
    refused_decisions: int = 0
    last_refusal: str | None = None


class DecisionRecord(Model):
    request: StepInput
    result: StepResult


class ApplyRequest(Model):
    receipt: str
    delay_seconds: int = Field(ge=0, le=60)


def step_id(parent: str, stage: int, reviewer: int) -> str:
    return f"{parent}-s{stage}-r{reviewer}"
