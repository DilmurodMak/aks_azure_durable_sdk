import asyncio
import os
import signal
from datetime import timedelta

from temporalio.worker import Worker

from approval.activities import apply_decision, record_decision, record_submission
from approval.runtime import configure_telemetry, connect
from approval.workflows import ApprovalChain, ApprovalStep


async def main() -> None:
    provider = configure_telemetry("temporal-approval-worker")
    client = await connect()
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for signum in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(signum, stop.set)
    try:
        async with Worker(
            client,
            task_queue=os.getenv("TEMPORAL_TASK_QUEUE", "approval"),
            workflows=[ApprovalChain, ApprovalStep],
            activities=[record_submission, record_decision, apply_decision],
            graceful_shutdown_timeout=timedelta(seconds=10),
        ):
            await stop.wait()
    finally:
        for signum in (signal.SIGTERM, signal.SIGINT):
            loop.remove_signal_handler(signum)
        provider.shutdown()


if __name__ == "__main__":
    asyncio.run(main())
