"""One-shot private Key Vault credential transfer, using AKS Workload Identity."""

import base64
import json
import os
import ssl
from pathlib import Path
from urllib.request import Request, urlopen

from azure.identity import WorkloadIdentityCredential


def main() -> None:
    vault = os.environ["TEMPORAL_KEY_VAULT"]
    if not vault.replace("-", "").isalnum():
        raise ValueError("Invalid Key Vault name.")
    service_account = Path("/var/run/secrets/kubernetes.io/serviceaccount")
    kube_token = (service_account / "token").read_text().strip()
    kube_context = ssl.create_default_context(cafile=str(service_account / "ca.crt"))
    kube_url = (
        f"https://{os.environ['KUBERNETES_SERVICE_HOST']}:{os.environ['KUBERNETES_SERVICE_PORT']}"
    )
    with WorkloadIdentityCredential() as credential:
        token = credential.get_token("https://vault.azure.net/.default").token
        for source, target, key in [
            ("postgres-application-password", "temporal-db", "POSTGRES_PWD"),
            ("postgres-administrator-password", "temporal-db-bootstrap", "password"),
        ]:
            request = Request(
                f"https://{vault}.vault.azure.net/secrets/{source}?api-version=7.4",
                headers={"Authorization": f"Bearer {token}"},
            )
            with urlopen(request, timeout=30) as response:
                password = json.load(response)["value"]
            if not password:
                raise ValueError("Key Vault returned an empty credential.")
            request = Request(
                f"{kube_url}/api/v1/namespaces/temporal-spike/secrets/{target}",
                data=json.dumps(
                    {"data": {key: base64.b64encode(password.encode()).decode()}}
                ).encode(),
                headers={
                    "Authorization": f"Bearer {kube_token}",
                    "Content-Type": "application/merge-patch+json",
                },
                method="PATCH",
            )
            with urlopen(request, context=kube_context, timeout=30) as response:
                response.read()
            print(f"Updated {target} from private Key Vault.")


if __name__ == "__main__":
    main()
