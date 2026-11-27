using AsheTrader.Core.Store;
using AsheTrader.Core.Networking;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private FriendOrder? friendOrder;
    private SavedTransferJob? friendTemplate;
    private bool friendPreviousHost;
    private bool friendPreviousSuppressed;
    private bool friendCompletionChecked;
    public bool FriendDeliveryConnected => friendOrder!=null;
    private bool RemoteControlAllowed => friendOrder!=null || Settings.AllowRemoteControl;
    public bool CanStartFriendDelivery => LocalReadyForJob && Run is not {Step:not CoordinatorStep.Finished}
        && !WaitingForPeer && !HasQueuedTransfer && !completionDeadline.HasValue && !plugin.CompletionHandoff.Pending
        && !workerRecords.Values.Any(r=>r.Command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch && (r.Response==null||r.Response.Ambiguous))
        && (!Connected || RemoteReadyForJob && !remoteHasUnfinishedJob);
    public bool FriendDeliveryFinished => friendOrder!=null && host && Run is {Step:CoordinatorStep.Finished}
        && Run.ScheduleOccurrenceId==friendOrder.Id && (!Run.CompletedNormally||friendCompletionChecked)
        && !completionDeadline.HasValue && !plugin.CompletionHandoff.Pending && activeWorker==null;
    public bool FriendWorkerIdle => activeWorker==null && !executor.IsBusy && !plugin.Trader.IsRunning && !plugin.CompletionHandoff.Pending;
    public void ConnectFriendDelivery(StoreAccess access,FriendOrder order,SavedTransferJob? template)
    {
        if(friendOrder!=null) {if(friendOrder.Id==order.Id)return;throw new InvalidOperationException("Finish the active friend delivery first.");}
        var supplying=access.Role=="publisher";
        if(order.State!="active" || (supplying?order.HostPeer:order.GuestPeer)!=Settings.PeerId)throw new InvalidDataException("This request belongs to another PC.");
        if(!CanStartFriendDelivery && !(LocalReadyForJob&&Run?.ScheduleOccurrenceId==order.Id&&Run.Paused&&(!Connected||RemoteReadyForJob&&!remoteHasUnfinishedJob)))
            throw new InvalidOperationException("Finish the current job before connecting the friend delivery.");
        friendPreviousHost=host;friendPreviousSuppressed=autoConnectionSuppressed;
        friendOrder=order;friendTemplate=template;host=supplying;friendCompletionChecked=false;
        scanner.Stop();advertiser.Stop();peer.Stop();autoConnectionSuppressed=true;
        CancelItemSearch();inventorySender=null;inventoryReceiver=null;
        selectedCoordinatorId=null;connectionId=null;acceptedPeerId=null;RemoteRoster=null;wasConnected=false;remoteReady=false;remoteHasUnfinishedJob=false;
        peer.StartHosted(access,order,supplying);error="";
    }
    public void StartFriendDelivery()
    {
        if(friendOrder==null||!host||!HasAcceptedConnection||RemoteRoster is not {ProtocolVersion:>=6}||!RemoteReadyForJob)return;
        if(Run?.ScheduleOccurrenceId==friendOrder.Id)
        {
            // Reattach the existing journal; a terminal receipt must not hide a lost AR handoff.
            if(Run.Step==CoordinatorStep.Finished&&!friendCompletionChecked)
            {
                friendCompletionChecked=true;
                if(Run.CompletedNormally)
                {
                    if(Run.CompletionCommands.Count==0)BeginCompletionHandoff();
                    else if(!completionDeadline.HasValue&&Run.CompletionCommands.Any(c=>c.Response==null))
                    {completionDeadline=Environment.TickCount64+60000;nextCompletionSend=0;}
                }
            }
            return;
        }
        var job=FriendTransferPolicy.Build(friendOrder,friendTemplate??throw new InvalidOperationException("Friend profile unavailable."),plugin.Config.Characters.Select(c=>c.ContentId));
        Start(job,false,friendOrder.Id,"Friend request",friendOrder.Id);
    }
    public void ReleaseFriendDelivery()
    {
        if(friendOrder==null)return;
        if(!FriendWorkerIdle||host&&Run is {Step:not CoordinatorStep.Finished})throw new InvalidOperationException("End and reconcile the delivery before disconnecting.");
        peer.Stop();friendOrder=null;friendTemplate=null;host=friendPreviousHost;autoConnectionSuppressed=friendPreviousSuppressed;
        trustedRun.Cancel();
        connectionId=null;acceptedPeerId=null;selectedCoordinatorId=null;wasConnected=false;RemoteRoster=savedRemoteRoster;remoteReady=false;
        nextConnectAt=nextDevicePoll=0;error="";
    }
    public void ReconnectFriendDelivery(StoreAccess access,FriendOrder order)
    {
        if(friendOrder==null||Connected)return;
        if(!FriendWorkerIdle)throw new InvalidOperationException("Wait for local delivery actions to stop before reconnecting.");
        peer.StartHosted(access,order,host);connectionId=null;acceptedPeerId=null;wasConnected=false;RemoteRoster=null;remoteReady=false;
    }
    private bool FriendCharacterAllowed(ulong id) => friendOrder==null || (host
        ? friendTemplate?.ItemDefinitions.Any(d=>d.Sources.Contains(id))==true
        : id.ToString(System.Globalization.CultureInfo.InvariantCulture)==friendOrder.Recipient.ContentId);
    partial void ValidateStartContext(Guid? friendRequest)
    {
        if(friendOrder!=null&&friendRequest!=friendOrder.Id)throw new InvalidOperationException("Finish the friend delivery before starting another transfer.");
    }
    partial void ValidateResumeContext()
    {
        if(Run?.FriendRequestId is {} request&&friendOrder?.Id!=request)
            throw new InvalidOperationException("Reconnect this request in Stores → Friend deliveries before resuming it.");
    }
    private void ValidateFriendMessage(PeerMessage message)
    {
        if(friendOrder==null)return;
        string[] allowed=host?["heartbeat","device-ready","roster","roster-request","response","trade-tally-result","trade-settle-result","pause"]
            :["heartbeat","device-ready","roster","roster-request","command","query","trade-tally","trade-settle","pause"];
        if(!allowed.Contains(message.Kind))throw new InvalidDataException("Message is outside the friend delivery.");
        if(!host&&message.Kind is "query" or "trade-tally" or "trade-settle")
        {
            var id=message.Kind=="query"?Decode<Guid>(message.Payload):message.Kind=="trade-tally"?Decode<TradeTallyRequest>(message.Payload).CommandId:Decode<AutomaticTradeResolution>(message.Payload).CommandId;
            if(workerRecords.TryGetValue(id,out var record)&&record.Command.JobId!=friendOrder.Id)throw new InvalidDataException("Cannot inspect another job through a friend connection.");
        }
    }
}
