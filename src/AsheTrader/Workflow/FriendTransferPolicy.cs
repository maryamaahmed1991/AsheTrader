using AsheTrader.Core.Store;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

/// <summary>Hosted recipients grant permission for receiving this request only, never sending their own stock.</summary>
public static class FriendTransferPolicy
{
    public static SavedTransferJob Build(FriendOrder order,SavedTransferJob template,IEnumerable<ulong> localCharacters)
    {
        if(!ulong.TryParse(order.Recipient.ContentId,out var recipient)||recipient==0||order.Items.Length is <1 or >50)
            throw new InvalidDataException("Invalid friend recipient or item list.");
        var local=localCharacters.ToHashSet();
        if(local.Contains(recipient))throw new InvalidDataException("The recipient must be on the other account.");
        var definitions=new List<TransferJobDefinition>();
        foreach(var line in order.Items)
        {
            var selected=template.ItemDefinitions.SingleOrDefault(d=>d.ItemId==line.ItemId&&d.HighQuality==line.HighQuality)
                ?? throw new InvalidDataException("The requested item is not in the supplying profile.");
            var sources=selected.Sources.Where(local.Contains).Distinct().ToList();
            if(sources.Count==0||line.Quantity==0||line.Quantity>1_000_000_000)throw new InvalidDataException("Invalid requested stock or supplying characters.");
            definitions.Add(new(){Id=definitions.Count==0?order.Id:Guid.NewGuid(),Name=$"Friend delivery to {order.Recipient.Name}",
                ItemId=line.ItemId,HighQuality=line.HighQuality,QuantityMode=JobQuantityMode.DeliverAmount,Sources=sources,
                Recipients=[new(){CharacterId=recipient,Quantity=line.Quantity}]});
        }
        if(definitions.Select(x=>(x.ItemId,x.HighQuality)).Distinct().Count()!=definitions.Count)throw new InvalidDataException("Duplicate requested item.");
        return new(){Definition=definitions[0],AdditionalItems=definitions.Skip(1).ToList(),Meeting=template.Meeting,EnableAutoRetainerAfterCompletion=true};
    }
    public static void ValidateRecipientCommand(FriendOrder order,WorkerCommand command,IEnumerable<WorkerRecord> records)
    {
        if(command.JobId!=order.Id || command.Operation.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture)!=order.Recipient.ContentId)
            throw new InvalidDataException("Friend delivery cannot control another job or character.");
        if(command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.PreviewBatch || command.Operation.UseSaddlebags
            || command.Kind==WorkerCommandKind.Game&&command.Operation.Kind is GameOperationKind.Withdraw or GameOperationKind.ArrangeSaddlebags)
            throw new InvalidDataException("Friend delivery only permits receiving and depositing requested items.");
        var line=order.Items.SingleOrDefault(x=>x.ItemId==command.Operation.ItemId&&x.HighQuality==command.Operation.HighQuality);
        if(command.Kind!=WorkerCommandKind.ReceiveBatch && line==null)throw new InvalidDataException("Item is outside the friend request.");
        if(command.Kind==WorkerCommandKind.ReceiveBatch)
        {
            foreach(var item in MultiItemTradeEvidence.Lines(command))
            {
                var limit=order.Items.SingleOrDefault(x=>x.ItemId==item.ItemId&&x.HighQuality==item.HighQuality)?.Quantity??0;
                var used=records.Where(r=>r.Command.JobId==order.Id&&r.Command.Id!=command.Id&&r.Command.Kind==WorkerCommandKind.ReceiveBatch
                    && r.Response?.NeverStarted!=true && r.Response?.Unchanged!=true)
                    .SelectMany(r=>MultiItemTradeEvidence.Lines(r.Command)).Where(x=>x.ItemId==item.ItemId&&x.HighQuality==item.HighQuality)
                    .Aggregate(0UL,(sum,x)=>checked(sum+x.Quantity));
                if(item.Quantity>limit || used>limit-item.Quantity)throw new InvalidDataException("Receive batch exceeds the requested quantity.");
            }
        }
        if(command.Kind==WorkerCommandKind.Game&&command.Operation.Kind==GameOperationKind.Deposit&&command.Operation.Quantity>line!.Quantity)
            throw new InvalidDataException("Deposit exceeds the friend request.");
        if(command.Kind==WorkerCommandKind.Game&&command.Operation.Kind==GameOperationKind.TravelMeeting && command.Operation.Destination is not {TerritoryId:>0,AetheryteId:>0})
            throw new InvalidDataException("Invalid friend meeting destination.");
    }
}
