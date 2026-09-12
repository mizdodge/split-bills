/* SplitBill long-share renderer. First-party Canvas 2D only; no CDN or DOM screenshot. */
(function (global) {
  "use strict";

  const WIDTH = 1000;
  const PAD = 56;
  const CONTENT = WIDTH - PAD * 2;
  const MAX_HEIGHT = 30000;
  const COLORS = { emerald: "#003A40", lime: "#CDFF70", stone: "#444547", white: "#FFFFFF", cool: "#F2F0FA", muted: "#66727a", danger: "#c23636" };

  const normalize = value => value == null ? "" : String(value);
  // ShareData is serialized with the ASP.NET Web (camelCase) defaults. Keep
  // the renderer defensive for older cached payloads and null display fields;
  // this is display normalization only and contains no financial calculation.
  const valueOf = (object, camelName, pascalName = camelName) => normalize(object?.[camelName] ?? object?.[pascalName]);
  const listOf = (object, camelName, pascalName = camelName) => {
    const value = object?.[camelName] ?? object?.[pascalName];
    return Array.isArray(value) ? value : [];
  };
  const wrap = (ctx, value, maxWidth) => {
    const words = normalize(value).split(/\s+/).filter(Boolean);
    if (words.length === 0) return [""];
    const lines = [];
    let current = "";
    for (const word of words) {
      const candidate = current ? `${current} ${word}` : word;
      if (current && ctx.measureText(candidate).width > maxWidth) { lines.push(current); current = word; }
      else current = candidate;
    }
    if (current) lines.push(current);
    return lines;
  };

  const font = (size, weight = 400) => `${weight} ${size}px Arial, sans-serif`;
  const textHeight = (lines, lineHeight) => Math.max(1, lines.length) * lineHeight;

  function layout(data, ctx) {
    const commands = [];
    const labels = data.labels || data.Labels || {};
    const collectedLabel = labels.collected || "collected";
    const outstandingLabel = labels.outstanding || "outstanding";
    let cursor = PAD;
    const addText = (text, x, y, maxWidth, size = 22, weight = 400, color = COLORS.stone, lineHeight = 30) => {
      ctx.font = font(size, weight);
      const lines = wrap(ctx, text, maxWidth);
      commands.push({ type: "text", text: lines, x, y: cursor + y, size, weight, color, lineHeight });
      return textHeight(lines, lineHeight);
    };
    const addRule = (y, color = "#d9dde0") => commands.push({ type: "rule", y: cursor + y, color });
    const addBox = (y, height, fill, stroke = null, radius = 18) => commands.push({ type: "box", y: cursor + y, height, fill, stroke, radius });
    const advance = amount => { cursor += amount; };

    addBox(0, 106, COLORS.emerald);
    commands.push({ type: "brand", y: cursor });
    addText(valueOf(data, "merchantName", "MerchantName"), PAD + 116, 18, CONTENT - 116, 30, 800, COLORS.white, 36);
    addText(`${valueOf(data, "transactionDateText", "TransactionDateText")} · ${valueOf(data, "transactionNumber", "TransactionNumber")}`, PAD + 116, 58, CONTENT - 116, 18, 400, "#d8f1ee", 25);
    commands.push({ type: "language", value: valueOf(data, "language", "Language") === "en-US" ? "EN" : "ID", y: cursor });
    advance(130);

    addText(valueOf(data, "participantCountText", "ParticipantCountText"), PAD, 0, CONTENT, 20, 700, COLORS.emerald, 28);
    const paymentSummary = data.paymentSummary || data.PaymentSummary || {};
    addText(`${valueOf(paymentSummary, "paidCountText", "PaidCountText")} · ${valueOf(paymentSummary, "awaitingCountText", "AwaitingCountText")} · ${valueOf(paymentSummary, "unpaidCountText", "UnpaidCountText")}`, PAD, 34, CONTENT, 18, 400, COLORS.muted, 25);
    addText(`${valueOf(paymentSummary, "collectedAmountText", "CollectedAmountText")} ${collectedLabel} · ${valueOf(paymentSummary, "outstandingAmountText", "OutstandingAmountText")} ${outstandingLabel}`, PAD, 67, CONTENT, 18, 700, COLORS.stone, 25);
    advance(112);

    const pickupPersonName = valueOf(data, "pickupPersonName", "PickupPersonName");
    if (pickupPersonName) {
      addBox(0, 62, COLORS.lime, null, 14);
      addText(`🛵  ${pickupPersonName}`, PAD + 20, 16, CONTENT - 40, 22, 800, COLORS.emerald, 28);
      advance(78);
    }

    let paintedParticipants = 0;
    for (const participant of listOf(data, "participants", "Participants")) {
      const itemLines = [];
      ctx.font = font(18, 400);
      for (const item of listOf(participant, "items", "Items")) {
        const name = valueOf(item, "name", "Name");
        const shareDescription = valueOf(item, "shareDescription", "ShareDescription");
        const sharedWith = valueOf(item, "sharedWithText", "SharedWithText");
        const label = [name, shareDescription].filter(Boolean).join(" ") + (sharedWith ? ` · ${sharedWith}` : "");
        itemLines.push({ label, lines: wrap(ctx, label, CONTENT - 210), amount: valueOf(item, "amountText", "AmountText") });
      }
      const adjustmentLines = listOf(participant, "adjustments", "Adjustments").map(adjustment => {
        const labelText = valueOf(adjustment, "label", "Label");
        const percentageText = valueOf(adjustment, "percentageText", "PercentageText");
        const label = percentageText ? `${labelText} · ${percentageText}` : labelText;
        ctx.font = font(17, 400);
        return { label, lines: wrap(ctx, label, CONTENT - 210), amount: `${(adjustment.isSubtract ?? adjustment.IsSubtract) ? "−" : "+"} ${valueOf(adjustment, "amountText", "AmountText")}` };
      });
      let height = 72 + itemLines.reduce((sum, row) => sum + Math.max(1, row.lines.length) * 27 + 12, 0);
      height += 42 + adjustmentLines.reduce((sum, row) => sum + Math.max(1, row.lines.length) * 26 + 10, 0);
      height += 84;
      addBox(0, height, COLORS.white, "#d9dde0", 18);
      commands.push({ type: "participantHeader", name: valueOf(participant, "displayName", "DisplayName"), status: valueOf(participant, "paymentStatusText", "PaymentStatusText"), y: cursor + 22 });
      let inner = 70;
      for (const row of itemLines) {
        commands.push({ type: "row", label: row.lines, amount: row.amount, x: PAD + 24, y: cursor + inner, lineHeight: 27, color: COLORS.stone, size: 18 });
        inner += Math.max(1, row.lines.length) * 27 + 12;
      }
      addRule(inner + 2);
      commands.push({ type: "labelAmount", label: labels.menuSubtotal || labels.MenuSubtotal || "Menu subtotal", amount: valueOf(participant, "menuSubtotalText", "MenuSubtotalText"), y: cursor + inner + 18, weight: 700 });
      inner += 54;
      for (const row of adjustmentLines) {
        commands.push({ type: "row", label: row.lines, amount: row.amount, x: PAD + 24, y: cursor + inner, lineHeight: 26, color: row.amount.startsWith("−") ? COLORS.danger : COLORS.emerald, size: 17 });
        inner += Math.max(1, row.lines.length) * 26 + 10;
      }
      commands.push({ type: "labelAmount", label: labels.total || labels.Total || "Total", amount: valueOf(participant, "finalAmountText", "FinalAmountText"), y: cursor + height - 43, weight: 800, size: 22 });
      advance(height + 18);
      paintedParticipants++;
    }

    const summary = data.receiptSummary || data.ReceiptSummary || {};
    const receiptRows = listOf(summary, "adjustments", "Adjustments").map(adjustment => {
      const labelText = valueOf(adjustment, "label", "Label");
      const percentageText = valueOf(adjustment, "percentageText", "PercentageText");
      const label = percentageText ? `${labelText} · ${percentageText}` : labelText;
      ctx.font = font(18, 400);
      return { label, lines: wrap(ctx, label, CONTENT - 210), amount: `${(adjustment.isSubtract ?? adjustment.IsSubtract) ? "−" : "+"} ${valueOf(adjustment, "amountText", "AmountText")}` };
    });
    const receiptRowsHeight = receiptRows.reduce((sum, row) => sum + Math.max(1, row.lines.length) * 27 + 11, 0);
    const summaryHeight = 168 + receiptRowsHeight;
    addBox(0, summaryHeight, COLORS.cool, null, 18);
    commands.push({ type: "summaryTitle", label: labels.receiptSummary || labels.ReceiptSummary || "Receipt summary", y: cursor + 24 });
    commands.push({ type: "labelAmount", label: labels.subtotal || labels.Subtotal || "Subtotal", amount: valueOf(summary, "subtotalText", "SubtotalText"), y: cursor + 63, weight: 700 });
    let summaryInner = 96;
    for (const row of receiptRows) {
      commands.push({ type: "row", label: row.lines, amount: row.amount, x: PAD + 24, y: cursor + summaryInner, lineHeight: 27, color: row.amount.startsWith("−") ? COLORS.danger : COLORS.emerald, size: 18 });
      summaryInner += Math.max(1, row.lines.length) * 27 + 11;
    }
    commands.push({ type: "labelAmount", label: labels.grandTotal || labels.GrandTotal || "Grand total", amount: valueOf(summary, "grandTotalText", "GrandTotalText"), y: cursor + summaryHeight - 44, weight: 800, size: 24, color: COLORS.emerald });
    const finalTotalY = cursor + summaryHeight - 44;
    advance(summaryHeight + 28);
    addRule(0, COLORS.emerald);
    addText(labels.generatedBy || labels.GeneratedBy || "Generated by SplitBill", PAD, 18, CONTENT, 16, 400, COLORS.muted, 22);
    advance(48);

    return { commands, width: WIDTH, height: cursor, paintedParticipants, finalTotalY, finalTotalBlock: true };
  }

  function paint(canvas, data) {
    const participants = data?.participants ?? data?.Participants;
    if (!canvas || !data || !Array.isArray(participants)) throw new Error("Share data is incomplete.");
    const measureCanvas = document.createElement("canvas");
    const measureContext = measureCanvas.getContext("2d");
    if (!measureContext) throw new Error("Canvas is unavailable.");
    const measured = layout(data, measureContext);
    if (measured.height > MAX_HEIGHT) {
      const error = new Error("Share image is too long; select fewer participants.");
      error.code = "SHARE_IMAGE_TOO_TALL";
      throw error;
    }
    canvas.width = WIDTH;
    canvas.height = Math.ceil(measured.height);
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Canvas is unavailable.");
    context.fillStyle = COLORS.white;
    context.fillRect(0, 0, canvas.width, canvas.height);
    for (const command of measured.commands) {
      if (command.type === "box") {
        context.beginPath();
        context.roundRect(PAD, command.y, CONTENT, command.height, command.radius);
        context.fillStyle = command.fill; context.fill();
        if (command.stroke) { context.strokeStyle = command.stroke; context.lineWidth = 1; context.stroke(); }
      } else if (command.type === "text") {
        context.font = font(command.size, command.weight); context.fillStyle = command.color;
        command.text.forEach((line, index) => context.fillText(line, command.x, command.y + command.size + index * command.lineHeight));
      } else if (command.type === "rule") {
        context.strokeStyle = command.color; context.lineWidth = 1; context.beginPath(); context.moveTo(PAD, command.y); context.lineTo(WIDTH - PAD, command.y); context.stroke();
      } else if (command.type === "brand") {
        context.fillStyle = COLORS.lime; context.beginPath(); context.roundRect(PAD + 22, command.y + 23, 70, 58, 14); context.fill();
        context.fillStyle = COLORS.emerald; context.font = font(34, 800); context.fillText("S", PAD + 45, command.y + 63);
      } else if (command.type === "language") {
        context.fillStyle = COLORS.lime; context.beginPath(); context.roundRect(WIDTH - PAD - 88, command.y + 29, 64, 42, 21); context.fill(); context.fillStyle = COLORS.emerald; context.font = font(18, 800); context.fillText(command.value, WIDTH - PAD - 70, command.y + 56);
      } else if (command.type === "participantHeader") {
        context.fillStyle = COLORS.emerald; context.font = font(24, 800); context.fillText(command.name, PAD + 24, command.y + 25);
        context.fillStyle = COLORS.muted; context.font = font(17, 700); context.fillText(command.status, WIDTH - PAD - context.measureText(command.status).width - 24, command.y + 24);
      } else if (command.type === "row") {
        context.font = font(command.size, 400); context.fillStyle = command.color;
        command.label.forEach((line, index) => context.fillText(line, command.x, command.y + command.size + index * command.lineHeight));
        context.font = font(command.size, 700); context.fillText(command.amount, WIDTH - PAD - context.measureText(command.amount).width - 24, command.y + command.size);
      } else if (command.type === "labelAmount") {
        context.font = font(command.size || 18, command.weight || 400); context.fillStyle = command.color || COLORS.stone; context.fillText(command.label, PAD + 24, command.y);
        context.fillText(command.amount, WIDTH - PAD - context.measureText(command.amount).width - 24, command.y);
      } else if (command.type === "summaryTitle") {
        context.fillStyle = COLORS.emerald; context.font = font(22, 800); context.fillText(command.label, PAD + 24, command.y + 24);
      }
    }
    if (measured.paintedParticipants !== participants.length || !measured.finalTotalBlock || measured.finalTotalY > canvas.height) {
      throw new Error("Share image paint verification failed.");
    }
    return { width: canvas.width, height: canvas.height, paintedParticipantCount: measured.paintedParticipants, finalCursor: measured.height, finalTotalPainted: true, canvas };
  }

  function toJpeg(canvas, quality = 0.94) {
    return new Promise((resolve, reject) => {
      canvas.toBlob(blob => blob && blob.size > 0 && blob.type === "image/jpeg" ? resolve(blob) : reject(new Error("JPEG export failed.")), "image/jpeg", quality);
    });
  }

  global.SplitBillShare = { WIDTH, MAX_HEIGHT, wrap, layout, paint, toJpeg };
})(window);
