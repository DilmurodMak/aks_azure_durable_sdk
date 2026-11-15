"use strict";
const element = id => document.getElementById(id);
let busy = false;
let registry = { policies: [], active: [] };
let selectedKey = "";
let editorMode = "new";
let editBaseline = "";
let identityRegistry = { assignments: [] };
let businessSteps = [];
let workflowBlocks = [];
let startedWorkflow = "";
let refreshCounter = 0;
const pendingDecisions = new Map();
const FINISHED_STEP_STATES = ["Approved", "Rejected", "Returned", "Timeout", "Failed", "Terminated", "Completed", "Canceled", "NotStarted"];
const DECISION_ACTIONS = [
  { outcome: "Approve", label: "Approve", symbol: "check", className: "approve" },
  { outcome: "Return", label: "Return for changes", symbol: "return", className: "return" },
  { outcome: "Reject", label: "Reject", symbol: "close", className: "reject" }
];
const COMMENT_REQUIRED = ["Reject", "Return"];
const draftComments = new Map();
const FINISHED_RUN_STATES = ["Completed", "Failed", "Terminated", "Canceled"];
const ACTIVE_RUN_STATES = ["Running", "Pending", "Suspended"];
const MAX_WORKFLOWS_SHOWN = 6;
element("chain-instance").value = `chain-demo-${Date.now()}`;

const policyKey = policy => `${policy.id}:${policy.version}`;
const policyByKey = key => registry.policies.find(item => policyKey(item) === key);
const isActivePolicy = policy => registry.active.some(item => item.id === policy.id && item.version === policy.version);

function scopeOf(form) {
  return { client: form.elements.clientId.value.trim(), account: form.elements.accountId.value.trim() || null };
}

function ensureOption(select, value, label) {
  if (value && ![...select.options].some(option => option.value === value)) select.add(new Option(label, value));
}

function fillStepSelect(select, excluded = []) {
  select.replaceChildren(new Option("Choose a business step", ""));
  for (const phase of new Set(businessSteps.map(step => step.phaseName))) {
    const group = document.createElement("optgroup");
    group.label = phase;
    for (const step of businessSteps.filter(item => item.phaseName === phase && !excluded.includes(item.code)))
      group.append(new Option(`${step.code} | ${step.name}`, step.code));
    if (group.children.length) select.append(group);
  }
}

function setStep(select, code) {
  select.value = code || "";
}

const MAX_OVERRIDES = 50;
const blankStage = () => ({ name: "review", timeoutMinutes: 15, reviewers: [{ role: "" }] });
const toDraftStage = stage => ({ name: stage.name, timeoutMinutes: stage.timeoutMinutes, reviewers: stage.roles.map(role => ({ role })) });
const toApiStages = drafts => drafts.map(stage => ({
  name: stage.name, timeoutMinutes: stage.timeoutMinutes, roles: stage.reviewers.map(reviewer => reviewer.role)
}));
const policyRoles = policy => [...(policy.defaultStages ?? []), ...policy.overrides.flatMap(item => item.stages)]
  .flatMap(stage => stage.roles);
const policySummary = policy =>
  `${policy.defaultStages ? "default chain" : "no default"} + ${policy.overrides.length} override(s)`;

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
    syncOverrideButton();
    busy = false;
  }
}

function showResult(payload, reveal = false) {
  element("result").textContent = JSON.stringify(payload, null, 2);
  element("result-time").textContent = `Updated ${new Date().toLocaleTimeString()}`;
  if (reveal) element("result-section").scrollIntoView({ block: "nearest", behavior: "smooth" });
}

async function showStatus(id) {
  const status = await request(`/api/approvals/${encodeURIComponent(id)}?history=true`);
  showResult(status, true);
  if (isFinished(status)) {
    pendingDecisions.delete(id);
    await refresh();
  }
  return status;
}

function syncOverrideButton() {
  const count = element("overrides").children.length;
  element("add-override").disabled = count >= Math.min(MAX_OVERRIDES, businessSteps.length || MAX_OVERRIDES);
  element("overrides-empty").hidden = count > 0;
}

function chainDrafts(container) {
  return [...container.querySelectorAll(":scope > .stage-card")].map(card => ({
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

function rolesForScope(client, account) {
  return [...new Set(identityRegistry.assignments
    .filter(item => (item.clientId === client && (item.accountId === null || item.accountId === account)) ||
      (item.clientId === "agency-default" && item.accountId === null))
    .map(item => item.role))];
}

function roleField(value, accessibleName) {
  const label = document.createElement("label");
  label.append(document.createTextNode("Approver role"));
  const select = document.createElement("select");
  select.required = true;
  select.dataset.field = "role";
  select.setAttribute("aria-label", accessibleName);
  const { client, account } = scopeOf(element("policy-editor"));
  const roles = rolesForScope(client, account);
  select.add(new Option(roles.length || value ? "Choose a role" : "No roles yet - set up Identities first", ""));
  for (const role of roles) select.add(new Option(role, role));
  ensureOption(select, value, `${value} (no user assigned)`);
  select.value = value;
  label.append(select);
  return label;
}

function renderChain(container, stages, label) {
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
        const draft = chainDrafts(container);
        [draft[index], draft[index + offset]] = [draft[index + offset], draft[index]];
        renderChain(container, draft, label);
      }, "compact");
      move.setAttribute("aria-label", `${label} move stage ${number} ${direction}`);
      move.disabled = index + offset < 0 || index + offset >= stages.length;
      heading.append(move);
    }
    const removeStage = button("", "close", () => {
      const draft = chainDrafts(container); draft.splice(index, 1); renderChain(container, draft, label);
    }, "compact");
    removeStage.setAttribute("aria-label", `${label} remove stage ${number}`);
    removeStage.disabled = stages.length === 1;
    heading.append(removeStage);
    const fields = document.createElement("div");
    fields.className = "stage-fields";
    fields.append(inputField("Stage name", stage.name, "name", `${label} stage ${number} name`),
      inputField("Timeout (minutes)", stage.timeoutMinutes, "timeout", `${label} stage ${number} timeout`, "number"));
    const reviewers = document.createElement("div");
    reviewers.className = "reviewer-list";
    stage.reviewers.forEach((reviewer, reviewerIndex) => {
      const row = document.createElement("div");
      row.className = "reviewer-row policy-reviewer-row";
      const remove = button("", "close", () => {
        const draft = chainDrafts(container); draft[index].reviewers.splice(reviewerIndex, 1); renderChain(container, draft, label);
      }, "compact");
      remove.setAttribute("aria-label", `${label} remove reviewer ${reviewerIndex + 1} from stage ${number}`);
      remove.disabled = stage.reviewers.length === 1;
      row.append(roleField(reviewer.role, `${label} stage ${number} reviewer ${reviewerIndex + 1} role`), remove);
      reviewers.append(row);
    });
    const add = button("Add parallel reviewer", "plus", () => {
      const draft = chainDrafts(container); draft[index].reviewers.push({ role: "" }); renderChain(container, draft, label);
    });
    add.disabled = stage.reviewers.length >= 10;
    reviewers.append(add);
    card.append(heading, fields, reviewers);
    container.append(card);
  });
  const addStage = button("Add next stage", "plus", () => {
    const draft = chainDrafts(container);
    draft.push({ ...blankStage(), name: `stage-${draft.length + 1}` });
    renderChain(container, draft, label);
  });
  addStage.disabled = stages.length >= 10;
  container.append(addStage);
}

function policyDrafts() {
  return {
    hasDefault: element("has-default").checked,
    defaultStages: chainDrafts(element("default-stages")),
    overrides: [...element("overrides").children].map(card => ({
      workflowCode: card.querySelector("[data-field=step]").value,
      stages: chainDrafts(card.querySelector(".chain"))
    }))
  };
}

function renderOverrides(overrides) {
  const container = element("overrides");
  container.replaceChildren();
  overrides.forEach((override, index) => {
    const card = document.createElement("div");
    card.className = "override-card";
    const head = document.createElement("div");
    head.className = "override-head";
    const label = document.createElement("label");
    label.append(document.createTextNode("Business step"));
    const select = document.createElement("select");
    select.required = true;
    select.dataset.field = "step";
    select.setAttribute("aria-label", `Override ${index + 1} business step`);
    fillStepSelect(select, overrides.filter((_, other) => other !== index).map(item => item.workflowCode));
    setStep(select, override.workflowCode);
    select.addEventListener("change", () => renderOverrides(policyDrafts().overrides));
    label.append(select);
    const remove = button("Remove override", "close", () => {
      const draft = policyDrafts().overrides; draft.splice(index, 1); renderOverrides(draft);
    });
    head.append(label, remove);
    const chain = document.createElement("div");
    chain.className = "chain";
    renderChain(chain, override.stages, `Override ${index + 1}`);
    card.append(head, chain);
    container.append(card);
  });
  syncOverrideButton();
}

function renderPolicyChains(drafts) {
  element("has-default").checked = drafts.hasDefault;
  element("default-chain").hidden = !drafts.hasDefault;
  renderChain(element("default-stages"), drafts.defaultStages.length ? drafts.defaultStages : [blankStage()], "Default");
  renderOverrides(drafts.overrides);
}

const refreshChains = () => renderPolicyChains(policyDrafts());

function setScopeEditable(editable) {
  const form = element("policy-editor");
  for (const key of ["id", "clientId", "accountId"]) form.elements[key].readOnly = !editable;
}

function setEditorMode(mode) {
  editorMode = mode;
  element("policy-fields").disabled = mode === "view";
  element("edit-policy").hidden = !selectedKey || mode !== "view";
  element("policy-action").hidden = mode === "view";
  element("policy-action-label").textContent = mode === "edit" ? "Save" : "Save & activate";
  element("cancel-edit").hidden = mode !== "edit";
}

function policyInput() {
  const form = element("policy-editor");
  const { client, account } = scopeOf(form);
  const drafts = policyDrafts();
  return {
    id: form.elements.id.value.trim(), clientId: client, accountId: account,
    baseVersion: Number(form.elements.baseVersion.value),
    defaultStages: drafts.hasDefault ? toApiStages(drafts.defaultStages) : null,
    overrides: drafts.overrides
      .map(item => ({ workflowCode: item.workflowCode, stages: toApiStages(item.stages) }))
      .sort((a, b) => (a.workflowCode < b.workflowCode ? -1 : a.workflowCode > b.workflowCode ? 1 : 0))
  };
}

function policyFingerprint(input) {
  return JSON.stringify({
    id: input.id, clientId: input.clientId, accountId: input.accountId,
    defaultStages: input.defaultStages, overrides: input.overrides
  });
}

function examplePolicy() {
  const lead = () => ({ name: "account-lead", timeoutMinutes: 15, reviewers: [{ role: "GroupAccountLead" }] });
  const review = () => ({ name: "review", timeoutMinutes: 15, reviewers: [{ role: "FinanceController" }, { role: "Legal" }] });
  return {
    hasDefault: true,
    defaultStages: [lead(), review()],
    overrides: [{
      workflowCode: "B07",
      stages: [lead(), review(), { name: "client", timeoutMinutes: 15, reviewers: [{ role: "ClientApprover" }] }]
    }]
  };
}

function newPolicy(example = false) {
  selectedKey = "";
  element("policies").value = "";
  const form = element("policy-editor");
  form.reset();
  form.elements.id.value = example
    ? (registry.policies.some(policy => policy.id === "sunny-approvals") ? `example-${Date.now()}` : "sunny-approvals") : "";
  setScopeEditable(true);
  renderPolicyChains(example ? examplePolicy() : { hasDefault: true, defaultStages: [blankStage()], overrides: [] });
  setEditorMode("new");
  element("policy-badge").className = "badge subtle";
  element("policy-badge").textContent = "Not saved";
  element("policy-message").textContent = example
    ? "Example ready. Adjust the chains, then Save & activate."
    : "New policy. Saving creates and activates version 1.";
  element("start-policy-hint").textContent = "Save this policy to use it for new starts. Previously saved policies stay active until then.";
}

function populatePolicy(policy) {
  const form = element("policy-editor");
  for (const key of ["id", "clientId"]) form.elements[key].value = policy[key];
  form.elements.accountId.value = policy.accountId || "";
  form.elements.baseVersion.value = Math.max(...registry.policies.filter(item => item.id === policy.id).map(item => item.version));
  setScopeEditable(false);
  renderPolicyChains({
    hasDefault: !!policy.defaultStages,
    defaultStages: (policy.defaultStages ?? []).map(toDraftStage),
    overrides: policy.overrides.map(item => ({ workflowCode: item.workflowCode, stages: item.stages.map(toDraftStage) }))
  });
  editBaseline = policyFingerprint(policyInput());
  setEditorMode("view");
  const active = isActivePolicy(policy);
  element("policy-badge").textContent = `${active ? "Active" : "Saved"} v${policy.version}`;
  element("policy-badge").className = `badge ${active ? "active" : "subtle"}`;
  element("policy-message").textContent = `${policy.id} v${policy.version} is read-only. Choose Edit to change its chains.`;
  const start = element("chain-start");
  start.elements.clientId.value = policy.clientId;
  start.elements.accountId.value = policy.accountId || "";
  if (!start.elements.workflowCode.value && policy.overrides[0]) setStep(start.elements.workflowCode, policy.overrides[0].workflowCode);
  const simulator = element("identity-editor");
  simulator.elements.clientId.value = policy.clientId;
  simulator.elements.accountId.value = policy.accountId || "";
  renderIdentityScope();
  void updateStartHint();
}

async function loadPolicies(preferred) {
  registry = await request("/api/policies");
  const select = element("policies");
  select.replaceChildren(new Option("Create a new policy", ""));
  for (const policy of [...registry.policies].sort((a, b) => a.id.localeCompare(b.id) || b.version - a.version)) {
    select.add(new Option(`${policy.id} v${policy.version} | ${policy.clientId}/${policy.accountId || "all-accounts"} | ${policySummary(policy)}${isActivePolicy(policy) ? " [ACTIVE]" : ""}`, policyKey(policy)));
  }
  const reference = registry.active.at(-1);
  const key = preferred || selectedKey || (reference ? `${reference.id}:${reference.version}` : "");
  const policy = policyByKey(key);
  if (policy) { selectedKey = key; select.value = key; populatePolicy(policy); }
  else newPolicy();
  renderCoverage();
}

element("policies").addEventListener("change", () => {
  const key = element("policies").value;
  if (!key) { newPolicy(); return; }
  act(async () => {
    const policy = policyByKey(key);
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
element("add-override").addEventListener("click", () => {
  if (editorMode === "view") return;
  renderOverrides([...policyDrafts().overrides, { workflowCode: "", stages: [blankStage()] }]);
});
element("has-default").addEventListener("change", event => { element("default-chain").hidden = !event.target.checked; });
for (const name of ["clientId", "accountId"])
  element("policy-editor").elements[name].addEventListener("change", refreshChains);
element("edit-policy").addEventListener("click", event => {
  if (editorMode !== "view" || busy) return;
  event.preventDefault();
  setEditorMode("edit");
  message("Editing enabled. Save only creates a new version when content changes. Policy ID, client, and account stay fixed.");
  element("policy-message").textContent = "Edit the default chain or any step override, then Save. Cancel discards your changes.";
});
element("cancel-edit").addEventListener("click", () => {
  const policy = policyByKey(selectedKey);
  if (policy) populatePolicy(policy);
  message("Changes discarded. Saved policy is read-only.");
});
element("policy-editor").addEventListener("submit", event => {
  event.preventDefault();
  if (editorMode === "view") return;
  act(async () => {
    const input = policyInput();
    if (editorMode === "edit" && policyFingerprint(input) === editBaseline) {
      populatePolicy(policyByKey(selectedKey));
      message("No changes to save. No new version was created.");
      return;
    }
    const saved = await request("/api/policies", input);
    await loadPolicies(`${saved.id}:${saved.version}`);
    message(saved.version === input.baseVersion ? "No changes to save. No new version was created."
      : `Saved and activated ${saved.id} v${saved.version}. Ready for new workflow starts.`);
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
    remove.setAttribute("aria-label", `Remove assignment ${index + 1}`);
    row.append(
      inputField("Role", assignment.role, "role", `Assignment ${index + 1} role`),
      inputField("Simulated user", assignment.actor, "actor", `Assignment ${index + 1} user`), remove);
    container.append(row);
  });
  renderCoverage();
}

function renderIdentityScope() {
  const { client, account } = scopeOf(element("identity-editor"));
  const scoped = identityRegistry.assignments.filter(item => item.clientId === client && item.accountId === account);
  renderIdentities(scoped.length ? scoped : [{ role: "", actor: "" }]);
  element("identity-message").textContent = scoped.length
    ? `${scoped.length} saved assignment(s) for ${client}/${account || "all-accounts"}.`
    : `Nothing saved for ${client}/${account || "all-accounts"} yet. Fill in the row, or load the example.`;
}

function renderCoverage() {
  const box = element("identity-coverage");
  const { client, account } = scopeOf(element("identity-editor"));
  const have = new Set(identityDrafts().map(item => item.role).filter(Boolean));
  const needed = new Set();
  for (const active of registry.active) {
    const policy = policyByKey(`${active.id}:${active.version}`);
    const policyAccount = policy?.accountId ?? null;
    if (policy && policy.clientId === client && (policyAccount === null || policyAccount === account))
      policyRoles(policy).forEach(role => needed.add(role));
  }
  box.replaceChildren();
  const missing = [...needed].filter(role => !have.has(role));
  if (!needed.size) {
    box.textContent = "No active policy uses this scope yet. Define roles here first, then choose them in step 2 (Policies).";
  } else if (!missing.length) {
    box.textContent = `Every role used by this scope's active policies has a user (${needed.size} role(s)).`;
  } else {
    box.append(document.createTextNode("Active policies still need a user for: "));
    for (const role of missing)
      box.append(button(`Add ${role}`, "plus", () => renderIdentities([...identityDrafts(), { role, actor: "" }]), "chip"));
  }
}

function updateRequesterChoices() {
  const select = element("requester");
  const previous = select.value || "requester";
  select.replaceChildren(new Option("requester (plan owner, not an approver)", "requester"));
  for (const actor of new Set(identityRegistry.assignments.map(item => item.actor)))
    if (actor !== "requester") select.add(new Option(`${actor} (demo user)`, actor));
  select.value = [...select.options].some(option => option.value === previous) ? previous : "requester";
}

function afterIdentityChange() {
  renderIdentityScope();
  updateActorChoices();
  updateRequesterChoices();
  if (element("default-stages").children.length) refreshChains();
}

function allSteps() {
  return workflowBlocks.flatMap(block => block.steps);
}

function updateActorChoices() {
  const select = element("actor");
  const previous = select.value;
  const steps = allSteps();
  const assignments = steps.length
    ? steps.map(step => ({ role: step.role, actor: step.approver }))
    : identityRegistry.assignments;
  const actors = new Map();
  for (const assignment of assignments) {
    if (!actors.has(assignment.actor)) actors.set(assignment.actor, new Set());
    actors.get(assignment.actor).add(assignment.role);
  }
  select.replaceChildren(new Option(actors.size ? "Select a demo user" : "No assignments - set up Identities first", ""));
  for (const [actor, roles] of actors)
    select.add(new Option(`${[...roles].join(", ")} - ${actor}`, actor));
  const preferred = steps.find(step => step.canDecide)?.approver;
  select.value = actors.has(previous) ? previous : actors.has(preferred) ? preferred : "";
  select.disabled = actors.size === 0;
}

async function loadIdentitySettings() {
  identityRegistry = await request("/api/demo-identities");
  afterIdentityChange();
}

element("identity-editor").elements.clientId.addEventListener("change", renderIdentityScope);
element("identity-editor").elements.accountId.addEventListener("change", renderIdentityScope);
element("identity-rows").addEventListener("input", renderCoverage);
element("identity-example").addEventListener("click", () => {
  renderIdentities([
    { role: "GroupAccountLead", actor: "lead" },
    { role: "FinanceController", actor: "finance" },
    { role: "Legal", actor: "legal" },
    { role: "ClientApprover", actor: "client" }
  ]);
  message("Example loaded. Nothing is saved until you choose Save identities.");
});
element("add-identity").addEventListener("click", () => {
  renderIdentities([...identityDrafts(), { role: "", actor: "" }]);
});
element("identity-editor").addEventListener("submit", event => {
  event.preventDefault();
  act(async () => {
    const assignments = Object.create(null);
    for (const item of identityDrafts()) {
      if (Object.hasOwn(assignments, item.role)) throw new Error(`Duplicate role: ${item.role}`);
      assignments[item.role] = item.actor;
    }
    const { client, account } = scopeOf(event.target);
    identityRegistry = await request("/api/demo-identities", { clientId: client, accountId: account, assignments });
    afterIdentityChange();
    showPanel("policies");
    message("Identities saved. Now configure a policy; its role dropdown offers these roles. No policy version was created; running workflows are unchanged.");
  });
});

function isFinished(status) {
  return FINISHED_RUN_STATES.includes(status.runtimeStatus);
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
  if (!approval.canDecide) {
    message(approval.blockedReason || "This step is not waiting for a decision.", true);
    return;
  }
  const comment = (draftComments.get(id) || "").trim();
  const decision = {
    actingAs: element("actor").value.trim(), outcome,
    version: Number(element("decision-version").value || approval.version),
    ...(comment && { comment })
  };
  if (!decision.actingAs || !Number.isInteger(decision.version) || decision.version < 1) {
    message("Choose an Acting as user and a positive plan version.", true);
    return;
  }
  if (COMMENT_REQUIRED.includes(outcome) && !comment) {
    message(`Add a comment to explain why you ${outcome === "Reject" ? "reject" : "return"} this plan.`, true);
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
    const invalidReason = invalidDecisionReason(approval, decision);
    for (let attempt = 0; attempt < 20; attempt++) {
      const status = await request(`/api/approvals/${encodeURIComponent(id)}?history=true`);
      showResult(status);
      if (isFinished(status)) {
        pendingDecisions.delete(id);
        draftComments.delete(id);
        await refresh();
        message(`${id}: ${status.result?.status || status.runtimeStatus}. The finished step stays visible in its workflow.`);
        return;
      }
      const received = status.history?.some(event => event.eventType === "EventRaised" && event.name === "Decision" &&
        JSON.stringify(JSON.parse(event.input || "{}")) === JSON.stringify({
          Approver: decision.actingAs, Outcome: decision.outcome, Version: decision.version,
          Comment: decision.comment ?? null
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
  const token = ++refreshCounter;
  const workflows = await request("/api/workflows");
  const shown = workflows.filter((item, index) => index === 0 || ACTIVE_RUN_STATES.includes(item.runtimeStatus))
    .slice(0, MAX_WORKFLOWS_SHOWN);
  if (startedWorkflow && !shown.some(item => item.instanceId === startedWorkflow))
    shown.unshift(workflows.find(item => item.instanceId === startedWorkflow)
      ?? { instanceId: startedWorkflow, runtimeStatus: "Pending" });
  const blocks = await Promise.all(shown.map(workflow =>
    request(`/api/workflows/${encodeURIComponent(workflow.instanceId)}/approvals`)
      .then(steps => ({ workflow, steps }), error => ({ workflow, steps: [], error: error.message }))));
  if (token !== refreshCounter) return;
  workflowBlocks = blocks;
  renderApprovals();
}

const span = (className, text) => {
  const node = document.createElement("span");
  node.className = className;
  node.textContent = text;
  return node;
};

function cellOf(label, ...parts) {
  const cell = document.createElement("td");
  cell.dataset.label = label;
  const body = document.createElement("div");
  body.className = "cell-body";
  body.append(...parts);
  cell.append(body);
  return cell;
}

function workflowHeader(block) {
  const { workflow, steps, error } = block;
  const row = document.createElement("tr");
  row.className = "workflow-header";
  const cell = row.insertCell();
  cell.colSpan = 6;
  const content = document.createElement("div");
  content.className = "workflow-header-content";
  const title = document.createElement("strong");
  title.textContent = workflow.instanceId;
  const detail = span("", [workflow.workflowCode, steps[0] && `${steps[0].planId} v${steps[0].version}`, workflow.chainSource,
    steps[0] && `policy v${steps[0].policyVersion}`, workflow.requester && `started by ${workflow.requester}`,
    workflow.correlationId && `correlation ${workflow.correlationId}`].filter(Boolean).join(" | "));
  content.append(title, span(`state state-${workflow.runtimeStatus}`, workflow.runtimeStatus), detail,
    button("Pinned policy & result", "info", () => act(async () => {
      showResult(await request(`/api/workflows/${encodeURIComponent(workflow.instanceId)}`), true);
      message("Workflow progress and its original policy snapshot are shown below.");
    })));
  cell.append(content);
  if (error) {
    const problem = document.createElement("p");
    problem.className = "hint blocked-reason";
    problem.textContent = `Steps could not be loaded: ${error}`;
    cell.append(problem);
  }
  return row;
}

function decisionNote(approval) {
  const when = approval.decidedAt ? new Date(approval.decidedAt).toLocaleString() : "";
  const text = {
    Approved: `Approved by ${approval.decidedBy} ${when}`,
    Rejected: `Rejected by ${approval.decidedBy} ${when}`,
    Returned: `Returned for changes by ${approval.decidedBy} ${when}`,
    Timeout: `No decision before the deadline ${when}`,
    Canceled: `Canceled ${when}: another reviewer ended the plan`,
    NotStarted: "Never started: an earlier stage ended the plan"
  }[approval.state];
  if (!text) return null;
  const note = document.createElement("p");
  note.className = "decision-note";
  note.append(document.createTextNode(text.trim()));
  if (approval.comment) {
    const quote = document.createElement("q");
    quote.textContent = approval.comment;
    note.append(document.createTextNode(" - "), quote);
  }
  return note;
}

function approvalRow(approval) {
  const row = document.createElement("tr");
  const state = approval.state;
  row.dataset.state = state;
  row.dataset.approvalId = approval.instanceId;
  row.className = approval.canDecide ? "" : "approval-inactive";
  const finishedStep = FINISHED_STEP_STATES.includes(state);
  if (finishedStep) row.className = "approval-finished";
  row.append(
    cellOf("Rank", approval.rank),
    cellOf("Approval step", span("cell-main mono", approval.instanceId)),
    cellOf("Business plan", span("cell-main", approval.planId), span("cell-sub", `v${approval.version}`)),
    cellOf("Stage / state", span("cell-main", approval.progress.stage), span(`state state-${state}`, state),
      span("cell-sub", `Policy v${approval.policyVersion}`)),
    cellOf("Role / user", span("cell-main", approval.role), span("cell-sub", approval.approver)));
  const actions = document.createElement("div");
  actions.className = "row-actions";
  const pending = pendingDecisions.get(approval.instanceId);
  const parts = [];
  if (!finishedStep) {
    const comment = document.createElement("input");
    comment.type = "text";
    comment.className = "decision-comment";
    comment.maxLength = 1000;
    comment.placeholder = "Comment (required to reject or return)";
    comment.setAttribute("aria-label", `Comment for ${approval.instanceId}`);
    comment.value = draftComments.get(approval.instanceId) || "";
    comment.disabled = !approval.canDecide || !!pending;
    comment.addEventListener("input", () => draftComments.set(approval.instanceId, comment.value));
    parts.push(comment);
  }
  for (const { outcome, label, symbol, className } of DECISION_ACTIONS) {
    if (finishedStep) break;
    const action = button(label, symbol, () => void submitDecision(approval, outcome), className);
    action.disabled = !!pending || !approval.canDecide;
    if (action.disabled && approval.blockedReason) action.title = approval.blockedReason;
    actions.append(action);
  }
  const view = button("View", "info", () => void act(async () => {
    await showStatus(approval.instanceId);
    message("Approval status and history shown below.");
  }));
  view.disabled = !approval.canView;
  if (view.disabled && approval.blockedReason) view.title = approval.blockedReason;
  if (approval.state !== "NotStarted") actions.append(view);
  parts.push(actions);
  const record = decisionNote(approval);
  if (record) parts.push(record);
  if (pending) {
    const hint = document.createElement("p");
    hint.className = "hint decision-pending";
    hint.textContent = pending;
    hint.setAttribute("role", "status");
    parts.push(hint);
  }
  if (approval.blockedReason) {
    const reason = document.createElement("p");
    reason.className = "hint blocked-reason";
    reason.textContent = approval.blockedReason;
    parts.push(reason);
  }
  row.append(cellOf("Actions", ...parts));
  return row;
}

function refreshSummary() {
  return {
    refreshedAt: new Date().toISOString(),
    stepsWaitingForDecision: allSteps().filter(step => step.canDecide).length,
    workflows: workflowBlocks.map(({ workflow, steps, error }) => ({
      instanceId: workflow.instanceId, status: workflow.runtimeStatus, businessStep: workflow.workflowCode,
      chain: workflow.chainSource, policyVersion: workflow.policyVersion,
      steps: steps.map(step => `${step.rank} ${step.role} (${step.approver}): ${step.state}${step.comment ? ` - "${step.comment}"` : ""}`),
      ...(error && { error })
    }))
  };
}

function renderApprovals() {
  element("waiting-count").textContent = allSteps().filter(step => step.canDecide).length;
  const tbody = element("approvals");
  updateActorChoices();
  tbody.replaceChildren();
  for (const block of workflowBlocks) {
    tbody.append(workflowHeader(block));
    for (const step of block.steps) tbody.append(approvalRow(step));
  }
  if (!workflowBlocks.length) {
    const cell = tbody.insertRow().insertCell();
    cell.colSpan = 6; cell.className = "empty-state";
    cell.textContent = "No workflows yet. Start one in step 3, then refresh. Its worker must be running.";
  }
}

element("chain-start").addEventListener("submit", event => {
  event.preventDefault();
  act(async () => {
    const input = Object.fromEntries(new FormData(event.target));
    input.accountId = input.accountId || null; input.version = Number(input.version);
    input.correlationId = input.correlationId?.trim() || null;
    const result = await request("/api/workflows", input);
    startedWorkflow = result.instanceId;
    showResult(result);
    message(`Started ${result.instanceId} using ${result.source} from ${result.policyId} v${result.policyVersion}. Correlation ID ${result.correlationId}. Refresh to see its active stage.`);
    await refresh();
    showPanel("approvals");
  });
});
element("refresh").addEventListener("click", () => act(async () => {
  await refresh();
  showResult(refreshSummary());
  message("Approvals refreshed.");
}));

let startHintToken = 0;
async function updateStartHint() {
  const form = element("chain-start");
  const token = ++startHintToken;
  const hint = element("start-policy-hint");
  const step = form.elements.workflowCode.value;
  if (!step) { hint.textContent = "Choose a business step to see which approval chain will apply."; return; }
  const { client, account } = scopeOf(form);
  const query = new URLSearchParams({ clientId: client, workflowCode: step });
  if (account) query.set("accountId", account);
  try {
    const match = await request(`/api/policy-match?${query}`);
    if (token !== startHintToken) return;
    const approvals = match.stages.reduce((count, stage) => count + stage.roles.length, 0);
    hint.textContent = `${match.workflowCode} will use the ${match.source} chain from ${match.policyId} v${match.policyVersion}: ${match.stages.length} stage(s), ${approvals} approval(s). It is pinned when you start.`;
  } catch (error) {
    if (token === startHintToken) hint.textContent = `No chain will apply. ${error.message}`;
  }
}
for (const name of ["clientId", "accountId", "workflowCode"])
  element("chain-start").elements[name].addEventListener("change", () => void updateStartHint());
act(async () => {
  const environment = await request("/api/environment");
  element("environment").textContent = environment.environment;
  element("environment").classList.toggle("azure", environment.environment === "AZURE");
  element("endpoint").textContent = `${environment.endpoint} | task hub: ${environment.taskHub}`;
  element("policy-location").textContent = `approval_policies/${environment.environment === "AZURE" ? "azure" : "local"}.json`;
  element("dashboard").href = environment.dashboardUrl;
  element("waiting-count").title = "Steps waiting for a decision in the workflows shown";
  businessSteps = await request("/api/business-steps");
  fillStepSelect(element("chain-start").elements.workflowCode);
  await loadIdentitySettings(); await loadPolicies(); await refresh();
  message("Ready. Work left to right: Identities, Policies, Start workflow, Approvals.");
});
