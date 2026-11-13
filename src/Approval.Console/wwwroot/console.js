"use strict";
const element = id => document.getElementById(id);
let busy = false;
let registry = { policies: [], active: [] };
let selectedKey = "";
let editorMode = "new";
let editBaseline = "";
let identityRegistry = { assignments: [] };
let waitingApprovals = [];
let workflowApprovals = [];
let selectedWorkflow = "";
let workflowChoiceSet = false;
const pendingDecisions = new Map();
const finishedApprovals = new Set();
element("chain-instance").value = `chain-demo-${Date.now()}`;

function icon(name) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.classList.add("icon");
  svg.setAttribute("aria-hidden", "true");
  const use = document.createElementNS(svg.namespaceURI, "use");
  use.setAttribute("href", `#icon-${name}`);
  svg.append(use);
  return svg;
}

function button(text, symbol, action, className = "") {
  const control = document.createElement("button");
  control.type = "button";
  control.className = className;
  control.append(icon(symbol), document.createTextNode(text));
  control.addEventListener("click", action);
  return control;
}

function message(text, error = false) {
  element("message").textContent = text;
  element("message").classList.toggle("error", error);
}

function showPanel(name, focus = false) {
  for (const tab of document.querySelectorAll("[role=tab]")) {
    const selected = tab.dataset.panel === name;
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
    element(`panel-${tab.dataset.panel}`).hidden = !selected;
    if (selected && focus) tab.focus();
  }
}

const tabs = [...document.querySelectorAll("[role=tab]")];
tabs.forEach((tab, index) => {
  tab.addEventListener("click", () => showPanel(tab.dataset.panel));
  tab.addEventListener("keydown", event => {
    const next = event.key === "ArrowRight" ? (index + 1) % tabs.length
      : event.key === "ArrowLeft" ? (index + tabs.length - 1) % tabs.length
      : event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 : -1;
    if (next >= 0) { event.preventDefault(); showPanel(tabs[next].dataset.panel, true); }
  });
});

async function request(path, body) {
  const response = await fetch(path, body === undefined ? {} : {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Approval-Console": "spike" },
    body: JSON.stringify(body)
  });
  const payload = await response.json();
  if (!response.ok) {
    const validation = payload.errors ? Object.values(payload.errors).flat().join(" ") : "";
    const error = new Error(`${response.status}: ${validation || payload.title || "Request failed"}`);
    error.httpStatus = response.status;
    throw error;
  }
  return payload;
}

async function act(action) {
  if (busy) return;
  busy = true;
  const controls = [...document.querySelectorAll("button, #policies")].map(control => [control, control.disabled]);
  controls.forEach(([control]) => { control.disabled = true; });
  message("Working...");
  try { await action(); }
  catch (error) { message(error.message, true); }
  finally {
    controls.forEach(([control, disabled]) => { if (control.isConnected) control.disabled = disabled; });
    element("add-stage").disabled = editorMode === "view" || element("stages").children.length >= 10;
    busy = false;
  }
}

function showResult(payload) {
  element("result").textContent = JSON.stringify(payload, null, 2);
}

async function showStatus(id) {
  element("status-id").value = id;
  const status = await request(`/api/approvals/${encodeURIComponent(id)}?history=true`);
  showResult(status);
  if (isFinished(status)) {
    pendingDecisions.delete(id);
    finishedApprovals.add(id);
    await refresh();
  }
  return status;
}

function stageDrafts() {
  return [...element("stages").children].map(card => ({
    name: card.querySelector("[data-field=name]").value.trim(),
    timeoutMinutes: Number(card.querySelector("[data-field=timeout]").value),
    reviewers: [...card.querySelectorAll(".reviewer-row")].map(row => ({
      role: row.querySelector("[data-field=role]").value.trim()
    }))
  }));
}

function inputField(labelText, value, field, accessibleName, type = "text") {
  const label = document.createElement("label");
  label.append(document.createTextNode(labelText));
  const input = document.createElement("input");
  input.value = value;
  input.type = type;
  input.required = true;
  input.dataset.field = field;
  input.setAttribute("aria-label", accessibleName);
  if (type === "number") {
    input.min = "0.001"; input.max = "1440"; input.step = "any";
  } else if (field !== "actor") {
    input.maxLength = 80; input.pattern = "[A-Za-z0-9\\-]+";
  } else {
    input.maxLength = 100;
  }
  label.append(input);
  return label;
}

function renderStages(stages) {
  const container = element("stages");
  container.replaceChildren();
  stages.forEach((stage, index) => {
    const number = index + 1;
    const card = document.createElement("div");
    card.className = "stage-card";
    const heading = document.createElement("div");
    heading.className = "stage-heading";
    const badge = document.createElement("span");
    badge.className = "stage-number";
    badge.textContent = `STAGE ${number}`;
    const title = document.createElement("strong");
    title.textContent = index === 0 ? "Starts first" : `After stage ${index} finishes`;
    heading.append(badge, title);
    for (const [direction, offset] of [["up", -1], ["down", 1]]) {
      const move = button("", direction, () => {
        const draft = stageDrafts();
        [draft[index], draft[index + offset]] = [draft[index + offset], draft[index]];
        renderStages(draft);
      }, "compact");
      move.setAttribute("aria-label", `Move stage ${number} ${direction}`);
      move.disabled = index + offset < 0 || index + offset >= stages.length;
      heading.append(move);
    }
    const removeStage = button("", "close", () => {
      const draft = stageDrafts(); draft.splice(index, 1); renderStages(draft);
    }, "compact");
    removeStage.setAttribute("aria-label", `Remove stage ${number}`);
    removeStage.disabled = stages.length === 1;
    heading.append(removeStage);
    const fields = document.createElement("div");
    fields.className = "stage-fields";
    fields.append(inputField("Stage name", stage.name, "name", `Stage ${number} name`),
      inputField("Timeout (minutes)", stage.timeoutMinutes, "timeout", `Stage ${number} timeout`, "number"));
    const reviewers = document.createElement("div");
    reviewers.className = "reviewer-list";
    stage.reviewers.forEach((reviewer, reviewerIndex) => {
      const row = document.createElement("div");
      row.className = "reviewer-row policy-reviewer-row";
      const remove = button("", "close", () => {
        const draft = stageDrafts(); draft[index].reviewers.splice(reviewerIndex, 1); renderStages(draft);
      }, "compact");
      remove.setAttribute("aria-label", `Remove reviewer ${reviewerIndex + 1} from stage ${number}`);
      remove.disabled = stage.reviewers.length === 1;
      row.append(inputField("Approver role", reviewer.role, "role", `Stage ${number} reviewer ${reviewerIndex + 1} role`), remove);
      reviewers.append(row);
    });
    const add = button("Add parallel reviewer", "plus", () => {
      const draft = stageDrafts(); draft[index].reviewers.push({ role: "" }); renderStages(draft);
    });
    add.disabled = stage.reviewers.length >= 10;
    reviewers.append(add);
    card.append(heading, fields, reviewers);
    container.append(card);
  });
  element("add-stage").disabled = stages.length >= 10;
}

function setScopeEditable(editable) {
  for (const key of ["id", "clientId", "accountId", "workflowCode"])
    element("policy-editor").elements[key].readOnly = !editable;
}

function setEditorMode(mode) {
  editorMode = mode;
  element("policy-fields").disabled = mode === "view";
  element("edit-policy").hidden = !selectedKey || mode !== "view";
  element("policy-action").hidden = mode === "view";
  element("policy-action-label").textContent = mode === "edit" ? "Save" : "Save & activate";
  element("cancel-edit").hidden = mode !== "edit";
  element("add-stage").disabled = mode === "view" || element("stages").children.length >= 10;
}

function policyInput() {
  const form = element("policy-editor");
  const input = {
    id: form.elements.id.value.trim(), clientId: form.elements.clientId.value.trim(),
    accountId: form.elements.accountId.value.trim() || null,
    workflowCode: form.elements.workflowCode.value.trim(),
    baseVersion: Number(form.elements.baseVersion.value)
  };
  input.stages = stageDrafts().map(stage => ({
    name: stage.name, timeoutMinutes: stage.timeoutMinutes,
    roles: stage.reviewers.map(reviewer => reviewer.role)
  }));
  return input;
}

function policyFingerprint(input) {
  return JSON.stringify({
    id: input.id, clientId: input.clientId, accountId: input.accountId, workflowCode: input.workflowCode,
    stages: input.stages
  });
}

function newPolicy(example = false) {
  selectedKey = "";
  element("policies").value = "";
  const form = element("policy-editor");
  form.reset();
  form.elements.id.value = example
    ? (registry.policies.some(policy => policy.id === "sunny-gate2") ? `example-${Date.now()}` : "sunny-gate2") : "";
  setScopeEditable(true);
  renderStages(example ? [
    { name: "account-lead", timeoutMinutes: 15, reviewers: [{ role: "GroupAccountLead" }] },
    { name: "review", timeoutMinutes: 15, reviewers: [{ role: "FinanceController" }, { role: "Legal" }] },
    { name: "client", timeoutMinutes: 15, reviewers: [{ role: "ClientApprover" }] }
  ] : [{ name: "review", timeoutMinutes: 15, reviewers: [{ role: "Approver" }] }]);
  setEditorMode("new");
  element("policy-badge").className = "badge subtle";
  element("policy-badge").textContent = "Not saved";
  element("policy-message").textContent = example
    ? "Example ready. Adjust the fields, then Save & activate."
    : "New policy. Saving creates and activates version 1.";
  element("start-policy-hint").textContent = "Save this policy to use it for new starts. Previously saved policies stay active until then.";
}

function populatePolicy(policy) {
  const form = element("policy-editor");
  for (const key of ["id", "clientId", "workflowCode"]) form.elements[key].value = policy[key];
  form.elements.accountId.value = policy.accountId || "";
  form.elements.baseVersion.value = Math.max(...registry.policies.filter(item => item.id === policy.id).map(item => item.version));
  setScopeEditable(false);
  renderStages(policy.stages.map(stage => ({
    name: stage.name, timeoutMinutes: stage.timeoutMinutes,
    reviewers: stage.roles.map(role => ({ role }))
  })));
  editBaseline = policyFingerprint(policyInput());
  setEditorMode("view");
  const active = registry.active.some(item => item.id === policy.id && item.version === policy.version);
  element("policy-badge").textContent = `${active ? "Active" : "Saved"} v${policy.version}`;
  element("policy-badge").className = `badge ${active ? "active" : "subtle"}`;
  element("policy-message").textContent = `${policy.id} v${policy.version} is read-only. Choose Edit to change its stages or reviewers.`;
  const start = element("chain-start");
  for (const key of ["clientId", "workflowCode"]) start.elements[key].value = policy[key];
  start.elements.accountId.value = policy.accountId || "";
  const simulator = element("identity-editor");
  simulator.elements.clientId.value = policy.clientId;
  simulator.elements.accountId.value = policy.accountId || "";
  renderIdentityScope();
  element("start-policy-hint").textContent = `${policy.id} v${policy.version} | ${policy.stages.length} stage(s) | ${policy.stages.reduce((count, stage) => count + stage.roles.length, 0)} approval(s). Assign users separately in Demo identities; the resolved policy is pinned when you start.`;
}

async function loadPolicies(preferred) {
  registry = await request("/api/policies");
  const select = element("policies");
  select.replaceChildren(new Option("Create a new policy", ""));
  for (const policy of [...registry.policies].sort((a, b) => a.id.localeCompare(b.id) || b.version - a.version)) {
    const active = registry.active.some(item => item.id === policy.id && item.version === policy.version);
    select.add(new Option(`${policy.id} v${policy.version} | ${policy.clientId}/${policy.accountId || "all-accounts"}/${policy.workflowCode}${active ? " [ACTIVE]" : ""}`, `${policy.id}:${policy.version}`));
  }
  const reference = registry.active.at(-1);
  const key = preferred || selectedKey || (reference ? `${reference.id}:${reference.version}` : "");
  const policy = registry.policies.find(item => `${item.id}:${item.version}` === key);
  if (policy) { selectedKey = key; select.value = key; populatePolicy(policy); }
  else newPolicy();
}

element("policies").addEventListener("change", () => {
  const key = element("policies").value;
  if (!key) { newPolicy(); return; }
  act(async () => {
    const policy = registry.policies.find(item => `${item.id}:${item.version}` === key);
    try {
      await request(`/api/policies/${encodeURIComponent(policy.id)}/activate`, { version: policy.version });
    } catch (error) {
      element("policies").value = selectedKey;
      throw error;
    }
    await loadPolicies(key);
    message(`${policy.id} v${policy.version} is active for new starts. Existing runs are unchanged.`);
  });
});
element("new-policy").addEventListener("click", () => { if (!busy) { newPolicy(); message("Create a new policy."); } });
element("load-example").addEventListener("click", () => { if (!busy) { newPolicy(true); message("Example loaded. Nothing is saved until you choose Save & activate."); } });
element("add-stage").addEventListener("click", () => {
  if (editorMode === "view") return;
  const draft = stageDrafts();
  draft.push({ name: `stage-${draft.length + 1}`, timeoutMinutes: 15, reviewers: [{ role: "" }] });
  renderStages(draft);
});
element("edit-policy").addEventListener("click", event => {
  if (editorMode !== "view" || busy) return;
  event.preventDefault();
  setEditorMode("edit");
  message("Editing enabled. Save only creates a new version when content changes. Policy ID and scope stay fixed.");
  element("policy-message").textContent = "Edit stages, reviewers, or timeouts, then Save. Cancel discards your changes.";
});
element("cancel-edit").addEventListener("click", () => {
  const policy = registry.policies.find(item => `${item.id}:${item.version}` === selectedKey);
  if (policy) populatePolicy(policy);
  message("Changes discarded. Saved policy is read-only.");
});
element("policy-editor").addEventListener("submit", event => {
  event.preventDefault();
  if (editorMode === "view") return;
  act(async () => {
    const input = policyInput();
    if (editorMode === "edit" && policyFingerprint(input) === editBaseline) {
      populatePolicy(registry.policies.find(item => `${item.id}:${item.version}` === selectedKey));
      message("No changes to save. No new version was created.");
      return;
    }
    const saved = await request("/api/policies", input);
    await loadPolicies(`${saved.id}:${saved.version}`);
    message(saved.version === input.baseVersion ? "No changes to save. No new version was created."
      : `Saved and activated ${saved.id} v${saved.version}. Ready for new workflow starts.`);
    showResult({ policyId: saved.id, version: saved.version, active: true, storage: element("policy-location").textContent });
  });
});

function identityDrafts() {
  return [...element("identity-rows").children].map(row => ({
    role: row.querySelector("[data-field=role]").value.trim(),
    actor: row.querySelector("[data-field=actor]").value.trim()
  }));
}

function renderIdentities(assignments) {
  const container = element("identity-rows");
  container.replaceChildren();
  assignments.forEach((assignment, index) => {
    const row = document.createElement("div");
    row.className = "reviewer-row demo-identity-row";
    const remove = button("", "close", () => {
      const draft = identityDrafts(); draft.splice(index, 1); renderIdentities(draft);
    }, "compact");
    remove.setAttribute("aria-label", `Remove demo assignment ${index + 1}`);
    row.append(
      inputField("Role", assignment.role, "role", `Demo assignment ${index + 1} role`),
      inputField("Simulated user", assignment.actor, "actor", `Demo assignment ${index + 1} user`), remove);
    container.append(row);
  });
}

function renderIdentityScope() {
  const form = element("identity-editor");
  const client = form.elements.clientId.value.trim();
  const account = form.elements.accountId.value.trim() || null;
  const scoped = identityRegistry.assignments.filter(item => item.clientId === client && item.accountId === account);
  renderIdentities(scoped);
  element("identity-message").textContent = `${scoped.length} saved demo assignment(s) for ${client}/${account || "all-accounts"}. Separate from policy configuration.`;
}

function updateActorChoices() {
  const select = element("actor");
  const previous = select.value;
  const assignments = selectedWorkflow
    ? workflowApprovals.map(item => ({ role: item.role, actor: item.approver }))
    : waitingApprovals.length
      ? waitingApprovals.map(item => ({ role: item.role || "Approver", actor: item.approver }))
      : identityRegistry.assignments;
  const actors = new Map();
  for (const assignment of assignments) {
    if (!actors.has(assignment.actor)) actors.set(assignment.actor, new Set());
    actors.get(assignment.actor).add(assignment.role);
  }
  select.replaceChildren(new Option(actors.size ? "Select a demo user" : "No assignments - configure Demo identities", ""));
  for (const [actor, roles] of actors)
    select.add(new Option(`${[...roles].join(", ")} - ${actor}`, actor));
  const preferred = workflowApprovals.find(item => item.canDecide)?.approver || waitingApprovals[0]?.approver;
  select.value = actors.has(previous) ? previous : actors.has(preferred) ? preferred : "";
  select.disabled = actors.size === 0;
}

async function loadIdentitySettings() {
  identityRegistry = await request("/api/demo-identities");
  renderIdentityScope();
  updateActorChoices();
}

element("load-identities").addEventListener("click", () => act(async () => {
  await loadIdentitySettings();
  message("Demo identity settings loaded. Approval policies were not changed.");
}));
element("use-policy-roles").addEventListener("click", () => {
  const form = element("identity-editor");
  const client = form.elements.clientId.value.trim();
  const account = form.elements.accountId.value.trim() || null;
  const roles = [...new Set(stageDrafts().flatMap(stage => stage.reviewers.map(item => item.role)).filter(Boolean))];
  renderIdentities(roles.map(role => {
    const existing = identityRegistry.assignments.find(item =>
      item.clientId === client && item.accountId === account && item.role === role)
      || identityRegistry.assignments.find(item =>
        item.clientId === client && item.accountId === null && item.role === role);
    return { role, actor: existing?.actor || "" };
  }));
  message("Policy roles copied to the simulator. Enter users and Save demo identities; the policy itself is unchanged.");
});
element("identity-example").addEventListener("click", () => {
  renderIdentities([
    { role: "GroupAccountLead", actor: "lead" },
    { role: "FinanceController", actor: "finance" },
    { role: "Legal", actor: "legal" },
    { role: "ClientApprover", actor: "client" },
    { role: "Approver", actor: "finance" }
  ]);
  message("Example identities loaded in the simulator only. Save demo identities to use them.");
});
element("add-identity").addEventListener("click", () => {
  renderIdentities([...identityDrafts(), { role: "", actor: "" }]);
});
element("identity-editor").addEventListener("submit", event => {
  event.preventDefault();
  act(async () => {
    const form = event.target;
    const assignments = Object.create(null);
    for (const item of identityDrafts()) {
      if (Object.hasOwn(assignments, item.role)) throw new Error(`Duplicate demo role: ${item.role}`);
      assignments[item.role] = item.actor;
    }
    identityRegistry = await request("/api/demo-identities", {
      clientId: form.elements.clientId.value.trim(),
      accountId: form.elements.accountId.value.trim() || null, assignments
    });
    renderIdentityScope(); updateActorChoices();
    message("Demo identities saved separately. No policy version was created; running snapshots are unchanged.");
  });
});

function isFinished(status) {
  return ["Completed", "Failed", "Terminated", "Canceled"].includes(status.runtimeStatus);
}

function invalidDecisionReason(approval, decision) {
  if (decision.actingAs.toLowerCase() === approval.requester.toLowerCase()) return "Self-approval is not allowed.";
  if (decision.actingAs.toLowerCase() !== approval.approver.toLowerCase()) return `This approval is assigned to ${approval.approver}.`;
  if (decision.version !== approval.version) return `Use plan version ${approval.version}, not ${decision.version}.`;
  return null;
}

async function submitDecision(approval, outcome) {
  const id = approval.instanceId;
  if (busy || pendingDecisions.has(id)) return;
  if (approval.canDecide === false) {
    message(approval.blockedReason || "This step is not waiting for a decision.", true);
    return;
  }
  const decision = {
    actingAs: element("actor").value.trim(), outcome,
    version: Number(element("decision-version").value || approval.version)
  };
  if (!decision.actingAs || !Number.isInteger(decision.version) || decision.version < 1) {
    message("Enter a demo actor and a positive decision version.", true);
    return;
  }
  pendingDecisions.set(id, `Sending ${outcome}...`);
  renderApprovals();
  let sent = false;
  try {
    await request(`/api/approvals/${encodeURIComponent(id)}/decision`, decision);
    sent = true;
    pendingDecisions.set(id, "Decision sent. Waiting for worker...");
    renderApprovals();
    if (approval.parentInstanceId) element("chain-status-id").value = approval.parentInstanceId;
    element("status-id").value = id;
    const invalidReason = invalidDecisionReason(approval, decision);
    for (let attempt = 0; attempt < 20; attempt++) {
      const status = await request(`/api/approvals/${encodeURIComponent(id)}?history=true`);
      showResult(status);
      if (isFinished(status)) {
        finishedApprovals.add(id);
        pendingDecisions.delete(id);
        await refresh();
        message(`${id}: ${status.result?.status || status.runtimeStatus}. ${selectedWorkflow ? "Completed step retained in the plan." : "Removed from waiting approvals."}`);
        return;
      }
      const received = status.history?.some(event => event.eventType === "EventRaised" && event.name === "Decision" &&
        JSON.stringify(JSON.parse(event.input || "{}")) === JSON.stringify({
          Approver: decision.actingAs, Outcome: decision.outcome, Version: decision.version
        }));
      if (invalidReason && received && status.customStatus?.state === "Waiting") {
        pendingDecisions.delete(id);
        await refresh();
        message(`${invalidReason} The workflow is still waiting; correct the actor/version and retry.`, true);
        return;
      }
      await new Promise(resolve => setTimeout(resolve, 750));
    }
    message("The decision was sent but is not yet resolved. Decision buttons remain locked; use View or refresh to check the worker.", true);
  } catch (error) {
    if (!sent && error.httpStatus >= 400 && error.httpStatus < 500)
      pendingDecisions.delete(id);
    else
      pendingDecisions.set(id, "Status uncertain. Use View to reconcile before sending again.");
    message(`Decision status is uncertain: ${error.message}. Check View before retrying.`, true);
  } finally {
    renderApprovals();
  }
}

async function refresh() {
  waitingApprovals = await request("/api/approvals");
  const workflows = await request("/api/workflows");
  const select = element("approval-workflow");
  if (!workflowChoiceSet && workflows.length) {
    selectedWorkflow = (workflows.find(item => ["Running", "Pending", "Suspended"].includes(item.runtimeStatus)) || workflows[0]).instanceId;
  }
  select.replaceChildren(new Option("All waiting approvals", ""));
  for (const workflow of workflows) {
    select.add(new Option(`${workflow.instanceId} | ${workflow.planId} v${workflow.version} | ${workflow.runtimeStatus}`, workflow.instanceId));
  }
  if (selectedWorkflow && !workflows.some(item => item.instanceId === selectedWorkflow))
    select.add(new Option(selectedWorkflow, selectedWorkflow));
  select.value = selectedWorkflow;
  const workflowId = selectedWorkflow;
  const planned = workflowId
    ? await request(`/api/workflows/${encodeURIComponent(workflowId)}/approvals`) : [];
  if (selectedWorkflow !== workflowId) return;
  workflowApprovals = planned;
  if (selectedWorkflow) element("chain-status-id").value = selectedWorkflow;
  renderApprovals();
}

function renderApprovals() {
  const approvals = selectedWorkflow ? workflowApprovals
    : waitingApprovals.filter(approval => !finishedApprovals.has(approval.instanceId));
  element("waiting-count").textContent = waitingApprovals.filter(approval => !finishedApprovals.has(approval.instanceId)).length;
  const tbody = element("approvals");
  updateActorChoices();
  tbody.replaceChildren();
  for (const approval of approvals) {
    const row = document.createElement("tr");
    const state = approval.state || "Waiting";
    row.dataset.state = state;
    row.dataset.approvalId = approval.instanceId;
    row.className = approval.canDecide === false ? "approval-inactive" : "";
    if (["Approved", "Rejected", "Timeout", "Failed", "Terminated"].includes(state))
      row.className = "approval-finished";
    for (const text of [
      approval.rank || String(approval.progress?.stageNumber || "-"),
      approval.instanceId + (approval.parentInstanceId ? `\nParent: ${approval.parentInstanceId}` : ""),
      `${approval.planId}\nv${approval.version}`,
      `${approval.progress?.stage || "Review"} | ${state}${approval.policyVersion ? `\nPolicy v${approval.policyVersion}` : ""}`,
      `${approval.role ? approval.role + "\n" : ""}${approval.approver}`
    ]) {
      const cell = document.createElement("td");
      cell.textContent = text;
      row.append(cell);
    }
    const cell = document.createElement("td");
    const actions = document.createElement("div");
    actions.className = "row-actions";
    const pending = pendingDecisions.get(approval.instanceId);
    for (const outcome of ["Approve", "Reject", "View"]) {
      if (outcome !== "View" && ["Approved", "Rejected", "Timeout", "Failed", "Terminated", "Completed", "Canceled"].includes(state))
        continue;
      const action = button(outcome, outcome === "Approve" ? "check" : outcome === "Reject" ? "close" : "info", () => {
        if (outcome !== "View") { void submitDecision(approval, outcome); return; }
        void act(async () => {
        if (approval.parentInstanceId) element("chain-status-id").value = approval.parentInstanceId;
        await showStatus(approval.instanceId);
        message("Approval status and history shown below.");
        });
      }, outcome === "View" ? "" : outcome.toLowerCase());
      action.disabled = outcome === "View" ? approval.canView === false
        : !!pending || approval.canDecide === false;
      if (action.disabled && approval.blockedReason) action.title = approval.blockedReason;
      actions.append(action);
    }
    cell.append(actions);
    if (pending) {
      const hint = document.createElement("p");
      hint.className = "hint decision-pending";
      hint.textContent = pending;
      hint.setAttribute("role", "status");
      cell.append(hint);
    }
    if (approval.blockedReason) {
      const reason = document.createElement("p");
      reason.className = "hint blocked-reason";
      reason.textContent = approval.blockedReason;
      cell.append(reason);
    }
    row.append(cell);
    tbody.append(row);
  }
  if (!approvals.length) {
    const cell = tbody.insertRow().insertCell();
    cell.colSpan = 6; cell.className = "empty-state";
    cell.textContent = selectedWorkflow ? "No approval steps were found for this workflow."
      : "No approvals waiting. Start a workflow, then refresh. Its worker must be running.";
  }
}

element("chain-start").addEventListener("submit", event => {
  event.preventDefault();
  act(async () => {
    const input = Object.fromEntries(new FormData(event.target));
    input.accountId = input.accountId || null; input.version = Number(input.version);
    const result = await request("/api/workflows", input);
    selectedWorkflow = result.instanceId;
    workflowChoiceSet = true;
    element("chain-status-id").value = result.instanceId;
    showResult(result);
    message(`Started ${result.instanceId} with pinned policy v${result.policyVersion}. Refresh to see its active stage.`);
    await refresh();
    showPanel("approvals");
  });
});
element("chain-status").addEventListener("submit", event => {
  event.preventDefault();
  act(async () => {
    const id = element("chain-status-id").value;
    showResult(await request(`/api/workflows/${encodeURIComponent(id)}`));
    selectedWorkflow = id;
    workflowChoiceSet = true;
    await refresh();
    message("Workflow progress and its original policy snapshot are shown below.");
  });
  element("approval-workflow").addEventListener("change", () => {
    selectedWorkflow = element("approval-workflow").value;
    workflowChoiceSet = true;
    void act(async () => { await refresh(); message(selectedWorkflow ? "Full pinned approval plan loaded." : "All waiting approvals loaded."); });
  });
});
element("refresh").addEventListener("click", () => act(async () => { await refresh(); message("Waiting approvals refreshed."); }));
element("status").addEventListener("submit", event => {
  event.preventDefault();
  act(async () => { await showStatus(element("status-id").value); message("Approval status and history shown below."); });
});
act(async () => {
  const environment = await request("/api/environment");
  element("environment").textContent = environment.environment;
  element("environment").classList.toggle("azure", environment.environment === "AZURE");
  element("endpoint").textContent = `${environment.endpoint} | task hub: ${environment.taskHub}`;
  element("policy-location").textContent = `approval_policies/${environment.environment === "AZURE" ? "azure" : "local"}.json`;
  element("dashboard").href = environment.dashboardUrl;
  await loadIdentitySettings(); await loadPolicies(); await refresh();
  message("Ready. Create a policy, start a workflow, then review approvals.");
});
