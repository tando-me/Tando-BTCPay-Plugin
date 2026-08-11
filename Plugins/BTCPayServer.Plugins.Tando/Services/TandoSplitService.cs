using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Tando.Services;

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
}

public class TandoSplitService(InvoiceRepository invoiceRepository, TandoMerchantSettingsService merchantSettingsService)
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
            MpesaSettled = mpesaPortion <= 0, // nothing to settle if the split routes 0% to M-Pesa
            RecordedAt = DateTimeOffset.UtcNow
        };

        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return (record, null);
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

    private static TandoSplitRecord? GetRecord(InvoiceEntity invoice)
        => invoice.Metadata?.GetAdditionalData<TandoSplitRecord>(SplitMetadataKey);
}
