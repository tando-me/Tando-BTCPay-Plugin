using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoInvoiceSettlementHostedService(EventAggregator eventAggregator, TandoSplitService splitService, TandoMerchantSettingsService merchantSettingsService, 
    ILogger<TandoInvoiceSettlementHostedService> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    protected override void SubscribeToEvents()
    {
        this.Subscribe<InvoiceEvent>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not InvoiceEvent
            {
                EventCode: InvoiceEventCode.Completed or InvoiceEventCode.MarkedCompleted or InvoiceEventCode.PaidInFull,
                Invoice: { Status: InvoiceStatus.Settled } invoice
            })
            return;

        var splitConfig = await merchantSettingsService.GetSplitConfig(invoice.StoreId);
        if (splitConfig.MpesaPercentage <= 0)
            return;

        await splitService.ComputeAndRecordSplit(invoice.StoreId, invoice.Id);
    }
}