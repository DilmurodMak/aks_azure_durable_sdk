"use strict";
const $ = id => document.getElementById(id);
let policies = { policies: [], active: [] }, cursor = null, lastQuery = "", selected = null;
const comments = new Map(), pending = new Set(), actors = new Map();
const example = () => ({
  id: "sunny-approvals", clientId: "sunny", accountId: null, baseVersion: 0,
  defaultStages: [
    { name: "parallel-review", roles: ["Finance", "Legal"], timeoutMinutes: 15 },
    { name: "lead-approval", roles: ["Lead"], timeoutMinutes: 15 }
  ], overrides: []
});
const node = (tag, text, cls) => { const n = document.createElement(tag); if (text) n.textContent = text; if (cls) n.className = cls; return n; };
function message(text, error = false) { $("message").textContent = text; $("message").classList.toggle("error", error); }
function result(data) { $("result").textContent = JSON.stringify(data, null, 2); }
function panel(id) {
  for (const section of document.querySelectorAll("main > section")) section.hidden = section.id !== id;
  for (const button of document.querySelectorAll("[data-panel]")) button.classList.toggle("selected", button.dataset.panel === id);
}
async function request(path, body) {
  const response = await fetch(path, body === undefined ? {} : {
    method: "POST", headers: { "Content-Type": "application/json", "X-Approval-Console": "spike" }, body: JSON.stringify(body)
  });
  if (!response.ok) {
    const text = await response.text();
    let detail = text;
    try { const data = JSON.parse(text); detail = data.title || data.detail || text; } catch { /* Non-JSON errors still appear explicitly. */ }
    throw new Error(`${response.status}: ${detail}`);
  }
  return response.json();
}
async function act(action) { try { await action(); } catch (error) { message(error.message, true); } }
function formData(id) {
  const values = Object.fromEntries(new FormData($(id)));
  if ("accountId" in values) values.accountId = values.accountId.trim() || null;
  return values;
}
function setPolicy(policy) {
  selected = policy || null;
  const value = policy ? {
    id: policy.id, clientId: policy.clientId, accountId: policy.accountId,
    baseVersion: Math.max(...policies.policies.filter(p => p.id === policy.id).map(p => p.version)),
    defaultStages: policy.defaultStages, overrides: policy.overrides
  } : example();
  $("policy-json").value = JSON.stringify(value, null, 2);
  $("policy-json").disabled = !!policy; $("edit-policy").disabled = !policy;
  $("save-policy").disabled = !!policy; $("activate").disabled = !policy;
}
async function loadPolicies(preferred) {
  policies = await request("/api/policies");
  $("policy-picker").replaceChildren(new Option("New policy", ""));
  for (const policy of policies.policies) {
    const active = policies.active.some(p => p.id === policy.id && p.version === policy.version);
    $("policy-picker").add(new Option(`${policy.id} v${policy.version} | ${policy.clientId}${active ? " [ACTIVE]" : ""}`, `${policy.id}:${policy.version}`));
  }
  $("policy-picker").value = preferred || "";
}
async function refresh(next = false) {
  const query = new URLSearchParams(Object.entries(formData("search-form")).filter(([, value]) => value));
  if (next && cursor) query.set("cursor", cursor);
  lastQuery = query.toString();
  const page = await request(`/api/workflows?${lastQuery}`);
  cursor = page.nextCursor; $("next-page").disabled = !cursor;
  $("runs").replaceChildren();
  for (const run of page.items) {
    const block = node("article", "", "run"), header = node("header"), title = node("div");
    title.append(node("strong", run.instanceId), node("p", `${run.workflowCode} | policy v${run.policyVersion} | ${run.runtimeStatus} / ${run.state}`),
      node("p", `Correlation: ${run.correlationId}`));
    const inspect = node("button", "Pinned policy & result");
    inspect.addEventListener("click", () => act(async () => result(await request(`/api/workflows/${run.instanceId}`))));
    header.append(title, inspect); block.append(header);
    try {
      const rows = await request(`/api/workflows/${run.instanceId}/approvals`);
      for (const row of rows) block.append(reviewRow(row, rows));
    } catch (error) { block.append(node("p", error.message)); }
    $("runs").append(block);
  }
  if (!page.items.length) $("runs").append(node("p", "No matching workflows. Start one or adjust filters."));
  result(page);
}
function reviewRow(row, all) {
  const card = node("div", "", "review"); card.dataset.state = row.state; card.dataset.id = row.instanceId;
  const summary = node("div");
  summary.append(node("strong", `${row.rank} ${row.stage} / ${row.state}`), node("div", `${row.role}: ${row.approver}`), node("small", row.instanceId));
  const controls = node("div"); card.append(summary, controls);
  if (row.canDecide) {
    const actor = node("select"); actor.setAttribute("aria-label", `Acting as for ${row.instanceId}`);
    for (const name of new Set(all.map(r => r.approver))) actor.add(new Option(name, name));
    actor.value = actors.get(row.instanceId) || row.approver;
    actor.addEventListener("change", () => actors.set(row.instanceId, actor.value));
    const comment = node("input"); comment.placeholder = "Comment (required to reject or return)"; comment.maxLength = 1000;
    comment.setAttribute("aria-label", `Comment for ${row.instanceId}`); comment.value = comments.get(row.instanceId) || "";
    comment.addEventListener("input", () => comments.set(row.instanceId, comment.value));
    controls.append(actor, comment);
    const buttons = node("div", "", "actions");
    for (const [outcome, label] of [["Approve", "Approve"], ["Return", "Return for changes"], ["Reject", "Reject"]]) {
      const button = node("button", label, outcome.toLowerCase()); button.disabled = pending.has(row.instanceId);
      button.addEventListener("click", () => act(async () => {
        if (pending.has(row.instanceId)) return;
        if (outcome !== "Approve" && !comment.value.trim()) { message("Add a comment to explain this decision.", true); return; }
        pending.add(row.instanceId); buttons.querySelectorAll("button").forEach(b => b.disabled = true);
        const before = row.progress?.refusedDecisions || 0;
        try {
          await request(`/api/approvals/${row.instanceId}/decision`, {
            actingAs: actor.value, outcome, version: row.version, comment: comment.value.trim() || null
          });
          for (let attempt = 0; attempt < 30; attempt++) {
            await new Promise(resolve => setTimeout(resolve, 750));
            const updated = await request(`/api/workflows/${row.parentInstanceId}/approvals`);
            const current = updated.find(item => item.instanceId === row.instanceId);
            if (!current.canDecide || (current.progress?.refusedDecisions || 0) > before) {
              pending.delete(row.instanceId);
              message(current.progress?.lastRefusal || `${row.instanceId}: ${current.state}`, !!current.progress?.lastRefusal);
              await refresh(); result(current); return;
            }
          }
          message("Decision sent; processing remains uncertain. Refresh before retrying.", true);
        } catch (error) { message(`${error.message}. Refresh to reconcile before retrying.`, true); }
      }));
      buttons.append(button);
    }
    controls.append(buttons);
  } else {
    const r = row.result;
    controls.append(node("p", r ? `${r.status} ${r.decidedBy || ""} ${new Date(r.decidedAt).toLocaleString()}${r.comment ? `\n"${r.comment}"` : ""}`
      : row.state === "NotStarted" ? "Never started: the chain has ended." : `Waiting for the active stage.`, "record"));
  }
  return card;
}
document.querySelectorAll("[data-panel]").forEach(b => b.addEventListener("click", () => panel(b.dataset.panel)));
$("identity-form").addEventListener("submit", e => { e.preventDefault(); act(async () => {
  const input = formData("identity-form"); input.assignments = JSON.parse(input.assignments);
  await request("/api/demo-identities", input);
  $("start-form").elements.clientId.value = input.clientId; $("start-form").elements.accountId.value = input.accountId || "";
  panel("policies"); message("Identities saved. Configure a roles-only policy.");
}); });
$("policy-picker").addEventListener("change", () => setPolicy(policies.policies.find(p => `${p.id}:${p.version}` === $("policy-picker").value)));
$("new-policy").addEventListener("click", () => { $("policy-picker").value = ""; setPolicy(null); });
$("edit-policy").addEventListener("click", () => { $("policy-json").disabled = false; $("save-policy").disabled = false; });
$("policy-form").addEventListener("submit", e => { e.preventDefault(); act(async () => {
  const saved = await request("/api/policies", JSON.parse($("policy-json").value));
  await loadPolicies(`${saved.id}:${saved.version}`); setPolicy(saved);
  $("start-form").elements.clientId.value = saved.clientId; $("start-form").elements.accountId.value = saved.accountId || "";
  message(`${saved.id} v${saved.version} is active for new starts. Existing chains stay pinned.`);
}); });
$("activate").addEventListener("click", () => act(async () => {
  await request(`/api/policies/${selected.id}/activate?version=${selected.version}`, {});
  await loadPolicies(`${selected.id}:${selected.version}`); message("Saved version activated for new starts.");
}));
$("start-form").addEventListener("submit", e => { e.preventDefault(); act(async () => {
  const input = formData("start-form"); input.version = Number(input.version); input.correlationId = input.correlationId.trim() || null;
  const started = await request("/api/workflows", input);
  panel("approvals"); await refresh(); result(started); message(`Started ${started.instanceId}; refresh after the worker starts its reviewers.`);
  $("instance-id").value = `temporal-${Date.now()}`;
}); });
$("search-form").addEventListener("submit", e => { e.preventDefault(); act(async () => { await refresh(); message("Indexed search refreshed."); }); });
$("next-page").addEventListener("click", () => act(() => refresh(true)));
act(async () => {
  const environment = await request("/api/environment");
  $("temporal-ui").href = environment.temporalUiUrl;
  await request("/api/health"); await loadPolicies(); setPolicy(null);
  for (const step of await request("/api/business-steps")) $("step-picker").add(new Option(`${step.code} | ${step.name}`, step.code));
  $("step-picker").value = "B07"; $("instance-id").value = `temporal-${Date.now()}`;
  message(`${environment.deployment}: ready. Set up identities, save a policy, then start an approval.`);
});
