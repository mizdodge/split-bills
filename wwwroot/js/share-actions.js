(function (global) {
  "use strict";
  const strings = global.splitBillStrings || {};
  const text = (key, fallback) => strings[key] || fallback;

  function csrf(root) {
    const value = root.querySelector("input[name='__RequestVerificationToken']")?.value;
    const body = new FormData();
    if (value) body.append("__RequestVerificationToken", value);
    return body;
  }
  async function post(root, endpoint) {
    if (!endpoint) throw new Error("share-link-endpoint-missing");
    const response = await fetch(endpoint, { method: "POST", body: csrf(root), credentials: "same-origin", cache: "no-store" });
    if (!response.ok) throw new Error("share-link-request-failed");
    return response.json();
  }
  async function copyValue(value, field) {
    if (global.isSecureContext && navigator.clipboard?.writeText) {
      try { await navigator.clipboard.writeText(value); return true; } catch (_) { /* Legacy path below. */ }
    }
    const temporary = document.createElement("textarea");
    temporary.value = value; temporary.readOnly = true; temporary.setAttribute("aria-hidden", "true");
    temporary.style.cssText = "position:fixed;left:-10000px;top:0;opacity:0";
    document.body.appendChild(temporary); temporary.focus(); temporary.select();
    let copied = false; try { copied = document.execCommand("copy"); } catch (_) { copied = false; }
    temporary.remove();
    if (!copied && field) { field.hidden = false; field.value = value; field.focus(); field.select(); }
    return copied;
  }
  function showManual(field, value) { if (field) { field.hidden = false; field.value = value; field.focus(); field.select(); } }

  function setup(root) {
    const endpoint = root.dataset.shareEndpoint;
    const dialog = root.querySelector("[data-share-dialog]");
    const openButton = document.querySelector("[data-share-open]");
    const canvas = root.querySelector("canvas") || document.createElement("canvas");
    let payload = null; let busy = false; let jpgLoaded = false;
    const setStatus = (value, error = false) => root.querySelectorAll("[data-share-status]").forEach(target => { target.textContent = value || ""; target.classList.toggle("text-danger", error); });
    const setBusy = value => { busy = value; root.querySelectorAll("[data-share-download], [data-share-native], [data-share-generate], [data-link-action]").forEach(button => { button.disabled = value; button.setAttribute("aria-busy", value ? "true" : "false"); }); };
    const fetchPayload = async () => {
      if (!endpoint || !global.SplitBillShare) throw new Error("share-data-unavailable");
      if (payload) return payload;
      const response = await fetch(endpoint, { headers: { Accept: "application/json" }, cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw new Error("share-data-failed");
      payload = await response.json(); return payload;
    };
    const participantsOf = data => data?.participants ?? data?.Participants ?? [];
    const valueOf = (item, camel, pascal) => item?.[camel] ?? item?.[pascal] ?? "";
    const selected = () => {
      if (!payload) return [];
      const mode = root.querySelector("input[name='share-mode']:checked")?.value || "all";
      const participants = participantsOf(payload);
      if (mode === "all" || !dialog) return participants;
      const keys = [...root.querySelectorAll("[data-share-participant]:checked")].map(x => x.value);
      if (mode === "individual") return participants.filter(x => valueOf(x, "participantKey", "ParticipantKey") === keys[0]);
      return participants.filter(x => keys.includes(valueOf(x, "participantKey", "ParticipantKey")));
    };
    const updateMode = () => {
      const mode = root.querySelector("input[name='share-mode']:checked")?.value || "all";
      const cards = root.querySelector("[data-share-participants]"); if (cards) cards.hidden = mode === "all";
      const inputs = [...root.querySelectorAll("[data-share-participant]")];
      inputs.forEach(input => { input.disabled = mode === "all"; });
      if (mode === "individual" && inputs.filter(input => input.checked).length !== 1) {
        inputs.forEach((input, index) => { input.checked = index === 0; });
      }
    };
    const renderCards = data => {
      const holder = root.querySelector("[data-share-participants]"); if (!holder) return;
      holder.querySelectorAll(".share-participant-card").forEach(card => card.remove());
      participantsOf(data).forEach(participant => {
        const label = document.createElement("label"); label.className = "share-participant-card";
        const input = document.createElement("input"); input.type = "checkbox"; input.name = "share-participant"; input.value = valueOf(participant, "participantKey", "ParticipantKey"); input.dataset.shareParticipant = ""; input.checked = true;
        const copy = document.createElement("span"); copy.className = "share-participant-copy";
        const name = document.createElement("strong"); name.textContent = valueOf(participant, "displayName", "DisplayName");
        const detail = document.createElement("small"); detail.textContent = `${valueOf(participant, "paymentStatusText", "PaymentStatusText")} · ${valueOf(participant, "finalAmountText", "FinalAmountText")}`;
        copy.append(name, detail); label.append(input, copy); holder.append(label);
      });
      holder.addEventListener("change", event => {
        if (root.querySelector("input[name='share-mode']:checked")?.value === "individual" && event.target.matches("[data-share-participant]")) {
          holder.querySelectorAll("[data-share-participant]").forEach(input => { input.checked = input === event.target; });
        }
        updateMode();
      });
      updateMode();
    };
    const close = () => {
      if (!dialog) return;
      if (dialog.open && typeof dialog.close === "function") dialog.close();
      else dialog.removeAttribute("open");
      dialog.hidden = true;
      document.body.classList.remove("share-dialog-open");
      openButton?.focus();
    };
    const activateJpg = async () => { if (jpgLoaded) return; setStatus(text("shareRendering", "Loading share options…")); try { const data = await fetchPayload(); renderCards(data); jpgLoaded = true; setStatus(""); } catch { setStatus(text("shareError", "The image could not be created. Try again."), true); } };
    const open = () => {
      if (!dialog || busy) return;
      dialog.hidden = false;
      try {
        if (typeof dialog.showModal === "function" && !dialog.open) dialog.showModal();
        else dialog.setAttribute("open", "");
      } catch (_) {
        // Older/private browsers may not implement the modal dialog API.
        dialog.setAttribute("open", "");
      }
      document.body.classList.add("share-dialog-open");
      dialog.querySelector("[data-share-close]")?.focus();
    };
    const setLinkFeedback = (holder, message, value = null) => { const feedback = holder.querySelector("[data-link-feedback]"); if (feedback) { feedback.replaceChildren(); feedback.textContent = message || ""; } const manual = holder.querySelector("[data-link-manual]"); if (manual) { manual.hidden = value === null; if (value !== null) showManual(manual, value); } };
    const copyLink = async (holder, endpointForCopy, onUrl = null) => {
      const button = holder.querySelector("[data-link-copy]"); if (button) button.disabled = true;
      try { const data = await post(root, endpointForCopy); if (!data.url) throw new Error("missing-url"); onUrl?.(data.url); const manual = holder.querySelector("[data-link-manual]"); const copied = await copyValue(data.url, manual); setLinkFeedback(holder, copied ? text("ShareLinkCopied", "Link copied.") : text("GuestLinkManualCopy", "Select the link to copy it manually."), copied ? null : data.url); }
      catch { setLinkFeedback(holder, text("ShareLinkUnavailable", "The guest link could not be loaded.")); }
      finally { if (button) button.disabled = false; }
    };
    const wireTransactionLink = () => {
      const holder = root.querySelector("[data-transaction-link]"); if (!holder) return;
      const urlField = holder.querySelector("[data-link-url]");
      holder.querySelector("[data-link-copy]")?.addEventListener("click", () => copyLink(holder, root.dataset.transactionLinkCopyEndpoint, url => { urlField.value = url; urlField.hidden = false; holder.querySelector("[data-transaction-link-state]").textContent = text("ShareLinkActive", "Active"); }));
      holder.querySelector("[data-link-regenerate]")?.addEventListener("click", async () => {
        if (!global.confirm(root.dataset.regenerateConfirm)) return;
        try { const data = await post(root, root.dataset.transactionLinkRegenerateEndpoint); urlField.value = data.url; urlField.hidden = false; holder.querySelector("[data-transaction-link-state]").textContent = text("ShareLinkActive", "Active"); setLinkFeedback(holder, text("ShareLinkRegenerated", "A new link is active; the old link was revoked.")); }
        catch { setLinkFeedback(holder, text("ShareLinkUnavailable", "The guest link could not be updated.")); }
      });
      root.querySelector("[data-transaction-link-revoke]")?.addEventListener("click", async () => {
        if (!global.confirm(root.dataset.revokeConfirm)) return;
        try { await post(root, root.dataset.transactionLinkRevokeEndpoint); urlField.value = ""; urlField.hidden = true; holder.querySelector("[data-transaction-link-state]").textContent = text("ShareLinkRevoked", "Revoked"); setLinkFeedback(holder, text("ShareLinkRevokedFeedback", "Guest access was revoked.")); }
        catch { setLinkFeedback(holder, text("ShareLinkUnavailable", "The guest link could not be updated.")); }
      });
    };
    const wireGuestLinks = () => root.querySelectorAll("[data-guest-link]").forEach(holder => {
      holder.querySelector("[data-link-copy]")?.addEventListener("click", () => copyLink(holder, holder.dataset.copyEndpoint));
      holder.querySelector("[data-link-regenerate]")?.addEventListener("click", async () => {
        if (!global.confirm(root.dataset.regenerateConfirm)) return;
        try { const data = await post(root, holder.dataset.regenerateEndpoint); const copied = await copyValue(data.url, holder.querySelector("[data-link-manual]")); holder.querySelector("[data-guest-link-state]").textContent = text("ShareLinkActive", "Active"); setLinkFeedback(holder, copied ? text("ShareLinkRegenerated", "A new link is active; the old link was revoked.") : text("GuestLinkManualCopy", "Select the link to copy it manually."), copied ? null : data.url); }
        catch { setLinkFeedback(holder, text("ShareLinkUnavailable", "The guest link could not be updated.")); }
      });
      holder.querySelector("[data-link-revoke]")?.addEventListener("click", async () => {
        if (!global.confirm(root.dataset.revokeConfirm)) return;
        try { await post(root, holder.dataset.revokeEndpoint); holder.querySelector("[data-guest-link-state]").textContent = text("ShareLinkRevoked", "Revoked"); setLinkFeedback(holder, text("ShareLinkRevokedFeedback", "Guest access was revoked.")); }
        catch { setLinkFeedback(holder, text("ShareLinkUnavailable", "The guest link could not be updated.")); }
      });
    });
    const setTab = async tab => { root.querySelectorAll("[data-share-tab]").forEach(button => { const active = button.dataset.shareTab === tab; button.classList.toggle("active", active); button.setAttribute("aria-selected", active ? "true" : "false"); }); root.querySelectorAll("[data-share-panel]").forEach(panel => { panel.hidden = panel.dataset.sharePanel !== tab; }); setStatus(""); if (tab === "jpg") await activateJpg(); };
    openButton?.addEventListener("click", open); root.querySelectorAll("[data-share-close]").forEach(button => button.addEventListener("click", close)); dialog?.addEventListener("close", () => { dialog.hidden = true; document.body.classList.remove("share-dialog-open"); }); root.querySelectorAll("[data-share-tab]").forEach(button => button.addEventListener("click", () => setTab(button.dataset.shareTab))); root.querySelectorAll("input[name='share-mode']").forEach(input => input.addEventListener("change", updateMode));
    root.querySelector("[data-share-download]")?.addEventListener("click", () => generate(false)); root.querySelector("[data-share-native]")?.addEventListener("click", () => generate(true)); root.querySelector("[data-share-generate]")?.addEventListener("click", () => generate(false)); dialog?.addEventListener("click", event => { if (event.target === dialog) close(); }); dialog?.addEventListener("keydown", event => { if (event.key === "Escape") close(); });
    wireTransactionLink(); wireGuestLinks();
    async function generate(nativeShare) {
      if (busy || !endpoint || !global.SplitBillShare) return; setBusy(true); setStatus(text("shareRendering", "Preparing image…"));
      try { const data = await fetchPayload(); const people = selected(); if (!people.length) throw new Error("no-participants"); const result = global.SplitBillShare.paint(canvas, { ...data, participants: people }); const blob = await global.SplitBillShare.toJpeg(result.canvas); const file = new File([blob], data.fileName, { type: "image/jpeg" }); if (nativeShare && navigator.share && navigator.canShare && navigator.canShare({ files: [file] })) { await navigator.share({ title: data.merchantName, files: [file] }); setStatus(text("shareReady", "Image ready to share.")); } else { const objectUrl = URL.createObjectURL(blob); const anchor = document.createElement("a"); anchor.href = objectUrl; anchor.download = data.fileName; anchor.click(); setTimeout(() => URL.revokeObjectURL(objectUrl), 1000); setStatus(text("shareFallback", "File sharing is unavailable in this browser, so the JPG was downloaded instead.")); } }
      catch (error) { if (error?.name !== "AbortError") setStatus(error?.message === "Share image is too long; select fewer participants." ? text("shareTooTall", "The details are too long. Select fewer participants.") : text("shareError", "The image could not be created. Try again."), true); } finally { setBusy(false); }
    }
  }
  document.querySelectorAll("[data-share-export]").forEach(setup);
})(window);
