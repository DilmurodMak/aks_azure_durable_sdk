from temporalio.common import SearchAttributeKey, SearchAttributePair, TypedSearchAttributes

from approval.models import ChainInput

CLIENT = SearchAttributeKey.for_keyword("ClientId")
ACCOUNT = SearchAttributeKey.for_keyword("AccountId")
STEP = SearchAttributeKey.for_keyword("BusinessStep")
POLICY = SearchAttributeKey.for_keyword("PolicyId")
POLICY_VERSION = SearchAttributeKey.for_int("PolicyVersion")
STATE = SearchAttributeKey.for_keyword("ApprovalState")
CORRELATION = SearchAttributeKey.for_keyword("CorrelationId")


def attributes(request: ChainInput, state: str) -> TypedSearchAttributes:
    policy = request.policy
    pairs = [
        SearchAttributePair(CLIENT, policy.client_id),
        SearchAttributePair(STEP, policy.workflow_code),
        SearchAttributePair(POLICY, policy.id),
        SearchAttributePair(POLICY_VERSION, policy.version),
        SearchAttributePair(STATE, state),
        SearchAttributePair(CORRELATION, request.correlation_id),
    ]
    if policy.account_id:
        pairs.append(SearchAttributePair(ACCOUNT, policy.account_id))
    return TypedSearchAttributes(pairs)
