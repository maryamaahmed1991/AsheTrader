using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Core.Store;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Jobs;

public class FriendTransferTests
{
    private static FriendOrder Order()=>new(Guid.NewGuid(),"source","reader",Guid.NewGuid(),Guid.NewGuid(),
        new("18014498570037335","Friend Alt","Faerie"),[new(10155,"Ceruleum Tank",false,100),new(5110,"Coke",false,30)],"active",DateTimeOffset.UtcNow,null);
    private static WorkerCommand Receive(FriendOrder order,uint count=40)=>new(){Id=Guid.NewGuid(),JobId=order.Id,Kind=WorkerCommandKind.ReceiveBatch,
        Operation=new(){CharacterId=ulong.Parse(order.Recipient.ContentId),ItemId=10155,Quantity=count}};
    [Fact]
    public void BuildsOneMultiItemJobWithOnlyLocalProvidersAndRequestedRecipient()
    {
        var order=Order();var template=new SavedTransferJob{Definition=new(){ItemId=10155,Sources=[1,2,3],Recipients=[new(){CharacterId=4}]},AdditionalItems=[new(){ItemId=5110,Sources=[3,2]}],Meeting=new(){TerritoryId=1,AetheryteId=2}};
        var job=FriendTransferPolicy.Build(order,template,[1,2]);
        Assert.Equal(order.Id,job.Definition.Id);Assert.Equal(2,job.ItemDefinitions.Count());Assert.True(job.EnableAutoRetainerAfterCompletion);
        Assert.Equal(new ulong[]{1,2},job.Definition.Sources);Assert.Equal(new ulong[]{2},job.AdditionalItems[0].Sources);
        Assert.All(job.ItemDefinitions,d=>{Assert.Equal(JobQuantityMode.DeliverAmount,d.QuantityMode);Assert.Equal(ulong.Parse(order.Recipient.ContentId),Assert.Single(d.Recipients).CharacterId);});
        Assert.Equal(100UL,job.Definition.Recipients[0].Quantity);Assert.Equal(30UL,job.AdditionalItems[0].Recipients[0].Quantity);
        Assert.Equal(new ulong[]{1,2,3},template.Definition.Sources);
    }
    [Fact]
    public void DoesNotAcceptUnlistedItemsOrProviderOnAnotherPc()
    {
        var order=Order();var template=new SavedTransferJob{Definition=new(){ItemId=10155,Sources=[1]}};
        Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.Build(order,template,[1]));
        template.AdditionalItems.Add(new(){ItemId=5110,Sources=[2]});
        Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.Build(order,template,[1]));
    }
    [Theory]
    [InlineData(WorkerCommandKind.SendBatch,GameOperationKind.Inspect)]
    [InlineData(WorkerCommandKind.PreviewBatch,GameOperationKind.Inspect)]
    [InlineData(WorkerCommandKind.Game,GameOperationKind.Withdraw)]
    [InlineData(WorkerCommandKind.Game,GameOperationKind.ArrangeSaddlebags)]
    public void FriendCannotSendOrWithdrawRecipientsStock(WorkerCommandKind kind,GameOperationKind operation)
    {
        var order=Order();var command=Receive(order);command.Kind=kind;command.Operation.Kind=operation;
        Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,command,[]));
    }
    [Fact]
    public void RejectsOtherCharactersJobsItemsAndQuality()
    {
        var order=Order();var command=Receive(order);command.JobId=Guid.NewGuid();Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,command,[]));
        command=Receive(order);command.Operation.CharacterId++;Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,command,[]));
        command=Receive(order);command.Operation.ItemId=1;Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,command,[]));
        command=Receive(order);command.Operation.HighQuality=true;Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,command,[]));
    }
    [Fact]
    public void CumulativeReceiveCapIncludesUncertainTradeButExcludesConfirmedNoMovement()
    {
        var order=Order();var old=Receive(order,60);var next=Receive(order,40);var record=new WorkerRecord{Command=old};
        FriendTransferPolicy.ValidateRecipientCommand(order,next,[record]);
        next.Operation.Quantity=41;Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,next,[record]));
        record.Response=new(){CommandId=old.Id,NeverStarted=true};FriendTransferPolicy.ValidateRecipientCommand(order,next,[record]);
        record.Response=new(){CommandId=old.Id,Success=true,Quantity=60};Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,next,[record]));
        FriendTransferPolicy.ValidateRecipientCommand(order,old,[record]); // Journal replay does not consume a second allowance.
    }
    [Fact]
    public void ValidatesEveryItemInMixedBatch()
    {
        var order=Order();var command=Receive(order);command.TradeItems=[new(){ItemId=10155,Quantity=100},new(){ItemId=5110,Quantity=30}];
        FriendTransferPolicy.ValidateRecipientCommand(order,command,[]);
        command.TradeItems[1].Quantity=31;Assert.Throws<InvalidDataException>(()=>FriendTransferPolicy.ValidateRecipientCommand(order,command,[]));
    }
    [Theory]
    [InlineData(WorkerCommandKind.PauseAutoRetainer,GameOperationKind.Inspect)]
    [InlineData(WorkerCommandKind.EnableAutoRetainer,GameOperationKind.Inspect)]
    [InlineData(WorkerCommandKind.Game,GameOperationKind.Login)]
    [InlineData(WorkerCommandKind.Game,GameOperationKind.Deposit)]
    public void PermitsScopedArLoginAndDeposit(WorkerCommandKind kind,GameOperationKind operation)
    {
        var order=Order();var command=Receive(order);command.Kind=kind;command.Operation.Kind=operation;
        FriendTransferPolicy.ValidateRecipientCommand(order,command,[]);
    }
}
