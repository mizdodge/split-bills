document.addEventListener("DOMContentLoaded", () => {
  const t = window.splitBillStrings || {};
  const format = (template, value) => String(template || "").replace("{0}", value ?? "");
  const passwordToggle = document.querySelector(".password-toggle");
  passwordToggle?.addEventListener("click", () => {
    const input = passwordToggle.parentElement.querySelector("input");
    input.type = input.type === "password" ? "text" : "password";
  });

  const receiptInput = document.getElementById("receipt-input");
  const dropZone = document.getElementById("drop-zone");
  const previewGallery = document.getElementById("upload-preview-gallery");
  const uploadPrompt = document.getElementById("upload-prompt");
  const selectionBar = document.getElementById("upload-selection-bar");
  const selectionCount = document.getElementById("upload-selection-count");
  let selectedReceiptFiles = [];
  let previewUrls = [];
  const applyReceiptFiles = files => {
    selectedReceiptFiles = files;
    const transfer = new DataTransfer(); selectedReceiptFiles.forEach(file => transfer.items.add(file));
    receiptInput.files = transfer.files;
    previewUrls.forEach(URL.revokeObjectURL); previewUrls = [];
    previewGallery?.replaceChildren(...selectedReceiptFiles.map((file, index) => {
      const card = document.createElement("article");
      const image = document.createElement("img"); const url = URL.createObjectURL(file); previewUrls.push(url);
      image.src = url; image.alt = format(t.photoNumber, index + 1);
      const label = document.createElement("span"); label.textContent = format(t.photoNumber, index + 1);
      const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "×";
      remove.title = `${t.removePhoto || "Remove"} ${format(t.photoNumber, index + 1)}`; remove.dataset.removeImage = index;
      card.append(image, label, remove); return card;
    }));
    const hasFiles = selectedReceiptFiles.length > 0;
    if (uploadPrompt) uploadPrompt.hidden = hasFiles;
    if (previewGallery) previewGallery.hidden = !hasFiles;
    if (selectionBar) selectionBar.hidden = !hasFiles;
    if (selectionCount) selectionCount.textContent = format(t.selectedPhotos, selectedReceiptFiles.length);
  };
  receiptInput?.addEventListener("change", () => {
    const additions = [...receiptInput.files];
    const merged = [...selectedReceiptFiles];
    additions.forEach(file => {
      if (!merged.some(current => current.name === file.name && current.size === file.size && current.lastModified === file.lastModified)) merged.push(file);
    });
    applyReceiptFiles(merged);
  });
  ["dragenter", "dragover"].forEach(name => dropZone?.addEventListener(name, event => { event.preventDefault(); dropZone.classList.add("dragging"); }));
  ["dragleave", "drop"].forEach(name => dropZone?.addEventListener(name, event => { event.preventDefault(); dropZone.classList.remove("dragging"); }));
  dropZone?.addEventListener("drop", event => {
    if (!event.dataTransfer.files.length) return;
    const merged = [...selectedReceiptFiles, ...event.dataTransfer.files].filter((file, index, files) =>
      files.findIndex(current => current.name === file.name && current.size === file.size && current.lastModified === file.lastModified) === index);
    applyReceiptFiles(merged);
  });
  previewGallery?.addEventListener("click", event => {
    const button = event.target.closest("[data-remove-image]"); if (!button) return;
    applyReceiptFiles(selectedReceiptFiles.filter((_, index) => index !== Number(button.dataset.removeImage)));
  });
  document.getElementById("add-more-images")?.addEventListener("click", () => receiptInput.click());
  document.getElementById("upload-form")?.addEventListener("submit", event => {
    if (!receiptInput.files.length) return;
    document.getElementById("processing-overlay").hidden = false;
    event.submitter.disabled = true;
  });

  const editor = document.getElementById("item-editor");
  const reindexItems = () => {
    editor?.querySelectorAll(".item-row").forEach((row, index) => {
      row.querySelector(".item-number").textContent = index + 1;
      const fields = { ".item-name": "Name", 'input[name$=".Quantity"]': "Quantity", ".item-unit": "UnitPrice", ".item-total": "TotalPrice" };
      Object.entries(fields).forEach(([selector, field]) => {
        const input = row.querySelector(selector) || row.querySelector(`[data-field="${field}"]`);
        if (input) { input.name = `Items[${index}].${field}`; input.id = `Items_${index}__${field}`; }
      });
    });
  };
  const updateSubtotal = () => {
    const subtotal = document.getElementById("Subtotal");
    if (subtotal) subtotal.value = [...editor.querySelectorAll(".item-total")].reduce((sum, el) => sum + (Number(el.value) || 0), 0);
    updateChargeRates();
  };
  editor?.addEventListener("click", event => { if (event.target.closest(".remove-item")) { event.target.closest(".item-row").remove(); reindexItems(); updateSubtotal(); } });
  editor?.addEventListener("input", event => {
    const row = event.target.closest(".item-row");
    if (!row) return;
    if (event.target.matches('.item-unit,input[name$=".Quantity"],[data-field="Quantity"]')) {
      const qty = row.querySelector('input[name$=".Quantity"], [data-field="Quantity"]');
      const unit = row.querySelector(".item-unit"); const total = row.querySelector(".item-total");
      total.value = (Number(qty.value) || 0) * (Number(unit.value) || 0);
    }
    updateSubtotal();
  });
  document.getElementById("add-item")?.addEventListener("click", () => {
    const fragment = document.getElementById("item-template").content.cloneNode(true); editor.appendChild(fragment); reindexItems();
  });
  const chargeEditor = document.getElementById("charge-editor");
  const reindexCharges = () => {
    chargeEditor?.querySelectorAll(".charge-row").forEach((row, index) => {
      const fields = { ".charge-operation": "Operation", ".charge-label": "Label", ".charge-amount": "Amount" };
      Object.entries(fields).forEach(([selector, field]) => {
        const input = row.querySelector(selector) || row.querySelector(`[data-field="${field}"]`);
        if (input) { input.name = `Charges[${index}].${field}`; input.id = `Charges_${index}__${field}`; }
      });
    });
  };
  function updateChargeRates() {
    const subtotal = Number(document.getElementById("Subtotal")?.value) || 0;
    chargeEditor?.querySelectorAll(".charge-row").forEach(row => {
      const amount = Number(row.querySelector(".charge-amount")?.value) || 0;
      const helper = row.querySelector(".charge-rate");
      if (!helper) return;
      helper.textContent = subtotal > 0 && amount > 0
        ? format(t.effectiveRate || "≈ {0}% of subtotal", new Intl.NumberFormat(document.documentElement.lang === "en" ? "en-US" : "id-ID", { maximumFractionDigits: 2 }).format(amount / subtotal * 100))
        : (t.rateFromAmount || "Percentage will be shown from the amount");
    });
  }
  chargeEditor?.addEventListener("input", updateChargeRates);
  chargeEditor?.addEventListener("click", event => {
    if (!event.target.closest(".remove-charge")) return;
    event.target.closest(".charge-row").remove(); reindexCharges(); updateChargeRates();
  });
  document.getElementById("Subtotal")?.addEventListener("input", updateChargeRates);
  document.getElementById("add-charge")?.addEventListener("click", () => {
    const template = document.getElementById("charge-template");
    if (!template || !chargeEditor) return;
    chargeEditor.appendChild(template.content.cloneNode(true)); reindexCharges(); updateChargeRates();
    chargeEditor.lastElementChild?.querySelector(".charge-label")?.focus();
  });
  updateChargeRates();
  document.getElementById("review-form")?.addEventListener("submit", () => { reindexItems(); reindexCharges(); });

  const participantInput = document.getElementById("ParticipantNames");
  const participantJsonInput = document.getElementById("ParticipantsJson");
  const userPicker = document.getElementById("registered-user-picker");
  const guestInput = document.getElementById("guest-name-input");
  const addGuestButton = document.getElementById("add-guest");
  const assignmentArea = document.getElementById("assignment-area");
  const chips = document.getElementById("participant-chips");
  const splitForm = document.getElementById("split-form");
  const locale = window.splitBillAssignmentLocale || {};
  const assignmentItems = [...(assignmentArea?.querySelectorAll(".assignment-item") || [])];
  let participants = [];
  try { participants = JSON.parse(participantJsonInput?.value || "[]"); } catch { participants = []; }
  participants = Array.isArray(participants) ? participants.filter(x => x && x.clientKey && x.name) : [];
  let savedAssignments = {};
  try { savedAssignments = JSON.parse(document.getElementById("AssignmentsJson")?.value || "{}"); } catch { savedAssignments = {}; }
  const groupsByItem = {};
  assignmentItems.forEach(item => {
    const raw = savedAssignments[item.dataset.itemId];
    const itemQuantity = Number(item.dataset.itemQuantity) || 1;
    if (Array.isArray(raw)) groupsByItem[item.dataset.itemId] = [{ quantity: itemQuantity, participants: raw.map(x => typeof x === "number" ? `legacy-index:${x}` : x).filter(Boolean) }];
    else if (raw?.groups && Array.isArray(raw.groups)) groupsByItem[item.dataset.itemId] = raw.groups.map(group => ({ quantity: Number(group.quantity) || 0, participants: Array.isArray(group.participants) ? group.participants.filter(Boolean) : [] }));
    else groupsByItem[item.dataset.itemId] = [{ quantity: itemQuantity, participants: [] }];
  });
  const syncParticipants = () => {
    if (participantInput) participantInput.value = participants.map(x => x.name).join("\n");
    if (participantJsonInput) participantJsonInput.value = JSON.stringify(participants);
    userPicker?.querySelectorAll("[data-user-id]").forEach(button => {
      const selected = participants.some(x => x.userId === button.dataset.userId);
      button.classList.toggle("selected", selected); button.setAttribute("aria-pressed", selected ? "true" : "false");
    });
  };
  const removeParticipant = clientKey => {
    participants = participants.filter(x => x.clientKey !== clientKey);
    Object.values(groupsByItem).forEach(groups => groups.forEach(group => { group.participants = group.participants.filter(key => key !== clientKey); }));
    syncParticipants(); renderAssignments();
  };
  const renderParticipants = () => {
    if (!chips) return;
    chips.replaceChildren(...participants.map(participant => {
      const el = document.createElement("span"); el.className = "participant-chip";
      el.append(document.createTextNode(participant.name + (participant.userId ? ` · ${t.accountSuffix || "account"}` : ` · ${t.guestSuffix || "guest"}`)));
      const remove = document.createElement("button"); remove.type = "button"; remove.className = "remove-participant"; remove.dataset.clientKey = participant.clientKey; remove.setAttribute("aria-label", `${t.remove || "Remove"} ${participant.name}`); remove.textContent = "×";
      el.appendChild(remove); return el;
    }));
  };
  const pickupPreview = document.getElementById("pickup-preview");
  const pickupPreviewCandidates = pickupPreview?.querySelector(".pickup-preview-candidates");
  const pickupPreviewState = pickupPreview?.querySelector(".pickup-preview-state");
  const pickupLocale = window.splitBillPickupLocale || {};
  let pickupPreviewSequence = 0;
  const refreshPickupPreview = async () => {
    if (!pickupPreview || !splitForm?.dataset.pickupPreviewUrl) return;
    const ids = participants.map(x => x.userId).filter(Boolean);
    if (!ids.length) { pickupPreview.hidden = true; return; }
    const sequence = ++pickupPreviewSequence;
    const token = splitForm.querySelector('input[name="__RequestVerificationToken"]')?.value || "";
    const body = new URLSearchParams({ participantUserIds: JSON.stringify(ids), transactionId: splitForm.dataset.pickupTransactionId || "" });
    body.append("__RequestVerificationToken", token);
    try {
      const response = await fetch(splitForm.dataset.pickupPreviewUrl, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" }, body });
      if (!response.ok) throw new Error("preview failed");
      const data = await response.json();
      if (sequence !== pickupPreviewSequence) return;
      if (!Array.isArray(data.candidates) || data.candidates.length === 0) { pickupPreview.hidden = true; return; }
      pickupPreview.hidden = false;
      const roundRobin = data.strategy === "RoundRobin";
      if (pickupPreviewState) pickupPreviewState.textContent = roundRobin ? (pickupLocale.roundRobinPreview || "Round robin") : text(pickupLocale.candidateCount || "{0} candidates", data.candidates.length);
      const percent = new Intl.NumberFormat(document.documentElement.lang === "en" ? "en-US" : "id-ID", { style: "percent", maximumFractionDigits: 2 });
      pickupPreviewCandidates?.replaceChildren(...data.candidates.map(candidate => {
        const chip = document.createElement("span"); chip.className = "pickup-preview-candidate";
        const name = document.createElement("strong"); name.textContent = candidate.name;
        const odds = document.createElement("b"); odds.textContent = roundRobin ? (Number(candidate.probability) === 1 ? (pickupLocale.nextInRotation || "Next") : "—") : percent.format(Number(candidate.probability) || 0);
        const prior = document.createElement("small"); prior.textContent = text(pickupLocale.priorPickups || "{0} prior pickups", candidate.priorPickups || 0);
        chip.append(name, odds, prior); return chip;
      }));
    } catch { if (sequence === pickupPreviewSequence) pickupPreview.hidden = true; }
  };
  const money = value => `Rp ${Math.round(value).toLocaleString(document.documentElement.lang === "en" ? "en-US" : "id-ID")}`;
  const text = (template, ...values) => String(template || "").replace(/\{(\d+)\}/g, (_, index) => values[Number(index)] ?? "");
  const renderGroup = (item, group, groupIndex) => {
    const card = document.createElement("div"); card.className = "allocation-group";
    const header = document.createElement("div"); header.className = "allocation-group-head";
    const title = document.createElement("strong"); title.textContent = `${locale.group || "Group"} ${groupIndex + 1}`;
    const remove = document.createElement("button"); remove.type = "button"; remove.className = "remove-group"; remove.textContent = "×"; remove.title = locale.remove || "Remove group"; remove.setAttribute("aria-label", `${locale.remove || "Remove group"} ${groupIndex + 1}`);
    remove.addEventListener("click", () => { groupsByItem[item.dataset.itemId].splice(groupIndex, 1); renderAssignments(); });
    header.append(title, remove); card.appendChild(header);
    const controls = document.createElement("div"); controls.className = "allocation-group-controls";
    const qtyLabel = document.createElement("label"); qtyLabel.className = "group-quantity"; qtyLabel.textContent = "Quantity";
    const qty = document.createElement("input"); qty.type = "number"; qty.min = "1"; qty.step = "1"; qty.inputMode = "numeric"; qty.max = item.dataset.itemQuantity; qty.value = group.quantity > 0 ? group.quantity : 1; qty.setAttribute("aria-label", `Quantity ${groupIndex + 1}`);
    qty.addEventListener("input", () => { group.quantity = Math.max(0, Math.floor(Number(qty.value) || 0)); updateAssignmentItem(item); });
    qtyLabel.appendChild(qty); controls.appendChild(qtyLabel); card.appendChild(controls);
    const participantHolder = document.createElement("div"); participantHolder.className = "group-participants";
    participants.forEach(participant => {
      const label = document.createElement("label"); label.className = "person-check group-person";
      const input = document.createElement("input"); input.type = "checkbox"; input.value = participant.clientKey; input.checked = group.participants.includes(participant.clientKey);
      input.addEventListener("change", () => { if (input.checked) group.participants.push(participant.clientKey); else group.participants = group.participants.filter(key => key !== participant.clientKey); updateAssignmentItem(item); });
      label.append(input, document.createTextNode(participant.name)); participantHolder.appendChild(label);
    });
    card.appendChild(participantHolder);
    const selected = group.participants.map(key => participants.find(p => p.clientKey === key)).filter(Boolean);
    const state = document.createElement("small"); state.className = selected.length > 1 ? "group-state shared" : "group-state";
    state.textContent = selected.length === 0 ? (locale.choose || "Choose a participant") : selected.length === 1 ? (locale.owned || "Owned quantity") : text(locale.shared || "Shared equally · {0} people", selected.length);
    card.appendChild(state);
    const preview = document.createElement("div"); preview.className = "group-preview";
    if (selected.length > 0) {
      const itemQuantity = Number(item.dataset.itemQuantity) || 1; const itemTotal = Number(item.dataset.itemTotal) || 0;
      const groupAmount = Math.round(itemTotal * (Number(group.quantity) || 0) / itemQuantity); const each = Math.floor(groupAmount / selected.length);
      selected.forEach((person, index) => { const row = document.createElement("div"); row.innerHTML = `<span>${person.name}</span><strong>${money(index === selected.length - 1 ? groupAmount - each * (selected.length - 1) : each)}</strong>`; preview.appendChild(row); });
    }
    card.appendChild(preview);
    return card;
  };
  const updateAssignmentItem = item => {
    const groups = groupsByItem[item.dataset.itemId] || []; const itemQuantity = Number(item.dataset.itemQuantity) || 1;
    const assigned = groups.filter(group => group.participants.length > 0).reduce((sum, group) => sum + (Number(group.quantity) || 0), 0);
    const label = item.querySelector("[data-assigned-label]"); if (label) label.textContent = text(locale.assigned || "Assigned {0} of {1}", assigned, itemQuantity);
    const remaining = item.querySelector("[data-remaining-label]"); if (remaining) remaining.textContent = text(locale.remaining || "Remaining {0}", Math.max(0, itemQuantity - assigned));
    const bar = item.querySelector("[data-progress-bar]"); if (bar) bar.style.width = `${Math.min(100, assigned / itemQuantity * 100)}%`;
    item.classList.toggle("assignment-invalid", groups.length === 0 || groups.some(group => group.quantity <= 0 || group.participants.length === 0) || groups.reduce((sum, group) => sum + (Number(group.quantity) || 0), 0) !== itemQuantity);
    const holder = item.querySelector(".assignment-groups"); if (holder) holder.replaceChildren(...groups.map((group, index) => renderGroup(item, group, index)));
    const add = item.querySelector("[data-add-group]"); if (add) { add.disabled = assigned >= itemQuantity; add.title = add.disabled ? (locale.incomplete || "Reduce another group first") : ""; }
  };
  const renderAssignments = () => {
    if (!splitForm) return;
    renderParticipants(); syncParticipants();
    const intro = assignmentArea?.querySelector(".assignment-intro"); if (intro) intro.hidden = participants.length > 0;
    assignmentItems.forEach(updateAssignmentItem);
    const summary = document.getElementById("participant-assignment-summary");
    if (summary) {
      const counts = new Map();
      Object.values(groupsByItem).forEach(groups => groups.forEach(group => group.participants.forEach(key => { const person = participants.find(p => p.clientKey === key); if (person) counts.set(person.name, (counts.get(person.name) || 0) + Number(group.quantity || 0) / group.participants.length); })));
      summary.hidden = counts.size === 0; summary.textContent = [...counts].map(([name, count]) => `${name} · ${Number.isInteger(count) ? count : count.toFixed(1)} qty`).join(" · ");
    }
    refreshPickupPreview();
  };
  chips?.addEventListener("click", event => { const button = event.target.closest(".remove-participant"); if (button) removeParticipant(button.dataset.clientKey); });
  userPicker?.addEventListener("click", event => {
    const button = event.target.closest("[data-user-id]"); if (!button) return;
    const userId = button.dataset.userId; const name = button.dataset.name;
    const existing = participants.find(x => x.userId === userId);
    if (existing) removeParticipant(existing.clientKey);
    else { participants.push({ clientKey: `user:${userId}`, userId, name }); syncParticipants(); renderAssignments(); }
  });
  const addGuest = () => {
    const name = guestInput?.value.trim(); if (!name) return;
    if (participants.some(x => x.name.localeCompare(name, undefined, { sensitivity: "accent" }) === 0)) { window.alert(t.duplicateParticipant || "Participant names must be unique."); return; }
    participants.push({ clientKey: `guest:${Date.now()}-${Math.random().toString(16).slice(2)}`, userId: null, name });
    guestInput.value = ""; syncParticipants(); renderAssignments();
  };
  addGuestButton?.addEventListener("click", addGuest);
  guestInput?.addEventListener("keydown", event => { if (event.key === "Enter") { event.preventDefault(); addGuest(); } });
  assignmentArea?.addEventListener("click", event => {
    const button = event.target.closest("[data-add-group]"); if (!button || button.disabled) return;
    const item = button.closest(".assignment-item"); const groups = groupsByItem[item.dataset.itemId] || []; const used = groups.reduce((sum, group) => sum + (Number(group.quantity) || 0), 0); const remaining = Math.max(0, Number(item.dataset.itemQuantity) - used);
    if (remaining > 0) { groups.push({ quantity: remaining, participants: [] }); renderAssignments(); }
  });
  renderAssignments();
  splitForm?.addEventListener("submit", event => {
    syncParticipants();
    if (!participants.length) { event.preventDefault(); window.alert(t.chooseParticipant || "Choose at least one participant."); return; }
    const mode = splitForm.querySelector('input[name="SplitMethod"]:checked')?.value;
    if (mode !== "ByItem") { document.getElementById("AssignmentsJson").value = "{}"; return; }
    const result = {}; let valid = true;
    assignmentItems.forEach(item => {
      const groups = groupsByItem[item.dataset.itemId] || []; const itemQuantity = Number(item.dataset.itemQuantity) || 1;
      if (!groups.length || groups.some(group => !Number.isInteger(Number(group.quantity)) || Number(group.quantity) <= 0 || !group.participants.length) || groups.reduce((sum, group) => sum + Number(group.quantity), 0) !== itemQuantity) valid = false;
      result[item.dataset.itemId] = { groups: groups.map(group => ({ quantity: Number(group.quantity), participants: [...new Set(group.participants)] })) };
    });
    if (!valid) { event.preventDefault(); window.alert(t.chooseItemParticipant || locale.incomplete || "Complete every item assignment before saving."); return; }
    document.getElementById("AssignmentsJson").value = JSON.stringify(result);
  });
  const toggleAssignmentArea = () => { if (assignmentArea) assignmentArea.style.display = splitForm?.querySelector('input[name="SplitMethod"]:checked')?.value === "ByItem" ? "block" : "none"; };
  splitForm?.querySelectorAll('input[name="SplitMethod"]').forEach(input => input.addEventListener("change", toggleAssignmentArea)); toggleAssignmentArea();

  const settingsForm = document.getElementById("ai-settings-form");
  const modelSelect = document.getElementById("Model");
  const modelButton = document.getElementById("load-ai-models");
  const modelStatus = document.getElementById("model-load-status");
  let activeProvider = settingsForm?.querySelector('input[name="Provider"]:checked')?.value;
  const setModelStatus = (message, state = "") => {
    if (!modelStatus) return;
    modelStatus.textContent = message;
    modelStatus.dataset.state = state;
  };
  const resetModelSelect = selectedModel => {
    if (!modelSelect) return;
    modelSelect.replaceChildren();
    const placeholder = document.createElement("option");
    placeholder.value = ""; placeholder.textContent = t.chooseModel || "Choose a model"; placeholder.disabled = true;
    placeholder.selected = !selectedModel; modelSelect.appendChild(placeholder);
    if (selectedModel) {
      const current = document.createElement("option");
      current.value = selectedModel; current.textContent = selectedModel; current.selected = true;
      modelSelect.appendChild(current);
    }
  };
  const loadModels = async () => {
    if (!settingsForm || !modelSelect || !modelButton) return;
    const provider = settingsForm.querySelector('input[name="Provider"]:checked')?.value;
    const endpoint = document.getElementById("Endpoint")?.value.trim();
    const apiVersion = document.getElementById("ApiVersion")?.value.trim();
    if (provider === "AzureOpenAi" && (!endpoint || !apiVersion)) {
      setModelStatus(t.azureFieldsRequired || "Enter the Azure endpoint and API version first.", "error");
      return;
    }

    modelButton.disabled = true;
    modelSelect.disabled = true;
    setModelStatus(t.loadingModels || "Loading models...", "loading");
    const formData = new FormData();
    formData.append("Provider", provider || "OpenAi");
    formData.append("Endpoint", endpoint || "");
    formData.append("ApiVersion", apiVersion || "");
    formData.append("ApiKey", document.getElementById("ApiKey")?.value || "");
    formData.append("__RequestVerificationToken", settingsForm.querySelector('input[name="__RequestVerificationToken"]')?.value || "");

    try {
      const response = await fetch(settingsForm.dataset.modelsUrl, { method: "POST", body: formData });
      const payload = await response.json();
      if (!response.ok) throw new Error(payload.message || t.modelsLoadFailed || "Unable to load models.");

      const selectedModel = modelSelect.dataset.currentModel || modelSelect.value;
      const models = Array.isArray(payload.models) ? payload.models : [];
      resetModelSelect("");
      models.forEach(model => {
        const option = document.createElement("option");
        option.value = model; option.textContent = model;
        option.selected = model === selectedModel;
        modelSelect.appendChild(option);
      });
      if (selectedModel && !models.includes(selectedModel)) {
        const current = document.createElement("option");
        current.value = selectedModel; current.textContent = format(t.storedModel, selectedModel); current.selected = true;
        modelSelect.appendChild(current);
      }
      if (!modelSelect.value && models.length) modelSelect.value = models[0];
      modelSelect.dataset.currentModel = modelSelect.value;
      setModelStatus(format(t.modelsAvailable, models.length), "success");
    } catch (error) {
      setModelStatus(error.message || t.modelsLoadFailed || "Unable to load models.", "error");
    } finally {
      modelSelect.disabled = false;
      modelButton.disabled = false;
    }
  };
  const toggleProvider = () => {
    const value = settingsForm?.querySelector('input[name="Provider"]:checked')?.value;
    document.querySelectorAll(".azure-fields").forEach(x => x.style.display = value === "AzureOpenAi" ? "block" : "none");
    document.querySelectorAll(".openai-fields").forEach(x => x.style.display = value === "OpenAi" ? "block" : "none");
    if (activeProvider && activeProvider !== value) {
      modelSelect.dataset.currentModel = "";
      resetModelSelect("");
      setModelStatus(value === "AzureOpenAi" ? (t.azureConnectionHint || "Enter the Azure connection, then load models.") : (t.openAiModelHint || "Models will be loaded from OpenAI."));
    }
    activeProvider = value;
  };
  settingsForm?.querySelectorAll('input[name="Provider"]').forEach(input => input.addEventListener("change", () => { toggleProvider(); if (input.checked && input.value === "OpenAi") loadModels(); }));
  modelButton?.addEventListener("click", loadModels);
  toggleProvider();
  if (settingsForm && activeProvider === "OpenAi") loadModels();

  const receiptModal = document.querySelector("[data-receipt-modal]");
  const viewerButtons = [...document.querySelectorAll("[data-receipt-viewer], [data-proof-viewer]")];
  if (receiptModal && viewerButtons.length) {
    const receiptImage = receiptModal.querySelector("[data-receipt-image]");
    const receiptCounter = receiptModal.querySelector("[data-receipt-counter]");
    const receiptTitle = receiptModal.querySelector("#receipt-modal-title");
    const zoomValue = receiptModal.querySelector("[data-receipt-zoom-value]");
    const previous = receiptModal.querySelector("[data-receipt-prev]");
    const next = receiptModal.querySelector("[data-receipt-next]");
    let activeIndex = 0; let zoom = 1; let returnFocus = null; let activeButtons = [];
    const applyZoom = () => {
      if (!receiptImage) return;
      receiptImage.style.transform = `scale(${zoom})`;
      if (zoomValue) zoomValue.textContent = `${Math.round(zoom * 100)}%`;
    };
    const showReceipt = index => {
      if (!activeButtons.length) return;
      activeIndex = (index + activeButtons.length) % activeButtons.length;
      const button = activeButtons[activeIndex];
      const url = button.dataset.viewerUrl || button.dataset.receiptUrl || "";
      const label = button.dataset.viewerLabel || button.getAttribute("aria-label") || "Receipt photo";
      if (receiptImage) { receiptImage.src = url; receiptImage.alt = label; }
      if (receiptTitle) receiptTitle.textContent = button.dataset.viewerTitle || (button.matches("[data-proof-viewer]") ? (t.paymentProof || "Payment proof") : (t.receiptPhotos || "Receipt photos"));
      if (receiptCounter) receiptCounter.textContent = t.receiptPhotoCounter ? t.receiptPhotoCounter.replace("{0}", activeIndex + 1).replace("{1}", activeButtons.length) : `Photo ${activeIndex + 1} of ${activeButtons.length}`;
      if (previous) previous.disabled = activeButtons.length < 2;
      if (next) next.disabled = activeButtons.length < 2;
      zoom = 1; applyZoom();
    };
    const closeReceipt = () => { receiptModal.hidden = true; document.body.classList.remove("receipt-modal-open"); returnFocus?.focus(); };
    viewerButtons.forEach(button => button.addEventListener("click", event => { event.preventDefault(); const group = button.dataset.viewerGroup || (button.matches("[data-proof-viewer]") ? "proof" : "receipt"); activeButtons = viewerButtons.filter(item => (item.dataset.viewerGroup || (item.matches("[data-proof-viewer]") ? "proof" : "receipt")) === group); returnFocus = button; receiptModal.hidden = false; document.body.classList.add("receipt-modal-open"); showReceipt(activeButtons.indexOf(button)); receiptModal.querySelector("[data-receipt-close]")?.focus(); }));
    receiptModal.querySelectorAll("[data-receipt-close]").forEach(button => button.addEventListener("click", closeReceipt));
    previous?.addEventListener("click", () => showReceipt(activeIndex - 1));
    next?.addEventListener("click", () => showReceipt(activeIndex + 1));
    receiptModal.querySelector("[data-receipt-zoom-in]")?.addEventListener("click", () => { zoom = Math.min(3, zoom + .25); applyZoom(); });
    receiptModal.querySelector("[data-receipt-zoom-out]")?.addEventListener("click", () => { zoom = Math.max(1, zoom - .25); applyZoom(); });
    receiptModal.querySelector("[data-receipt-zoom-reset]")?.addEventListener("click", () => { zoom = 1; applyZoom(); });
    receiptModal.addEventListener("keydown", event => {
      if (event.key === "Escape") closeReceipt();
      if (event.key === "ArrowLeft" && activeButtons.length > 1) showReceipt(activeIndex - 1);
      if (event.key === "ArrowRight" && activeButtons.length > 1) showReceipt(activeIndex + 1);
    });
  }
});
