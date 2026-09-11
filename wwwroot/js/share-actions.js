(function (global) {
  "use strict";
  const strings = global.splitBillStrings || {};
  const text = (key, fallback) => strings[key] || fallback;

  function setup(root) {
    const endpoint = root.dataset.shareEndpoint;
    if (!endpoint || !global.SplitBillShare) return;
    const dialog = root.querySelector("[data-share-dialog]");
    const status = root.querySelector("[data-share-status]");
    const canvas = root.querySelector("canvas") || document.createElement("canvas");
    let payload = null;
    let busy = false;

    const setStatus = (value, error = false) => root.querySelectorAll("[data-share-status]").forEach(target => { target.textContent = value || ""; target.classList.toggle("text-danger", error); });
    const setBusy = value => {
      busy = value;
      root.querySelectorAll("[data-share-download], [data-share-native], [data-share-generate]").forEach(button => { button.disabled = value; button.setAttribute("aria-busy", value ? "true" : "false"); });
    };
    const fetchPayload = async () => {
      if (payload) return payload;
      const response = await fetch(endpoint, { headers: { Accept: "application/json" }, cache: "no-store" });
      if (!response.ok) throw new Error("share-data-failed");
      payload = await response.json();
      return payload;
    };
    const selected = () => {
      if (!payload) return [];
      const mode = root.querySelector("input[name='share-mode']:checked")?.value || "all";
      if (mode === "all" || !dialog) return payload.participants || [];
      const keys = [...root.querySelectorAll("[data-share-participant]:checked")].map(x => x.value);
      if (mode === "individual") return (payload.participants || []).filter(x => x.participantKey === keys[0]);
      return (payload.participants || []).filter(x => keys.includes(x.participantKey));
    };
    const updateMode = () => {
      const mode = root.querySelector("input[name='share-mode']:checked")?.value || "all";
      const cards = root.querySelector("[data-share-participants]");
      if (cards) cards.hidden = mode === "all";
      root.querySelectorAll("[data-share-participant]").forEach(input => { input.disabled = mode === "all"; if (mode === "individual" && input.checked) { root.querySelectorAll("[data-share-participant]").forEach(other => { if (other !== input) other.checked = false; }); } });
      const count = root.querySelector("[data-share-count]");
      if (count && payload) count.textContent = `${selected().length}/${payload.participants.length}`;
    };
    const renderCards = data => {
      const holder = root.querySelector("[data-share-participants]");
      if (!holder) return;
      holder.replaceChildren(...(data.participants || []).map(participant => {
        const label = document.createElement("label"); label.className = "share-participant-card";
        const input = document.createElement("input"); input.type = "checkbox"; input.name = "share-participant"; input.value = participant.participantKey; input.dataset.shareParticipant = ""; input.checked = true;
        const copy = document.createElement("span"); copy.className = "share-participant-copy";
        const name = document.createElement("strong"); name.textContent = participant.displayName;
        const detail = document.createElement("small"); detail.textContent = `${participant.paymentStatusText} · ${participant.finalAmountText}`;
        copy.append(name, detail); label.append(input, copy); holder.append(label);
      }));
      holder.addEventListener("change", updateMode, { once: true });
      updateMode();
    };
    const close = () => { if (dialog) { dialog.hidden = true; document.body.classList.remove("share-dialog-open"); root.querySelector("[data-share-open]")?.focus(); } };
    const open = async () => {
      if (busy) return;
      try { setStatus(text("shareRendering", "Loading share options…")); const data = await fetchPayload(); renderCards(data); if (dialog) { dialog.hidden = false; document.body.classList.add("share-dialog-open"); dialog.querySelector("[data-share-close]")?.focus(); } setStatus(""); }
      catch { setStatus(text("shareError", "The image could not be created. Try again."), true); }
    };
    const download = (blob, filename) => {
      const url = URL.createObjectURL(blob); const anchor = document.createElement("a"); anchor.href = url; anchor.download = filename; anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    };
    const generate = async (nativeShare) => {
      if (busy) return;
      setBusy(true); setStatus(text("shareRendering", "Preparing image…"));
      try {
        const data = await fetchPayload();
        const people = selected();
        if (!people.length) throw new Error("no-participants");
        const result = global.SplitBillShare.paint(canvas, { ...data, participants: people });
        const blob = await global.SplitBillShare.toJpeg(result.canvas);
        const file = new File([blob], data.fileName, { type: "image/jpeg" });
        if (nativeShare && navigator.share && navigator.canShare && navigator.canShare({ files: [file] })) {
          await navigator.share({ title: data.merchantName, files: [file] });
          setStatus(text("shareReady", "Image ready to share."));
        } else {
          download(blob, data.fileName);
          setStatus(text("shareFallback", "File sharing is unavailable in this browser, so the JPG was downloaded instead."));
        }
      } catch (error) {
        if (error?.name !== "AbortError") setStatus(error?.message === "Share image is too long; select fewer participants." ? text("shareTooTall", "The details are too long. Select fewer participants.") : text("shareError", "The image could not be created. Try again."), true);
      } finally { setBusy(false); }
    };

    root.querySelector("[data-share-open]")?.addEventListener("click", open);
    root.querySelector("[data-share-close]")?.addEventListener("click", close);
    root.querySelectorAll("input[name='share-mode']").forEach(input => input.addEventListener("change", updateMode));
    root.querySelector("[data-share-download]")?.addEventListener("click", () => generate(false));
    root.querySelector("[data-share-native]")?.addEventListener("click", () => generate(true));
    root.querySelector("[data-share-generate]")?.addEventListener("click", () => generate(false));
    dialog?.addEventListener("click", event => { if (event.target === dialog) close(); });
    dialog?.addEventListener("keydown", event => { if (event.key === "Escape") close(); });
    if (!dialog) root.querySelector("[data-share-download]")?.setAttribute("aria-label", text("shareMyBill", "Share my bill"));
  }
  document.querySelectorAll("[data-share-export]").forEach(setup);
})(window);
