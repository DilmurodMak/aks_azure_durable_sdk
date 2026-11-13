"use strict";
const element = id => document.getElementById(id);
let busy = false;
element("instance").value = `console-demo-${Date.now()}`;

async function request(path, body) {
  const response = await fetch(path, body === undefined ? {} : {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Approval-Console": "spike" },
    body: JSON.stringify(body)
  });
  const payload = await response.json();
  if (!response.ok) throw new Error(`${response.status}: ${payload.title || JSON.stringify(payload)}`);
  return payload;
}

async function showStatus(id) {
  element("status-id").value = id;
  element("result").textContent = JSON.stringify(await request(`/api/approvals/${encodeURIComponent(id)}?history=true`), null, 2);
}

async function act(action) {
  if (busy) return;
  busy = true;
  element("message").textContent = "Working...";
  try { await action(); }
  catch (error) { element("message").textContent = error.message; }
  finally { busy = false; }
}

async function refresh() {
  const approvals = await request("/api/approvals");
  const tbody = element("approvals");
  tbody.replaceChildren();
  for (const approval of approvals) {
    const row = document.createElement("tr");
    for (const text of [approval.instanceId, `${approval.planId} v${approval.version}`, approval.approver]) {
      const cell = document.createElement("td");
      cell.textContent = text;
      row.append(cell);
    }
    const actions = document.createElement("td");
    for (const outcome of ["Approve", "Reject", "Status"]) {
      const button = document.createElement("button");
      button.textContent = outcome;
      button.addEventListener("click", () => act(async () => {
        if (outcome !== "Status") {
          await request(`/api/approvals/${encodeURIComponent(approval.instanceId)}/decision`, {
            actingAs: element("actor").value, outcome,
            version: Number(element("decision-version").value || approval.version)
          });
          element("message").textContent = "Decision sent, not necessarily applied. Refresh status or watch the dashboard.";
        }
        await showStatus(approval.instanceId);
      }));
      actions.append(button);
    }
    row.append(actions);
    tbody.append(row);
  }
  if (approvals.length === 0) {
    const row = tbody.insertRow();
    const cell = row.insertCell();
    cell.colSpan = 4;
    cell.textContent = "No waiting approvals. Start one above, then refresh. Check that the selected environment's worker is running.";
  }
}

element("start").addEventListener("submit", event => {
  event.preventDefault();
  act(async () => {
    const input = Object.fromEntries(new FormData(event.target));
    for (const field of ["version", "timeoutMinutes", "applyDecisionDelaySeconds"]) input[field] = Number(input[field]);
    const result = await request("/api/approvals", input);
    element("status-id").value = result.instanceId;
    element("result").textContent = JSON.stringify(result, null, 2);
    element("message").textContent = "Started. Refresh to see the durable wait.";
    await refresh();
  });
});
element("refresh").addEventListener("click", () => act(refresh));
element("status").addEventListener("submit", event => {
  event.preventDefault();
  act(() => showStatus(element("status-id").value));
});
act(async () => {
  const environment = await request("/api/environment");
  element("environment").textContent = environment.environment;
  element("endpoint").textContent = `${environment.endpoint} | task hub: ${environment.taskHub}`;
  element("dashboard").href = environment.dashboardUrl;
  await refresh();
  element("message").textContent = environment.warning;
});
