using System;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Tando.Services;

public enum TandoPullPaymentStatus { NotApplicable, Created, Claimed, Failed }

public record TandoSplitRecord
{
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "";
    public decimal BtcPortionAmount { get; set; }
    public decimal MpesaPortionAmount { get; set; }
    public decimal MpesaPercentage { get; set; }
    public TandoMpesaDestinationType? MpesaDestinationType { get; set; }
    public string? MpesaDestination { get; set; }
    public bool MpesaSettled { get; set; }
    public DateTimeOffset? MpesaSettledAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? PullPaymentId { get; set; }
    public TandoPullPaymentStatus PullPaymentStatus { get; set; } = TandoPullPaymentStatus.NotApplicable;
    public string? PullPaymentError { get; set; }
}


public class TandoSplitService(InvoiceRepository invoiceRepository, TandoSubscriptionService subscriptionService,
    StoreRepository storeRepository, TandoMerchantSettingsService merchantSettingsService, PullPaymentHostedService pullPaymentHostedService)
{
    private const string SplitMetadataKey = "tandoSplit";

    public async Task<(TandoSplitRecord? Record, string? Error)> ComputeAndRecordSplit(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (null, "invoice_not_found");

        var existing = GetRecord(invoice);
        if (existing is not null)
            return (existing, null);

        var splitConfig = await merchantSettingsService.GetSplitConfig(storeId);
        var mpesaSettings = await merchantSettingsService.GetMpesaSettings(storeId);
        var mpesaPortion = Math.Round(invoice.Price * (splitConfig.MpesaPercentage / 100m), 2);
        var record = new TandoSplitRecord
        {
            TotalAmount = invoice.Price,
            Currency = invoice.Currency,
            MpesaPortionAmount = mpesaPortion,
            BtcPortionAmount = invoice.Price - mpesaPortion,
            MpesaPercentage = splitConfig.MpesaPercentage,
            MpesaDestinationType = mpesaSettings?.DestinationType,
            MpesaDestination = mpesaSettings?.Destination,
            MpesaSettled = mpesaPortion <= 0,
            PullPaymentStatus = mpesaPortion <= 0 ? TandoPullPaymentStatus.NotApplicable : TandoPullPaymentStatus.Created,
            RecordedAt = DateTimeOffset.UtcNow
        };
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        if (mpesaPortion > 0)
            record = await CreateAndClaimPayout(storeId, invoiceId, record);

        return (record, null);
    }

    private async Task<TandoSplitRecord> CreateAndClaimPayout(string storeId, string invoiceId, TandoSplitRecord record)
    {
        var settings = await subscriptionService.GetSettings();
        if (string.IsNullOrWhiteSpace(settings.TreasuryLightningAddress))
            return record;

        var store = await storeRepository.FindStore(storeId);
        if (store is null)
        {
            record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
            record.PullPaymentError = "store_not_found";
            await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
            return record;
        }
        try
        {
            var payoutMethodId = PayoutTypes.LN.GetPayoutMethodId("BTC");
            var pullPaymentId = await pullPaymentHostedService.CreatePullPayment(store, new CreatePullPaymentRequest
            {
                Name = $"Tando split payout - invoice {invoiceId}",
                Amount = record.MpesaPortionAmount,
                Currency = record.Currency,
                PayoutMethods = [payoutMethodId.ToString()],
                AutoApproveClaims = true
            });
            var claimResult = await pullPaymentHostedService.Claim(new ClaimRequest
            {
                Destination = new LNURLPayClaimDestinaton(settings.TreasuryLightningAddress),
                PullPaymentId = pullPaymentId,
                ClaimedAmount = record.MpesaPortionAmount,
                PayoutMethodId = payoutMethodId,
                StoreId = storeId
            });
            record.PullPaymentId = pullPaymentId;
            if (claimResult.Result == ClaimRequest.ClaimResult.Ok)
            {
                record.PullPaymentStatus = TandoPullPaymentStatus.Claimed;
                record.MpesaSettled = true;
                record.MpesaSettledAt = DateTimeOffset.UtcNow;
            }
            else
            {
                record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
                record.PullPaymentError = ClaimRequest.GetErrorMessage(claimResult.Result);
            }
        }
        catch (Exception ex)
        {
            record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
            record.PullPaymentError = ex.Message;
        }
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return record;
    }

    public async Task<TandoSplitRecord?> GetSplit(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return null;

        return GetRecord(invoice);
    }

    public async Task<(bool Success, string? Error)> MarkMpesaSettled(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (false, "invoice_not_found");

        var record = GetRecord(invoice);
        if (record is null)
            return (false, "split_not_recorded");
        if (record.MpesaSettled)
            return (true, null);

        record.MpesaSettled = true;
        record.MpesaSettledAt = DateTimeOffset.UtcNow;
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return (true, null);
    }

    private static TandoSplitRecord? GetRecord(InvoiceEntity invoice) => invoice.Metadata?.GetAdditionalData<TandoSplitRecord>(SplitMetadataKey);
}
