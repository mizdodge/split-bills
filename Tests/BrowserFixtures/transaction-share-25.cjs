/* Optional local clipping regression. Run from the repository root with a
   Playwright installation: node Tests/BrowserFixtures/transaction-share-25.cjs */
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1080, height: 900 } });
    const source = fs.readFileSync(path.resolve(__dirname, "../../wwwroot/js/transaction-share.js"), "utf8");
    await page.setContent("<!doctype html><html><body></body></html>");
    await page.addScriptTag({ content: source });
    const result = await page.evaluate(async () => {
      const participants = Array.from({ length: 25 }, (_, i) => ({
        participantKey: `p${i + 1}`, displayName: `Participant ${i + 1}`,
        paymentStatusText: i % 3 === 0 ? "Paid" : "Unpaid", menuSubtotalText: `IDR ${10000 + i}`,
        finalAmountText: `IDR ${12000 + i}`, items: [
          { name: `Menu item ${i + 1} with a deliberately long label`, shareDescription: "× 1", amountText: `IDR ${10000 + i}`, sharedWithText: null }
        ], adjustments: []
      }));
      const data = { language: "en-US", merchantName: "Long fixture merchant", transactionNumber: "TRX-FIXTURE-25", transactionDateText: "11 Sep 2026", pickupPersonName: "Participant 1", participantCountText: "25 participants", paymentSummary: { paidCountText: "9 paid", awaitingCountText: "0 awaiting confirmation", unpaidCountText: "16 unpaid", collectedAmountText: "IDR 100000", outstandingAmountText: "IDR 200000" }, participants, receiptSummary: { subtotalText: "IDR 300000", adjustments: [], grandTotalText: "IDR 300000" } };
      const canvas = document.createElement("canvas");
      const painted = window.SplitBillShare.paint(canvas, data);
      const blob = await window.SplitBillShare.toJpeg(canvas);
      const context = canvas.getContext("2d");
      const sample = context.getImageData(0, Math.max(0, canvas.height - 80), canvas.width, Math.min(80, canvas.height)).data;
      return { ...painted, blobType: blob.type, blobSize: blob.size, nonEmptyFinalPixels: Array.from(sample).some((value, index) => index % 4 !== 3 && value !== 255) };
    });
    if (result.width !== 1000 || result.height <= 900 || result.paintedParticipantCount !== 25 || result.finalCursor > result.height || result.blobType !== "image/jpeg" || result.blobSize <= 0 || !result.nonEmptyFinalPixels) throw new Error(`Share fixture failed: ${JSON.stringify(result)}`);
    console.log(JSON.stringify(result));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
