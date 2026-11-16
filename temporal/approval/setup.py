import asyncio

from temporalio.api.enums.v1 import IndexedValueType
from temporalio.api.operatorservice.v1 import (
    AddSearchAttributesRequest,
    ListSearchAttributesRequest,
)
from temporalio.api.workflowservice.v1 import RegisterNamespaceRequest
from temporalio.service import RPCError, RPCStatusCode

from approval.runtime import connect


async def setup() -> None:
    client = await connect()
    try:
        await client.workflow_service.register_namespace(
            RegisterNamespaceRequest(
                namespace=client.namespace,
                workflow_execution_retention_period={"seconds": 604800},
            )
        )
    except RPCError as error:
        if error.status != RPCStatusCode.ALREADY_EXISTS:
            raise
    desired = {
        name: IndexedValueType.INDEXED_VALUE_TYPE_KEYWORD
        for name in (
            "ClientId",
            "AccountId",
            "BusinessStep",
            "PolicyId",
            "ApprovalState",
            "CorrelationId",
        )
    }
    desired["PolicyVersion"] = IndexedValueType.INDEXED_VALUE_TYPE_INT
    existing = await client.operator_service.list_search_attributes(
        ListSearchAttributesRequest(namespace=client.namespace)
    )
    for name, kind in desired.items():
        if name in existing.custom_attributes and existing.custom_attributes[name] != kind:
            raise RuntimeError(f"Search Attribute {name} has a different type.")
    missing = {
        name: kind for name, kind in desired.items() if name not in existing.custom_attributes
    }
    if missing:
        await client.operator_service.add_search_attributes(
            AddSearchAttributesRequest(namespace=client.namespace, search_attributes=missing)
        )
    print(f"Namespace {client.namespace} and typed Search Attributes are ready.")


if __name__ == "__main__":
    asyncio.run(setup())
