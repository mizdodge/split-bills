using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize(Roles = $"{DatabaseSeeder.AdminRole},{DatabaseSeeder.ModeratorRole}")]
public sealed class TransactionsController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    IAiReceiptService aiReceiptService, ISplitBillCalculator calculator, IWebHostEnvironment environment,
    IUploadedImageProcessor? imageProcessor = null,
    IPaymentWorkflowService? paymentWorkflowService = null, IStringLocalizer<SharedResource>? localizer = null,
    IPaymentProofStorageService? proofStorage = null,
    ISharePointNotificationOutboxService? sharePointNotifications = null,
    IFoodPickupRotationService? pickupRotationService = null,
    ITransactionShareExportService? shareExportService = null,
    ICurrencyCatalog? currencyCatalog = null,
    ICurrencyRateService? currencyRateService = null,
    IGuestAccessService? guestAccessService = null) : Controller
{
    private static readonly JsonSerializerOptions ParticipantJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IActionResult> Index() => View(new TransactionListViewModel
    {
        Transactions = (await ScopedTransactions().AsNoTracking().Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals).Include(x => x.Participants).ThenInclude(x => x.PaymentHistories).ToListAsync())
            .OrderByDescending(x => x.UploadDate).ToList()
    });

    [HttpGet]
    public IActionResult Upload() => View(new UploadReceiptViewModel());

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(33_554_432)]
    public async Task<IActionResult> Upload(UploadReceiptViewModel model, CancellationToken cancellationToken)
    {
        var validationError = ReceiptUploadValidator.Validate(model.ReceiptImages);
        if (validationError is not null) ModelState.AddModelError(nameof(model.ReceiptImages), validationError);
        if (!ModelState.IsValid) return View(model);

        var processor = imageProcessor ?? new UploadedImageProcessor();
        var processedImages = new List<ProcessedImage>(model.ReceiptImages.Count);
        foreach (var image in model.ReceiptImages)
        {
            try
            {
                processedImages.Add(await processor.ProcessAsync(image, cancellationToken));
            }
            catch (UploadedImageException exception)
            {
                ModelState.AddModelError(nameof(model.ReceiptImages), exception.Message);
            }
        }
        if (!ModelState.IsValid) return View(model);

        var storedImages = new List<TransactionReceiptImage>(model.ReceiptImages.Count);
        for (var index = 0; index < processedImages.Count; index++)
        {
            var image = processedImages[index];
            var fileName = $"{Guid.NewGuid():N}{image.Extension}";
            var physicalPath = Path.Combine(environment.ContentRootPath, "App_Data", "receipts", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
            await System.IO.File.WriteAllBytesAsync(physicalPath, image.Bytes, cancellationToken);
            storedImages.Add(new TransactionReceiptImage
            {
                FileName = fileName, ContentType = image.ContentType, SortOrder = index
            });
        }

        var now = DateTimeOffset.UtcNow;
        var transaction = new BillTransaction
        {
            TransactionNumber = $"TRX-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            MerchantName = "Struk baru", UploadDate = now, UploadedByUserId = userManager.GetUserId(User)!,
            ReceiptImagePath = storedImages[0].FileName, ReceiptImages = storedImages,
            Status = TransactionStatus.Draft, AiNeedsReview = true,
            CreatedAt = now, UpdatedAt = now
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);

        var activeAi = await db.AiConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive, cancellationToken);
        var log = new ReceiptProcessingLog
        {
            TransactionId = transaction.Id, Provider = activeAi?.Provider ?? AiProvider.OpenAi,
            Model = activeAi?.Model ?? string.Empty, ApiMode = activeAi?.ApiMode ?? AiApiMode.Responses,
            Status = AiProcessingStatus.Processing, StartedAt = now
        };
        db.ReceiptProcessingLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var inputs = processedImages.Select(image =>
                new ReceiptImageInput(new MemoryStream(image.Bytes, writable: false), image.ContentType)).ToList();
            try
            {
                var (result, rawResponse) = await aiReceiptService.AnalyzeAsync(inputs, cancellationToken);
                ApplyAiResult(transaction, result, rawResponse);
                NormalizeCurrency(transaction);
            }
            finally
            {
                foreach (var input in inputs) await input.Stream.DisposeAsync();
            }
            log.Status = AiProcessingStatus.Succeeded;
            log.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = Text("ReceiptReadSuccess", "Struk berhasil dibaca. Periksa hasil AI sebelum melanjutkan.");
        }
        catch (Exception exception) when (exception is AiServiceException or JsonException)
        {
            log.Status = AiProcessingStatus.Failed;
            log.ErrorMessage = exception.Message;
            log.FinishedAt = DateTimeOffset.UtcNow;
            transaction.AiWarningsJson = JsonSerializer.Serialize(new[] { exception.Message });
            await db.SaveChangesAsync(cancellationToken);
            TempData["Error"] = Text("AiReadFailed", $"AI belum berhasil membaca struk: {exception.Message} Data tetap disimpan dan bisa diisi manual.", exception.Message);
        }
        return RedirectToAction(nameof(Review), new { id = transaction.Id });
    }

    public async Task<IActionResult> ReceiptImage(long id, int index = 0)
    {
        var transaction = await ScopedTransactions().AsNoTracking().Include(x => x.ReceiptImages).SingleOrDefaultAsync(x => x.Id == id);
        if (transaction is null) return NotFound();
        var images = StoredReceiptImages(transaction);
        if (index < 0 || index >= images.Count) return NotFound();
        var image = images[index];
        var fileName = Path.GetFileName(image.FileName);
        var physicalPath = Path.Combine(environment.ContentRootPath, "App_Data", "receipts", fileName);
        if (!System.IO.File.Exists(physicalPath)) return NotFound();
        var contentType = string.IsNullOrWhiteSpace(image.ContentType) ? ContentTypeFor(fileName) : image.ContentType;
        return PhysicalFile(physicalPath, contentType);
    }

    [HttpGet]
    public async Task<IActionResult> Review(long id)
    {
        var transaction = await ManageableTransaction(id).Include(x => x.Items).Include(x => x.Charges).Include(x => x.ReceiptImages)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentHistories).SingleOrDefaultAsync();
        if (transaction is null) return NotFound();
        if (!TransactionEditPolicy.Evaluate(transaction).Allowed)
        {
            TempData["Error"] = Text("EditLocked", "Transaksi tidak dapat diedit setelah aktivitas pembayaran dimulai.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var model = ToReviewViewModel(transaction);
        model.AvailableCurrencies = currencyCatalog?.GetAll() ?? [new CurrencyInfo("IDR", "Indonesian Rupiah", "Rp", 0)];
        model.ReportingCurrencyCode = transaction.ReportingCurrencyCode;
        if (currencyRateService is not null && !string.Equals(transaction.CurrencyCode, transaction.ReportingCurrencyCode, StringComparison.OrdinalIgnoreCase) && transaction.ExchangeRateToReporting <= 0 && transaction.TransactionDate is { } rateDate)
        {
            var rate = await currencyRateService.GetRateAsync(transaction.CurrencyCode, transaction.ReportingCurrencyCode, rateDate);
            if (rate is not null) { model.ExchangeRateToReporting = rate.Rate; model.ExchangeRateEffectiveDate = rate.EffectiveDate; model.ExchangeRateSource = rate.Source; model.ExchangeRateCaptureMode = ExchangeRateCaptureMode.Automatic; }
        }
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(ReviewTransactionViewModel model)
    {
        var transaction = await ManageableTransaction(model.Id).Include(x => x.Items).Include(x => x.Charges)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentHistories).SingleOrDefaultAsync();
        if (transaction is null) return NotFound();
        if (!TransactionEditPolicy.Evaluate(transaction).Allowed)
        {
            TempData["Error"] = Text("EditLocked", "Transaksi tidak dapat diedit setelah aktivitas pembayaran dimulai.");
            return RedirectToAction(nameof(Details), new { id = model.Id });
        }
        if (!model.TransactionDate.HasValue)
            ModelState.AddModelError(nameof(model.TransactionDate), Text("ReceiptDateRequired", "Tanggal struk belum terbaca. Isi tanggal secara manual untuk melanjutkan."));
        if (model.Subtotal < 0 || model.GrandTotal < 0 ||
            model.Items.Any(item => item.UnitPrice < 0 || item.TotalPrice < 0) ||
            model.Charges.Any(charge => charge.Amount < 0))
            ModelState.AddModelError(string.Empty, Text("NegativeAmount", "Nominal tidak boleh negatif. Masukkan potongan harga di kolom Diskon."));
        if (ModelState.IsValid)
        {
            ReceiptReviewNormalizer.Normalize(model);
            ModelState.Clear();
            if (model.GrandTotal <= 0)
                ModelState.AddModelError(string.Empty, Text("MissingReceiptTotals", "Isi Grand total, Subtotal, atau minimal satu nominal item untuk melanjutkan."));
        }
        if (!ModelState.IsValid)
        {
            model.TransactionNumber = transaction.TransactionNumber;
            model.ReceiptImagePath = transaction.ReceiptImagePath;
            model.AiConfidence = transaction.AiConfidence;
            model.AiNeedsReview = transaction.AiNeedsReview;
            model.AvailableCurrencies = currencyCatalog?.GetAll() ?? [new CurrencyInfo("IDR", "Indonesian Rupiah", "Rp", 0)];
            return View(model);
        }

        transaction.MerchantName = model.MerchantName!;
        transaction.TransactionDate = model.TransactionDate.HasValue ? DateOnly.FromDateTime(model.TransactionDate.Value) : null;
        transaction.Subtotal = model.Subtotal!.Value; transaction.Discount = model.Discount!.Value; transaction.Tax = model.Tax!.Value;
        transaction.ServiceCharge = model.ServiceCharge!.Value; transaction.GrandTotal = model.GrandTotal!.Value;
        var currencyCode = model.CurrencyCode.Trim().ToUpperInvariant();
        var reportingCode = (await db.CurrencyConfigurations.AsNoTracking().Where(x => x.Id == 1).Select(x => x.DefaultCurrencyCode).SingleOrDefaultAsync()) ?? "IDR";
        if (currencyCatalog is null || !currencyCatalog.TryGet(currencyCode, out _))
            ModelState.AddModelError(nameof(model.CurrencyCode), Text("UnsupportedCurrency", "Mata uang tidak didukung."));
        if (currencyCode != reportingCode && model.ExchangeRateToReporting <= 0)
            ModelState.AddModelError(nameof(model.ExchangeRateToReporting), Text("ExchangeRateRequired", "Masukkan kurs positif ke mata uang laporan."));
        if (currencyCode != reportingCode && model.ExchangeRateCaptureMode == ExchangeRateCaptureMode.Manual && string.IsNullOrWhiteSpace(model.ExchangeRateManualNote))
            ModelState.AddModelError(nameof(model.ExchangeRateManualNote), Text("ExchangeRateNoteRequired", "Tambahkan catatan sumber kurs manual."));
        if (!ModelState.IsValid)
        {
            model.AvailableCurrencies = currencyCatalog?.GetAll() ?? [];
            return View(model);
        }
        transaction.CurrencyCode = currencyCode;
        transaction.ReportingCurrencyCode = reportingCode;
        transaction.ExchangeRateToReporting = currencyCode == reportingCode ? 1m : model.ExchangeRateToReporting;
        transaction.ExchangeRateCaptureMode = currencyCode == reportingCode ? ExchangeRateCaptureMode.Identity : (model.ExchangeRateCaptureMode == ExchangeRateCaptureMode.Manual ? ExchangeRateCaptureMode.Manual : ExchangeRateCaptureMode.Automatic);
        transaction.ExchangeRateEffectiveDate = currencyCode == reportingCode ? transaction.TransactionDate : model.ExchangeRateEffectiveDate ?? transaction.TransactionDate;
        transaction.ExchangeRateCapturedAt = DateTimeOffset.UtcNow;
        transaction.ExchangeRateSource = string.IsNullOrWhiteSpace(model.ExchangeRateSource) ? (transaction.ExchangeRateCaptureMode == ExchangeRateCaptureMode.Manual ? "Manual" : "Review") : model.ExchangeRateSource.Trim();
        transaction.ExchangeRateManualNote = model.ExchangeRateManualNote?.Trim();
        transaction.AiNeedsReview = false; transaction.UpdatedAt = DateTimeOffset.UtcNow;
        db.TransactionParticipants.RemoveRange(transaction.Participants);
        db.TransactionItems.RemoveRange(transaction.Items);
        db.TransactionCharges.RemoveRange(transaction.Charges);
        transaction.Participants = [];
        transaction.AllocationPlanJson = null;
        transaction.Status = TransactionStatus.Draft;
        transaction.Items = model.Items.Select((item, index) => new TransactionItem
        {
            LineNumber = index + 1, Name = item.Name!, Quantity = Math.Max(1, item.Quantity!.Value),
            UnitPrice = item.UnitPrice!.Value, TotalPrice = item.TotalPrice!.Value, Confidence = 1
        }).ToList();
        transaction.Charges = model.Charges.Select((charge, index) => new TransactionCharge
        {
            Label = charge.Label!, Amount = charge.Amount!.Value,
            Operation = charge.Operation, SortOrder = index
        }).ToList();
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Split), new { id = transaction.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Split(long id)
    {
        var transaction = await ManageableTransaction(id).Include(x => x.Items).Include(x => x.Participants).ThenInclude(x => x.AccountLink)
            .Include(x => x.Participants).ThenInclude(x => x.ItemAllocations)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentHistories).SingleOrDefaultAsync();
        if (transaction is null) return NotFound();
        if (!TransactionEditPolicy.Evaluate(transaction).Allowed)
        {
            TempData["Error"] = Text("SplitLocked", "Pembagian tidak dapat diubah karena ada pembayaran yang sudah dibayar atau menunggu konfirmasi.");
            return RedirectToAction(nameof(Details), new { id });
        }
        return View(await ToSplitViewModelAsync(transaction));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Split(SplitTransactionViewModel model)
    {
        var transaction = await ManageableTransaction(model.TransactionId).Include(x => x.Items)
            .Include(x => x.Participants).ThenInclude(x => x.ItemAllocations)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentHistories).SingleOrDefaultAsync();
        if (transaction is null) return NotFound();
        var previousPickup = await db.FoodPickupAssignments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TransactionId == transaction.Id);
        var selections = await ParseParticipantSelectionsAsync(model);
        if (selections.Count == 0) ModelState.AddModelError(nameof(model.ParticipantsJson), Text("ChooseAtLeastOneParticipant", "Pilih minimal satu peserta."));
        var names = selections.Select(x => x.Name).ToList();

        SplitCalculation? calculation = null;
        List<PostedItemAssignment> postedAssignments = [];
        try
        {
            if (ModelState.IsValid && model.SplitMethod == SplitMethod.Equal)
                calculation = new SplitCalculation(calculator.SplitEqual(transaction.GrandTotal, names), []);
            else if (ModelState.IsValid)
            {
                postedAssignments = ParseAssignments(model.AssignmentsJson, transaction.Items);
                var keyToIndex = selections.Select((selection, index) => (selection.ClientKey, index))
                    .ToDictionary(x => x.ClientKey, x => x.index, StringComparer.Ordinal);
                var calculatorAssignments = new List<ItemGroupAssignment>();
                foreach (var assignment in postedAssignments)
                {
                    var groups = new List<AllocationGroup>();
                    foreach (var group in assignment.Groups)
                    {
                        var indexes = new List<int>();
                        foreach (var key in group.ParticipantKeys)
                        {
                            var resolvedKey = key.StartsWith("legacy-index:", StringComparison.Ordinal) && int.TryParse(key["legacy-index:".Length..], out var legacyIndex) && legacyIndex >= 0 && legacyIndex < selections.Count
                                ? selections[legacyIndex].ClientKey : key;
                            if (!keyToIndex.TryGetValue(resolvedKey, out var index))
                                ModelState.AddModelError(nameof(model.AssignmentsJson), Text("ChooseItemParticipant", "Ada penugasan item ke peserta yang tidak valid."));
                            else indexes.Add(index);
                        }
                        groups.Add(new AllocationGroup(group.Quantity, indexes));
                    }
                    calculatorAssignments.Add(new ItemGroupAssignment(assignment.ItemId, groups));
                }
                if (ModelState.IsValid) calculation = calculator.SplitByItemGroups(transaction.GrandTotal, names, transaction.Items, calculatorAssignments);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
        }
        if (!ModelState.IsValid || calculation is null)
        {
            await PopulateSplitViewModelAsync(model, transaction);
            return View(model);
        }
        if (!TransactionEditPolicy.Evaluate(transaction).Allowed)
        {
            ModelState.AddModelError(string.Empty, Text("SplitLocked", "Pembagian tidak dapat diubah karena ada pembayaran yang sudah dibayar atau menunggu konfirmasi."));
            await PopulateSplitViewModelAsync(model, transaction);
            return View(model);
        }

        await using var splitTransaction = await db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        FoodPickupDrawOperation? pickupOperation = null;
        var rotationEnabled = await FoodPickupRotationEnabledAsync();
        var requestedPickup = model.RequiresFoodPickup;
        // A crafted first-time ON request cannot bypass the global Admin switch.
        // An already assigned transaction may keep its winner while the global
        // switch is later disabled; turning it OFF still removes that winner.
        var keepExistingWhileDisabled = requestedPickup && previousPickup is not null && !rotationEnabled;
        transaction.RequiresFoodPickup = requestedPickup && (rotationEnabled || previousPickup is not null);
        db.TransactionParticipants.RemoveRange(transaction.Participants);
        transaction.Participants = calculation.ParticipantShares.Select((x, index) => new TransactionParticipant
        {
            Name = x.Name, Amount = x.Amount, PaymentStatus = ParticipantPaymentStatus.Unpaid,
            AccountLink = string.IsNullOrWhiteSpace(selections[index].UserId) ? null : new ParticipantAccountLink
            {
                UserId = selections[index].UserId!, LinkedAt = DateTimeOffset.UtcNow
            }
        }).ToList();
        await db.SaveChangesAsync();
        if (keepExistingWhileDisabled && previousPickup is not null)
        {
            var preservedParticipant = transaction.Participants.FirstOrDefault(x =>
                string.Equals(x.AccountLink?.UserId, previousPickup.SelectedUserId, StringComparison.Ordinal));
            if (preservedParticipant is not null)
                db.FoodPickupAssignments.Add(new FoodPickupAssignment
                {
                    TransactionId = transaction.Id,
                    SelectedUserId = previousPickup.SelectedUserId,
                    SelectedParticipantId = preservedParticipant.Id,
                    RecordedProbability = previousPickup.RecordedProbability,
                    Strategy = previousPickup.Strategy,
                    DrawKind = previousPickup.DrawKind,
                    SelectedAt = previousPickup.SelectedAt,
                    SelectedByUserId = previousPickup.SelectedByUserId
                });
        }
        else if (transaction.RequiresFoodPickup && pickupRotationService is not null)
            pickupOperation = await pickupRotationService.AssignOrReconcileAsync(transaction, userManager.GetUserId(User),
                previousPickup?.SelectedUserId, previousPickup, HttpContext.RequestAborted);
        if (model.SplitMethod == SplitMethod.ByItem)
        {
            var indexToParticipant = transaction.Participants.Select((participant, index) => (participant, index)).ToDictionary(x => x.index, x => x.participant);
            foreach (var itemResult in calculation.ItemAllocations)
            {
                var item = transaction.Items.Single(x => x.Id == itemResult.ItemId);
                foreach (var share in itemResult.Shares)
                    indexToParticipant[share.ParticipantIndex].ItemAllocations.Add(new ParticipantItemAllocation
                    {
                        Item = item, QuantityShare = share.QuantityShare, Amount = share.Amount
                    });
            }
            transaction.AllocationPlanJson = JsonSerializer.Serialize(BuildCanonicalPlan(transaction.Participants, postedAssignments, selections), ParticipantJsonOptions);
        }
        else transaction.AllocationPlanJson = null;
        transaction.SplitMethod = model.SplitMethod; transaction.Status = TransactionStatus.Unpaid;
        transaction.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var participant in transaction.Participants.Where(x => x.AccountLink is not null))
            db.UserNotifications.Add(new UserNotification
            {
                UserId = participant.AccountLink!.UserId, Participant = participant,
                Type = NotificationType.BillAssigned, IsRead = false, CreatedAt = DateTimeOffset.UtcNow
            });
        if (sharePointNotifications is not null)
            await sharePointNotifications.EnqueueBillAssignmentsAsync(transaction, userManager.GetUserId(User)!, HttpContext.RequestAborted);
        await db.SaveChangesAsync();
        if (guestAccessService is not null)
            await guestAccessService.EnsureLinksAsync(transaction, userManager.GetUserId(User)!, HttpContext.RequestAborted);
        if (sharePointNotifications is not null && pickupOperation?.Assignment is not null && pickupOperation.History is not null)
            await sharePointNotifications.EnqueueFoodPickupSelectedAsync(transaction, pickupOperation.Assignment, pickupOperation.History, userManager.GetUserId(User)!, HttpContext.RequestAborted);
        await db.SaveChangesAsync();
        await splitTransaction.CommitAsync(HttpContext.RequestAborted);
        TempData["Success"] = Text("SplitSaved", "Pembagian berhasil disimpan.");
        return RedirectToAction(nameof(Details), new { id = transaction.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PickupPreview(string? participantUserIds, long? transactionId, CancellationToken cancellationToken)
    {
        if (transactionId.HasValue && !await ManageableTransaction(transactionId.Value).AnyAsync(cancellationToken))
            return NotFound();
        List<string> ids;
        try
        {
            ids = string.IsNullOrWhiteSpace(participantUserIds)
                ? []
                : JsonSerializer.Deserialize<List<string>>(participantUserIds) ?? [];
        }
        catch (JsonException)
        {
            return BadRequest(new { message = Text("InvalidParticipantData", "Data peserta tidak valid.") });
        }
        var preview = pickupRotationService is null
            ? new FoodPickupPreview([])
            : await pickupRotationService.PreviewAsync(ids, transactionId, cancellationToken);
        // Kept for backwards-compatible clients, but deliberately returns only
        // a coarse availability signal. Candidate names, counts and odds are
        // never exposed from Step 3 anymore.
        return Json(new { enabled = preview.HasCandidates });
    }

    private async Task<SplitTransactionViewModel> ToSplitViewModelAsync(BillTransaction transaction)
    {
        var model = new SplitTransactionViewModel
        {
            TransactionId = transaction.Id,
            MerchantName = transaction.MerchantName,
            GrandTotal = transaction.GrandTotal,
            SplitMethod = transaction.SplitMethod,
            RequiresFoodPickup = transaction.RequiresFoodPickup,
            ParticipantNames = string.Join(Environment.NewLine, transaction.Participants.Select(x => x.Name)),
            ParticipantsJson = JsonSerializer.Serialize(transaction.Participants.Select((participant, index) => new ParticipantSelectionViewModel
            {
                ClientKey = participant.AccountLink is null ? $"guest:{participant.Id}" : $"user:{participant.AccountLink.UserId}",
                UserId = participant.AccountLink?.UserId,
                Name = participant.Name
            }), ParticipantJsonOptions),
            Items = transaction.Items.OrderBy(x => x.LineNumber).ToList(),
            AssignmentsJson = BuildAssignmentsJson(transaction)
        };
        model.PickupRotationEnabled = await FoodPickupRotationEnabledAsync();
        model.AvailableUsers = await AvailableParticipantUsersAsync();
        return model;
    }

    private async Task PopulateSplitViewModelAsync(SplitTransactionViewModel model, BillTransaction transaction)
    {
        model.MerchantName = transaction.MerchantName;
        model.GrandTotal = transaction.GrandTotal;
        model.RequiresFoodPickup = transaction.RequiresFoodPickup;
        model.PickupRotationEnabled = await FoodPickupRotationEnabledAsync();
        model.Items = transaction.Items.OrderBy(x => x.LineNumber).ToList();
        model.AvailableUsers = await AvailableParticipantUsersAsync();
    }

    private async Task<List<ApplicationUser>> AvailableParticipantUsersAsync()
    {
        // SQLite cannot translate DateTimeOffset comparisons reliably. Load the
        // small account list first, then apply the lockout check in memory.
        var users = await userManager.Users.AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.UserName)
            .ToListAsync();
        var now = DateTimeOffset.UtcNow;
        return users.Where(x => x.LockoutEnd is null || x.LockoutEnd < now).ToList();
    }

    private Task<bool> FoodPickupRotationEnabledAsync()
        => db.FoodPickupConfigurations.AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => x.Enabled)
            .SingleOrDefaultAsync();

    private async Task<List<ParticipantSelectionViewModel>> ParseParticipantSelectionsAsync(SplitTransactionViewModel model)
    {
        List<ParticipantSelectionViewModel>? selections = null;
        if (!string.IsNullOrWhiteSpace(model.ParticipantsJson))
        {
            try { selections = JsonSerializer.Deserialize<List<ParticipantSelectionViewModel>>(model.ParticipantsJson, ParticipantJsonOptions); }
            catch (JsonException) { ModelState.AddModelError(nameof(model.ParticipantsJson), Text("InvalidParticipantData", "Data peserta tidak valid.")); }
        }
        if (selections is null || selections.Count == 0)
        {
            selections = model.ParticipantNames.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((name, index) => new ParticipantSelectionViewModel { ClientKey = $"guest:legacy-{index}", Name = name }).ToList();
        }

        var users = await AvailableParticipantUsersAsync();
        var userById = users.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var result = new List<ParticipantSelectionViewModel>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            if (selection is null) continue;
            var userId = string.IsNullOrWhiteSpace(selection.UserId) ? null : selection.UserId.Trim();
            if (userId is not null)
            {
                if (!userById.TryGetValue(userId, out var user))
                {
                    ModelState.AddModelError(nameof(model.ParticipantsJson), Text("InvalidParticipantAccount", "Ada akun peserta yang tidak valid."));
                    continue;
                }
                selection.Name = user.DisplayName;
                selection.ClientKey = $"user:{user.Id}";
            }
            else
            {
                selection.Name = selection.Name?.Trim() ?? string.Empty;
                if (selection.Name.Length == 0) continue;
                if (!selection.ClientKey.StartsWith("guest:", StringComparison.Ordinal)) selection.ClientKey = $"guest:{Guid.NewGuid():N}";
            }
            if (!keys.Add(selection.ClientKey) || !names.Add(selection.Name))
            {
                ModelState.AddModelError(nameof(model.ParticipantsJson), Text("DuplicateParticipants", "Peserta tidak boleh duplikat."));
                continue;
            }
            result.Add(selection);
        }
        model.ParticipantsJson = JsonSerializer.Serialize(result, ParticipantJsonOptions);
        model.ParticipantNames = string.Join(Environment.NewLine, result.Select(x => x.Name));
        return result;
    }

    private sealed record PostedGroup(int Quantity, List<string> ParticipantKeys);
    private sealed record PostedItemAssignment(long ItemId, List<PostedGroup> Groups);
    private sealed class AssignmentPayload { public List<GroupPayload> Groups { get; set; } = []; }
    private sealed class GroupPayload { public int Quantity { get; set; } public List<string> Participants { get; set; } = []; }

    private static List<PostedItemAssignment> ParseAssignments(string json, IReadOnlyList<TransactionItem> items)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var result = new List<PostedItemAssignment>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!long.TryParse(property.Name, out var itemId))
                throw new JsonException("Penugasan item tidak valid.");
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var keys = property.Value.EnumerateArray().Select(value =>
                    value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var legacyIndex) ? $"legacy-index:{legacyIndex}" : throw new JsonException("Peserta item tidak valid.")).ToList();
                var item = items.SingleOrDefault(x => x.Id == itemId) ?? throw new JsonException("Penugasan item tidak dikenal.");
                if (item.Quantity <= 0 || item.Quantity != decimal.Truncate(item.Quantity)) throw new JsonException("Quantity item tidak valid.");
                result.Add(new PostedItemAssignment(itemId, [new PostedGroup(checked((int)item.Quantity), keys)]));
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Object) throw new JsonException("Penugasan item tidak valid.");
            var payload = property.Value.Deserialize<AssignmentPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
            if (payload is null || payload.Groups.Count == 0) throw new JsonException("Kelompok penugasan item tidak valid.");
            result.Add(new PostedItemAssignment(itemId, payload.Groups.Select(group => new PostedGroup(group.Quantity, group.Participants ?? [])).ToList()));
        }
        return result;
    }

    private static Dictionary<string, object> BuildCanonicalPlan(IReadOnlyList<TransactionParticipant> participants, IReadOnlyList<PostedItemAssignment> assignments, IReadOnlyList<ParticipantSelectionViewModel> selections)
    {
        var selectionToId = selections.Select((selection, index) => (selection.ClientKey, participants[index].Id))
            .ToDictionary(x => x.ClientKey, x => x.Id, StringComparer.Ordinal);
        return assignments.ToDictionary(assignment => assignment.ItemId.ToString(), assignment => (object)new
        {
            groups = assignment.Groups.Select(group => new
            {
                quantity = group.Quantity,
                participantIds = group.ParticipantKeys.Select(key => selectionToId.TryGetValue(key, out var id) ? id : 0).Where(id => id > 0).ToArray()
            }).ToArray()
        });
    }

    private static string BuildAssignmentsJson(BillTransaction transaction)
    {
        if (!string.IsNullOrWhiteSpace(transaction.AllocationPlanJson))
        {
            try
            {
                using var document = JsonDocument.Parse(transaction.AllocationPlanJson);
                var participantKeys = transaction.Participants.ToDictionary(x => x.Id, x => x.AccountLink is null ? $"guest:{x.Id}" : $"user:{x.AccountLink.UserId}");
                var result = new Dictionary<string, object>();
                foreach (var item in document.RootElement.EnumerateObject())
                {
                    var groups = item.Value.GetProperty("groups").EnumerateArray().Select(group => (object)new
                    {
                        quantity = group.GetProperty("quantity").GetInt32(),
                        participants = group.GetProperty("participantIds").EnumerateArray().Select(id => id.TryGetInt64(out var value) && participantKeys.TryGetValue(value, out var key) ? key : string.Empty).Where(key => key.Length > 0).ToArray()
                    }).ToArray();
                    result[item.Name] = new { groups };
                }
                return JsonSerializer.Serialize(result, ParticipantJsonOptions);
            }
            catch (JsonException) { }
        }

        var fallback = transaction.Items.OrderBy(x => x.LineNumber).ToDictionary(
            item => item.Id.ToString(),
            item => (object)transaction.Participants.Where(participant => participant.ItemAllocations.Any(allocation => allocation.TransactionItemId == item.Id))
                .Select(participant => participant.AccountLink is null ? $"guest:{participant.Id}" : $"user:{participant.AccountLink.UserId}")
                .ToArray());
        return JsonSerializer.Serialize(fallback, ParticipantJsonOptions);
    }

    public async Task<IActionResult> Details(long id)
    {
        var transaction = await ScopedTransactions().AsNoTracking().Include(x => x.UploadedByUser).Include(x => x.Items).Include(x => x.Charges).Include(x => x.ReceiptImages)
            .Include(x => x.PickupAssignment!).ThenInclude(x => x.SelectedUser)
            .Include(x => x.PickupDrawHistories)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentHistories)
            .Include(x => x.Participants).ThenInclude(x => x.AccountLink)
            .Include(x => x.Participants).ThenInclude(x => x.ItemAllocations)
            .Include(x => x.Participants).ThenInclude(x => x.GuestAccessLinks)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals).SingleOrDefaultAsync(x => x.Id == id);
        if (transaction is null) return NotFound();
        var participantBreakdowns = calculator.BuildParticipantBreakdowns(transaction)
            .ToDictionary(x => x.ParticipantId);
        return View(new TransactionDetailsViewModel
        {
            Transaction = transaction, ReceiptImageCount = StoredReceiptImages(transaction).Count,
            PickupAssignment = transaction.PickupAssignment,
            PickupHistory = transaction.PickupDrawHistories.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToList(),
            Charges = ToChargeViewModels(transaction), ParticipantBreakdowns = participantBreakdowns
        });
    }

    [HttpGet]
    public async Task<IActionResult> ShareData(long id, CancellationToken cancellationToken)
    {
        var currentUserId = userManager.GetUserId(User);
        var owner = await db.Transactions.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.UploadedByUserId, x.Status, ParticipantCount = x.Participants.Count })
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is null) return NotFound();
        if (!string.Equals(owner.UploadedByUserId, currentUserId, StringComparison.Ordinal))
            return User.IsInRole(DatabaseSeeder.AdminRole) ? Forbid() : NotFound();
        if (owner.Status == TransactionStatus.Draft || owner.ParticipantCount == 0)
            return BadRequest(new { message = Text("ShareUnavailable", "Simpan split dengan minimal satu peserta terlebih dahulu.") });

        var transaction = await db.Transactions.AsSplitQuery()
            .Where(x => x.Id == id && x.UploadedByUserId == currentUserId)
            .Include(x => x.Items)
            .Include(x => x.Charges)
            .Include(x => x.Participants).ThenInclude(x => x.ItemAllocations).ThenInclude(x => x.Item)
            .Include(x => x.PickupAssignment)
            .SingleOrDefaultAsync(cancellationToken);
        if (transaction is null) return NotFound();
        var breakdowns = calculator.BuildParticipantBreakdowns(transaction);
        var exporter = shareExportService ?? throw new InvalidOperationException("Share exporter is not configured.");
        var projection = exporter.Build(transaction, breakdowns);
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        return Json(projection);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RerollPickup(long id, string? reason, CancellationToken cancellationToken)
    {
        if (!User.IsInRole(DatabaseSeeder.AdminRole)) return Forbid();
        var transaction = await ManageableTransaction(id)
            .Include(x => x.Participants).ThenInclude(x => x.AccountLink)
            .SingleOrDefaultAsync(cancellationToken);
        if (transaction is null) return NotFound();
        if (transaction.Status == TransactionStatus.Draft)
        {
            TempData["Error"] = Text("FoodPickupRerollUnavailable", "Pickup person belum tersedia sebelum split disimpan.");
            return RedirectToAction(nameof(Details), new { id });
        }
        if (pickupRotationService is null)
        {
            TempData["Error"] = Text("FoodPickupRerollUnavailable", "Rotasi pengambilan belum tersedia.");
            return RedirectToAction(nameof(Details), new { id });
        }
        try
        {
            await using var rerollTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await pickupRotationService.RerollAsync(transaction, userManager.GetUserId(User)!, reason ?? string.Empty, cancellationToken);
            if (result is null)
            {
                TempData["Error"] = Text("FoodPickupRerollUnavailable", "Tidak ada kandidat lain untuk dipilih ulang.");
                return RedirectToAction(nameof(Details), new { id });
            }
            await db.SaveChangesAsync(cancellationToken);
            if (result.Assignment is not null && result.History is not null && sharePointNotifications is not null)
                await sharePointNotifications.EnqueueFoodPickupSelectedAsync(transaction, result.Assignment, result.History, userManager.GetUserId(User)!, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await rerollTransaction.CommitAsync(cancellationToken);
            TempData["Success"] = Text("FoodPickupRerolled", "Pickup person berhasil diundi ulang.");
        }
        catch (ArgumentException)
        {
            TempData["Error"] = Text("FoodPickupRerollReasonRequired", "Isi alasan undian ulang terlebih dahulu.");
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(long id)
    {
        var result = await (paymentWorkflowService ?? new PaymentWorkflowService(db)).MarkPaidAsync(id, userManager.GetUserId(User)!, User.IsInRole(DatabaseSeeder.AdminRole));
        if (!result.Succeeded && result.TransactionId is null) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = LocalizeResult(result);
        return RedirectToAction(nameof(Details), new { id = result.TransactionId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmPayment(long id)
    {
        var result = await (paymentWorkflowService ?? new PaymentWorkflowService(db)).ConfirmAsync(id, userManager.GetUserId(User)!, User.IsInRole(DatabaseSeeder.AdminRole));
        if (!result.Succeeded && result.TransactionId is null) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = LocalizeResult(result);
        return RedirectToAction(nameof(Details), new { id = result.TransactionId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectPayment(long id, string? note)
    {
        var result = await (paymentWorkflowService ?? new PaymentWorkflowService(db)).RejectAsync(id, userManager.GetUserId(User)!, User.IsInRole(DatabaseSeeder.AdminRole), note);
        if (!result.Succeeded && result.TransactionId is null) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = LocalizeResult(result);
        return RedirectToAction(nameof(Details), new { id = result.TransactionId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReopenPayment(long id, string? note)
    {
        var result = await (paymentWorkflowService ?? new PaymentWorkflowService(db)).ReopenAsync(id, userManager.GetUserId(User)!, User.IsInRole(DatabaseSeeder.AdminRole), note);
        if (!result.Succeeded && result.TransactionId is null) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = LocalizeResult(result);
        return RedirectToAction(nameof(Details), new { id = result.TransactionId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reprocess(long id, CancellationToken cancellationToken)
    {
        var transaction = await ManageableTransaction(id).Include(x => x.ReceiptImages).Include(x => x.Items).Include(x => x.Charges)
            .SingleOrDefaultAsync(cancellationToken);
        if (transaction is null) return NotFound();
        if (transaction.Status != TransactionStatus.Draft || transaction.GrandTotal > 0)
        {
            TempData["Error"] = Text("ReprocessDraftOnly", "Proses ulang hanya tersedia untuk Draft dengan grand total masih 0.");
            return RedirectToAction(nameof(Review), new { id });
        }

        var storedImages = StoredReceiptImages(transaction)
            .Select(image => new
            {
                Path = Path.Combine(environment.ContentRootPath, "App_Data", "receipts", Path.GetFileName(image.FileName)),
                ContentType = string.IsNullOrWhiteSpace(image.ContentType) ? ContentTypeFor(image.FileName) : image.ContentType
            })
            .Where(image => System.IO.File.Exists(image.Path)).ToList();
        if (storedImages.Count == 0)
        {
            TempData["Error"] = Text("ReceiptMissingRetry", "Foto struk tidak ditemukan sehingga belum dapat diproses ulang.");
            return RedirectToAction(nameof(Review), new { id });
        }

        var now = DateTimeOffset.UtcNow;
        var activeAi = await db.AiConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive, cancellationToken);
        var log = new ReceiptProcessingLog
        {
            TransactionId = transaction.Id, Provider = activeAi?.Provider ?? AiProvider.OpenAi,
            Model = activeAi?.Model ?? string.Empty, ApiMode = activeAi?.ApiMode ?? AiApiMode.Responses,
            Status = AiProcessingStatus.Processing, StartedAt = now
        };
        db.ReceiptProcessingLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken);

        var inputs = storedImages.Select(image =>
            new ReceiptImageInput(System.IO.File.OpenRead(image.Path), image.ContentType)).ToList();
        try
        {
            var (result, rawResponse) = await aiReceiptService.AnalyzeAsync(inputs, cancellationToken);
            db.TransactionItems.RemoveRange(transaction.Items);
            db.TransactionCharges.RemoveRange(transaction.Charges);
            transaction.Items = [];
            transaction.Charges = [];
            ApplyAiResult(transaction, result, rawResponse);
            NormalizeCurrency(transaction);
            log.Status = AiProcessingStatus.Succeeded;
            log.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = Text("ReprocessSuccess", $"{storedImages.Count} foto struk berhasil diproses ulang. Periksa kembali hasil AI.", storedImages.Count);
        }
        catch (Exception exception) when (exception is AiServiceException or JsonException)
        {
            log.Status = AiProcessingStatus.Failed;
            log.ErrorMessage = exception.Message;
            log.FinishedAt = DateTimeOffset.UtcNow;
            transaction.AiWarningsJson = JsonSerializer.Serialize(new[] { exception.Message });
            await db.SaveChangesAsync(cancellationToken);
            TempData["Error"] = Text("AiRetryFailed", $"AI masih belum berhasil: {exception.Message} Data manual tetap aman.", exception.Message);
        }
        finally
        {
            foreach (var input in inputs) await input.Stream.DisposeAsync();
        }
        return RedirectToAction(nameof(Review), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGuestLink(long transactionId, long participantId, CancellationToken cancellationToken)
    {
        var transaction = await ManageableTransaction(transactionId).Include(x => x.Participants).ThenInclude(x => x.GuestAccessLinks).SingleOrDefaultAsync(cancellationToken);
        var participant = transaction?.Participants.SingleOrDefault(x => x.Id == participantId && x.AccountLink == null);
        if (transaction is null || participant is null) return NotFound();
        if (guestAccessService is null) return BadRequest();
        await guestAccessService.EnsureLinksAsync(transaction, userManager.GetUserId(User)!, cancellationToken);
        TempData["Success"] = Text("GuestLinkCreated", "Guest link siap digunakan.");
        return RedirectToAction(nameof(Details), new { id = transactionId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CopyGuestLink(long transactionId, long participantId, CancellationToken cancellationToken)
    {
        var transaction = await ManageableTransaction(transactionId).Include(x => x.Participants).SingleOrDefaultAsync(cancellationToken);
        var participant = transaction?.Participants.SingleOrDefault(x => x.Id == participantId && x.AccountLink == null);
        if (transaction is null || participant is null || guestAccessService is null) return NotFound();
        await guestAccessService.EnsureLinksAsync(transaction, userManager.GetUserId(User)!, cancellationToken);
        var token = await guestAccessService.GetCopyTokenAsync(participantId, cancellationToken);
        if (token is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        return Json(new { url = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/g/my-bill/{Uri.EscapeDataString(token)}" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateGuestLink(long transactionId, long participantId, CancellationToken cancellationToken)
    {
        var transaction = await ManageableTransaction(transactionId).AnyAsync(cancellationToken);
        if (!transaction || guestAccessService is null) return NotFound();
        var token = await guestAccessService.RegenerateAsync(transactionId, participantId, userManager.GetUserId(User)!, cancellationToken);
        if (token is null) return NotFound();
        TempData["Success"] = Text("GuestLinkRegenerated", "Guest link berhasil dibuat ulang.");
        return RedirectToAction(nameof(Details), new { id = transactionId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeGuestLink(long transactionId, long participantId, CancellationToken cancellationToken)
    {
        if (guestAccessService is null || !await ManageableTransaction(transactionId).AnyAsync(cancellationToken)) return NotFound();
        await guestAccessService.RevokeAsync(transactionId, participantId, cancellationToken);
        TempData["Success"] = Text("GuestLinkRevoked", "Guest link berhasil dicabut.");
        return RedirectToAction(nameof(Details), new { id = transactionId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id)
    {
        List<string> receiptFileNames;
        List<string> proofFileNames;
        string transactionNumber;
        // Keep the status check and deletion in one transaction so a concurrent split cannot be deleted.
        await using (var deletion = await db.Database.BeginTransactionAsync())
        {
            var transaction = await ManageableTransaction(id).Include(x => x.ProcessingLogs).Include(x => x.ReceiptImages)
                .Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals).SingleOrDefaultAsync();
            if (transaction is null) return NotFound();
            var isAdmin = User.IsInRole(DatabaseSeeder.AdminRole);
            if (transaction.Status == TransactionStatus.Unpaid && !isAdmin)
            {
                TempData["Error"] = Text("AdminOnlyUnpaidDelete", "Hanya Admin yang dapat menghapus transaksi Unpaid.");
                return RedirectToAction(nameof(Details), new { id });
            }
            if (transaction.Status is not (TransactionStatus.Draft or TransactionStatus.Unpaid))
            {
                TempData["Error"] = Text("DraftOnlyDelete", "Hanya transaksi Draft atau Unpaid yang dapat dihapus.");
                return RedirectToAction(nameof(Details), new { id });
            }

            receiptFileNames = StoredReceiptImages(transaction).Select(image => Path.GetFileName(image.FileName)).Distinct().ToList();
            proofFileNames = transaction.Participants.SelectMany(x => x.PaymentApprovals).Select(x => x.ProofFileName)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.Ordinal).ToList();
            transactionNumber = transaction.TransactionNumber;
            db.ReceiptProcessingLogs.RemoveRange(transaction.ProcessingLogs);
            db.Transactions.Remove(transaction);
            await db.SaveChangesAsync();
            await deletion.CommitAsync();
        }

        var fileCleanupFailed = false;
        foreach (var receiptFileName in receiptFileNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var receiptPath = Path.Combine(environment.ContentRootPath, "App_Data", "receipts", receiptFileName);
            try
            {
                System.IO.File.Delete(receiptPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                fileCleanupFailed = true;
            }
        }

        foreach (var proofFileName in proofFileNames)
        {
            try
            {
                if (proofStorage is not null) proofStorage.Delete(proofFileName);
                else
                {
                    var safeName = Path.GetFileName(proofFileName);
                    if (string.Equals(safeName, proofFileName, StringComparison.Ordinal)) System.IO.File.Delete(Path.Combine(environment.ContentRootPath, "App_Data", "payment-proofs", safeName));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { fileCleanupFailed = true; }
        }

        if (fileCleanupFailed)
        {
            TempData["Error"] = Text("DraftFileCleanupFailed", "Transaksi terhapus, tetapi satu atau beberapa file tidak dapat dibersihkan dari server.");
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = Text("DraftDeleted", $"Transaksi {transactionNumber} berhasil dihapus.", transactionNumber);
        return RedirectToAction(nameof(Index));
    }

    private IQueryable<BillTransaction> ScopedTransactions()
    {
        var query = db.Transactions.AsQueryable();
        if (!User.IsInRole(DatabaseSeeder.AdminRole))
        {
            var userId = userManager.GetUserId(User);
            query = query.Where(x => x.UploadedByUserId == userId);
        }
        return query;
    }

    private IQueryable<BillTransaction> ManageableTransaction(long id)
    {
        var query = db.Transactions.Where(x => x.Id == id);
        if (User.IsInRole(DatabaseSeeder.AdminRole)) return query;
        var userId = userManager.GetUserId(User);
        return query.Where(x => x.UploadedByUserId == userId);
    }

    private static void ApplyAiResult(BillTransaction transaction, ReceiptAiResult result, string rawResponse)
    {
        transaction.MerchantName = string.IsNullOrWhiteSpace(result.MerchantName) ? "Merchant tidak terbaca" : result.MerchantName.Trim();
        transaction.TransactionDate = DateOnly.TryParse(result.TransactionDate, out var date) ? date : null;
        transaction.Subtotal = result.Subtotal; transaction.Discount = result.Discount; transaction.Tax = result.Tax;
        transaction.ServiceCharge = result.ServiceCharge; transaction.GrandTotal = result.GrandTotal;
        if (!string.IsNullOrWhiteSpace(result.Currency)) transaction.CurrencyCode = result.Currency.Trim().ToUpperInvariant();
        transaction.AiConfidence = result.Confidence; transaction.AiNeedsReview = result.NeedsReview;
        transaction.AiWarningsJson = JsonSerializer.Serialize(result.Warnings); transaction.AiRawResponseJson = rawResponse;
        transaction.UpdatedAt = DateTimeOffset.UtcNow;
        transaction.Items = result.Items.Select((item, index) => new TransactionItem
        {
            LineNumber = item.LineNumber > 0 ? item.LineNumber : index + 1, Name = item.Name, Quantity = Math.Max(1, decimal.Truncate(item.Quantity)),
            UnitPrice = item.UnitPrice, TotalPrice = item.TotalPrice, Confidence = item.Confidence
        }).ToList();
        transaction.Charges = result.Charges.Select((charge, index) => new TransactionCharge
        {
            Label = string.IsNullOrWhiteSpace(charge.Label) ? $"Biaya lainnya {index + 1}" : charge.Label.Trim(),
            Amount = Math.Max(0, charge.Amount),
            Operation = charge.Operation.Equals("subtract", StringComparison.OrdinalIgnoreCase)
                ? ChargeOperation.Subtract : ChargeOperation.Add,
            SortOrder = index
        }).Where(charge => charge.Amount > 0).ToList();
        if (transaction.Charges.Count == 0)
        {
            if (result.Discount > 0) transaction.Charges.Add(new TransactionCharge { Label = "Diskon", Amount = result.Discount, Operation = ChargeOperation.Subtract, SortOrder = 0 });
            if (result.Tax > 0) transaction.Charges.Add(new TransactionCharge { Label = "Pajak", Amount = result.Tax, Operation = ChargeOperation.Add, SortOrder = transaction.Charges.Count });
            if (result.ServiceCharge > 0) transaction.Charges.Add(new TransactionCharge { Label = "Service charge", Amount = result.ServiceCharge, Operation = ChargeOperation.Add, SortOrder = transaction.Charges.Count });
        }
        transaction.Discount = transaction.Charges.Where(x => x.Operation == ChargeOperation.Subtract).Sum(x => x.Amount);
        transaction.Tax = transaction.Charges.Where(x => x.Operation == ChargeOperation.Add && ReceiptReviewNormalizer.IsTaxLabel(x.Label)).Sum(x => x.Amount);
        transaction.ServiceCharge = transaction.Charges.Where(x => x.Operation == ChargeOperation.Add && !ReceiptReviewNormalizer.IsTaxLabel(x.Label)).Sum(x => x.Amount);
    }

    private void NormalizeCurrency(BillTransaction transaction)
    {
        var code = transaction.CurrencyCode?.Trim().ToUpperInvariant();
        if (currencyCatalog is null || !currencyCatalog.TryGet(code, out _))
        {
            transaction.CurrencyCode = "IDR";
            var warnings = string.IsNullOrWhiteSpace(transaction.AiWarningsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(transaction.AiWarningsJson) ?? [];
            warnings.Add("Mata uang struk tidak dikenali; pilih mata uang yang sesuai saat review.");
            transaction.AiWarningsJson = JsonSerializer.Serialize(warnings.Distinct());
        }
        else transaction.CurrencyCode = code!;
    }

    private static ReviewTransactionViewModel ToReviewViewModel(BillTransaction transaction) => new()
    {
        Id = transaction.Id, TransactionNumber = transaction.TransactionNumber, ReceiptImagePath = transaction.ReceiptImagePath,
        ReceiptImageCount = StoredReceiptImages(transaction).Count,
        MerchantName = transaction.MerchantName, TransactionDate = transaction.TransactionDate?.ToDateTime(TimeOnly.MinValue),
        Subtotal = transaction.Subtotal, Discount = transaction.Discount, Tax = transaction.Tax,
        ServiceCharge = transaction.ServiceCharge, GrandTotal = transaction.GrandTotal,
        CurrencyCode = string.IsNullOrWhiteSpace(transaction.CurrencyCode) ? "IDR" : transaction.CurrencyCode,
        ReportingCurrencyCode = string.IsNullOrWhiteSpace(transaction.ReportingCurrencyCode) ? "IDR" : transaction.ReportingCurrencyCode,
        ExchangeRateToReporting = transaction.ExchangeRateToReporting <= 0 ? 1m : transaction.ExchangeRateToReporting,
        ExchangeRateEffectiveDate = transaction.ExchangeRateEffectiveDate,
        ExchangeRateSource = transaction.ExchangeRateSource,
        ExchangeRateCaptureMode = transaction.ExchangeRateCaptureMode,
        ExchangeRateManualNote = transaction.ExchangeRateManualNote,
        AiConfidence = transaction.AiConfidence, AiNeedsReview = transaction.AiNeedsReview,
        Warnings = string.IsNullOrWhiteSpace(transaction.AiWarningsJson) ? [] : JsonSerializer.Deserialize<List<string>>(transaction.AiWarningsJson) ?? [],
        Items = transaction.Items.OrderBy(x => x.LineNumber).Select(x => new ReviewItemViewModel
        {
            Id = x.Id, LineNumber = x.LineNumber, Name = x.Name,
            Quantity = x.Quantity > int.MaxValue ? int.MaxValue : (int)Math.Max(1, decimal.Truncate(x.Quantity)),
            UnitPrice = x.UnitPrice, TotalPrice = x.TotalPrice
        }).ToList(),
        Charges = ToChargeViewModels(transaction)
    };

    private static List<TransactionReceiptImage> StoredReceiptImages(BillTransaction transaction)
    {
        if (transaction.ReceiptImages.Count > 0)
            return transaction.ReceiptImages.OrderBy(image => image.SortOrder).ThenBy(image => image.Id).ToList();
        if (string.IsNullOrWhiteSpace(transaction.ReceiptImagePath)) return [];
        return [new TransactionReceiptImage
        {
            FileName = transaction.ReceiptImagePath,
            ContentType = ContentTypeFor(transaction.ReceiptImagePath), SortOrder = 0
        }];
    }

    private static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg"
    };

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg"
    };

    private string LocalizeResult(PaymentWorkflowResult result) => result.MessageKey is { } key && localizer is not null ? localizer[key].Value : result.Message;
    private string Text(string key, string fallback, params object[] args) => localizer is null ? string.Format(fallback, args) : localizer[key, args].Value;

    private static List<ReviewChargeViewModel> ToChargeViewModels(BillTransaction transaction) =>
        (transaction.Charges.Count > 0
            ? transaction.Charges.OrderBy(x => x.SortOrder).Select(x => new ReviewChargeViewModel
            {
                Id = x.Id, Label = x.Label, Amount = x.Amount, Operation = x.Operation
            })
            : LegacyCharges(transaction)).ToList();

    private static IEnumerable<ReviewChargeViewModel> LegacyCharges(BillTransaction transaction)
    {
        if (transaction.Discount > 0) yield return new ReviewChargeViewModel { Label = "Diskon", Amount = transaction.Discount, Operation = ChargeOperation.Subtract };
        if (transaction.Tax > 0) yield return new ReviewChargeViewModel { Label = "Pajak", Amount = transaction.Tax, Operation = ChargeOperation.Add };
        if (transaction.ServiceCharge > 0) yield return new ReviewChargeViewModel { Label = "Service charge", Amount = transaction.ServiceCharge, Operation = ChargeOperation.Add };
    }
}
