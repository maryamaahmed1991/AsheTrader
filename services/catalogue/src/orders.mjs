// Explicit reader grants authorize bounded requests, never arbitrary control of the publisher.
export async function orders(request,env,path,credential,h) {
 const {reply,fail,body,label}=h, db=env.DB, now=new Date().toISOString();
 const uuid=x=>{if(typeof x!=='string'||!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(x)||x==='00000000-0000-0000-0000-000000000000')fail(400,'Invalid identity.');return x;};
 const lines=(value,empty=false)=>{
  if(!Array.isArray(value)||value.length<(empty?0:1)||value.length>50)fail(400,'Choose 1–50 items.');
  const seen=new Set();return value.map(x=>{if(!x||!Number.isInteger(x.itemId)||x.itemId<2||x.itemId>10000000||typeof x.highQuality!=='boolean'||!Number.isSafeInteger(x.quantity)||x.quantity<1||x.quantity>1000000000)fail(400,'Invalid item quantity.');
   const k=`${x.itemId}:${x.highQuality}`;if(seen.has(k))fail(400,'Duplicate item.');seen.add(k);return {itemId:x.itemId,highQuality:x.highQuality,quantity:x.quantity,name:label(x.name)};});
 };
 const owner=()=>{if(credential.role!=='owner')fail(403,'Owner access required.');};
 const publisher=()=>{if(credential.role!=='publisher')fail(403,'Publisher access required.');};
 const view=row=>({id:row.id,sourceId:row.source_id,readerId:row.reader_id,hostPeer:row.host_peer,guestPeer:row.guest_peer,recipient:JSON.parse(row.recipient),items:JSON.parse(row.items),state:row.state,createdAt:row.created_at,receipt:row.receipt,guestSeen:row.guest_seen});
 if(path==='/v1/transfers/grants') {
  owner();
  if(request.method==='GET')return reply((await db.prepare('SELECT reader_id AS readerId,source_id AS sourceId FROM transfer_grants').all()).results);
  if(request.method==='PUT'||request.method==='DELETE') {
   const x=await body(request),reader=uuid(x.readerId),source=uuid(x.sourceId);
   if(request.method==='DELETE') {
    await db.batch([db.prepare('DELETE FROM transfer_grants WHERE reader_id=? AND source_id=?').bind(reader,source),
     db.prepare("UPDATE transfer_orders SET state='cancelled',ended_at=? WHERE reader_id=? AND source_id=? AND state='queued'").bind(now,reader,source)]);
   } else {
    const r=await db.prepare("INSERT OR IGNORE INTO transfer_grants SELECT ?,? WHERE EXISTS(SELECT 1 FROM credentials WHERE id=? AND role='reader') AND EXISTS(SELECT 1 FROM sources WHERE id=?)").bind(reader,source,reader,source).run();
    if(!r.meta.changes && !await db.prepare('SELECT 1 FROM transfer_grants WHERE reader_id=? AND source_id=?').bind(reader,source).first())fail(400,'Choose a current reader and publisher.');
   }
   return reply({ok:true});
  }
 }
 if(path==='/v1/transfers/offer') {
  publisher();
  if(request.method==='GET')return reply(await db.prepare('SELECT revision,enabled,label,peer_id AS peerId,items,updated_at AS updatedAt FROM transfer_offers WHERE source_id=?').bind(credential.source_id).first()||{revision:0,enabled:0,label:'',peerId:'00000000-0000-0000-0000-000000000000',items:'[]'});
  if(request.method==='PUT') {
   const x=await body(request),items=lines(x.items,true),peer=uuid(x.peerId),name=label(x.label);
   if(!Number.isInteger(x.revision)||x.revision<0||typeof x.enabled!=='boolean')fail(400,'Invalid offer.');
   // Updating stock while a reservation exists could count its cargo twice; refresh only after it finishes.
   const r=await db.prepare(`INSERT INTO transfer_offers(source_id,revision,enabled,label,peer_id,items,updated_at)
    SELECT ?,1,?,?,?,?,? WHERE ?=0 AND NOT EXISTS(SELECT 1 FROM transfer_orders WHERE source_id=? AND state IN ('queued','active'))
    ON CONFLICT(source_id) DO UPDATE SET revision=revision+1,enabled=excluded.enabled,label=excluded.label,peer_id=excluded.peer_id,items=excluded.items,updated_at=excluded.updated_at
    WHERE transfer_offers.revision=? AND NOT EXISTS(SELECT 1 FROM transfer_orders WHERE source_id=? AND state IN ('queued','active'))`)
    .bind(credential.source_id,x.enabled?1:0,name,peer,JSON.stringify(items),now,x.revision,credential.source_id,x.revision,credential.source_id).run();
   // Existing offers use UPDATE separately because INSERT's initial-revision guard intentionally excludes them.
   if(!r.meta.changes) {
    const u=await db.prepare(`UPDATE transfer_offers SET revision=revision+1,enabled=?,label=?,peer_id=?,items=?,updated_at=? WHERE source_id=? AND revision=?
     AND NOT EXISTS(SELECT 1 FROM transfer_orders WHERE source_id=? AND state IN ('queued','active'))`).bind(x.enabled?1:0,name,peer,JSON.stringify(items),now,credential.source_id,x.revision,credential.source_id).run();
    if(!u.meta.changes)fail(409,'Offer changed or has reserved stock. Finish or cancel queued requests first.');
   }
   return reply({ok:true});
  }
 }
 if(path==='/v1/transfers/offers'&&request.method==='GET') {
  const rows=await db.prepare(`SELECT o.* FROM transfer_offers o WHERE o.enabled=1 AND EXISTS(SELECT 1 FROM transfer_grants g WHERE g.source_id=o.source_id AND g.reader_id=?)`).bind(credential.id).all();
  const result=[];
  for(const o of rows.results) {
   const reservations=(await db.prepare("SELECT items FROM transfer_orders WHERE source_id=? AND state IN ('queued','active')").bind(o.source_id).all()).results.flatMap(r=>JSON.parse(r.items));
   result.push({sourceId:o.source_id,label:o.label,revision:o.revision,updatedAt:o.updated_at,items:JSON.parse(o.items).map(i=>({...i,quantity:Math.max(0,i.quantity-reservations.filter(r=>r.itemId===i.itemId&&r.highQuality===i.highQuality).reduce((a,r)=>a+r.quantity,0))}))});
  }
  return reply(result);
 }
 if(path==='/v1/transfers/orders') {
  if(request.method==='GET') {
   const rows=credential.role==='publisher'?await db.prepare('SELECT * FROM transfer_orders WHERE source_id=? ORDER BY created_at DESC LIMIT 100').bind(credential.source_id).all()
    :await db.prepare('SELECT * FROM transfer_orders WHERE reader_id=? ORDER BY created_at DESC LIMIT 100').bind(credential.id).all();
   return reply(rows.results.map(view));
  }
  if(request.method==='POST') {
   if(credential.role!=='reader')fail(403,'Use a reader invitation with transfer permission.');
   const x=await body(request),id=uuid(x.id),source=uuid(x.sourceId),guest=uuid(x.guestPeer),items=lines(x.items),r=x.recipient;
   if(!r||typeof r.contentId!=='string'||!/^[1-9][0-9]{0,19}$/.test(r.contentId)||BigInt(r.contentId)>18446744073709551615n)fail(400,'Invalid recipient.');
   const recipient={contentId:r.contentId,name:label(r.name),world:label(r.world)},json=JSON.stringify(items);
   const previous=await db.prepare('SELECT * FROM transfer_orders WHERE id=?').bind(id).first();
   if(previous){if(previous.reader_id!==credential.id||previous.source_id!==source||previous.guest_peer!==guest||previous.recipient!==JSON.stringify(recipient)||previous.items!==json)fail(409,'Request ID already used.');return reply(view(previous));}
   // One statement serializes stock validation and reservation, including simultaneous requests.
   const result=await db.prepare(`INSERT INTO transfer_orders(id,source_id,reader_id,host_peer,guest_peer,recipient,items,state,created_at,guest_seen)
    SELECT ?,o.source_id,?,o.peer_id,?,?,?,'queued',?,? FROM transfer_offers o WHERE o.source_id=? AND o.enabled=1 AND o.peer_id!=?
    AND EXISTS(SELECT 1 FROM transfer_grants WHERE reader_id=? AND source_id=o.source_id)
    AND (SELECT count(*) FROM transfer_orders WHERE source_id=o.source_id AND state IN ('queued','active'))<20
    AND NOT EXISTS(SELECT 1 FROM transfer_orders WHERE reader_id=? AND state IN ('queued','active'))
    AND NOT EXISTS(SELECT 1 FROM json_each(?) wanted WHERE
      COALESCE((SELECT json_extract(stock.value,'$.quantity') FROM json_each(o.items) stock
       WHERE json_extract(stock.value,'$.itemId')=json_extract(wanted.value,'$.itemId') AND json_extract(stock.value,'$.highQuality')=json_extract(wanted.value,'$.highQuality')),0)
      - COALESCE((SELECT SUM(json_extract(res.value,'$.quantity')) FROM transfer_orders reserved,json_each(reserved.items) res
       WHERE reserved.source_id=o.source_id AND reserved.state IN ('queued','active') AND json_extract(res.value,'$.itemId')=json_extract(wanted.value,'$.itemId') AND json_extract(res.value,'$.highQuality')=json_extract(wanted.value,'$.highQuality')),0)
      < json_extract(wanted.value,'$.quantity'))`)
    .bind(id,credential.id,guest,JSON.stringify(recipient),json,now,now,source,guest,credential.id,credential.id,json).run();
   if(!result.meta.changes)fail(409,'Stock is reserved, unavailable, or request permission changed. Refresh before requesting.');
   return reply(view(await db.prepare('SELECT * FROM transfer_orders WHERE id=?').bind(id).first()),201);
  }
 }
 const match=path.match(/^\/v1\/transfers\/orders\/([a-f0-9-]{36})(?:\/(claim|exchange|finish|cancel|presence))?$/);
 if(match) {
  const row=await db.prepare('SELECT * FROM transfer_orders WHERE id=?').bind(match[1]).first();if(!row)fail(404,'Request not found.');
  const host=credential.role==='publisher'&&row.source_id===credential.source_id,guest=credential.role==='reader'&&row.reader_id===credential.id;
  if(!host&&!guest)fail(403,'Request access denied.');
  const granted=await db.prepare('SELECT 1 FROM transfer_grants WHERE reader_id=? AND source_id=?').bind(row.reader_id,row.source_id).first();
  const action=match[2];
  if(!action&&request.method==='GET')return reply(view(row));
  if(request.method!=='POST')fail(405,'Method not allowed.');
  if(action==='presence') {if(!guest||!granted)fail(403,'Request permission revoked.');await db.prepare('UPDATE transfer_orders SET guest_seen=? WHERE id=?').bind(now,row.id).run();return reply(view(row));}
  if(action==='cancel') {
   if(row.state!=='queued')fail(409,'An active delivery must be ended and reconciled on the supplying PC.');
   await db.prepare("UPDATE transfer_orders SET state='cancelled',ended_at=? WHERE id=? AND state='queued'").bind(now,row.id).run();return reply({ok:true});
  }
  if(action==='claim') {
   if(!host||!granted)fail(403,'Request permission revoked.');
   const result=await db.prepare(`UPDATE transfer_orders SET state='active' WHERE id=? AND state='queued' AND guest_seen>?
    AND NOT EXISTS(SELECT 1 FROM transfer_orders WHERE source_id=? AND state='active')`).bind(row.id,new Date(Date.now()-45000).toISOString(),row.source_id).run();
   if(!result.meta.changes&&row.state!=='active')fail(409,'Recipient offline or another delivery is active.');return reply(view({...row,state:'active'}));
  }
  if(action==='finish') {
   if(!host)fail(403,'Only the supplier can settle a delivery.');
   if(row.state==='completed')return reply({ok:true});
   if(row.state!=='active')fail(409,'Request is not active.');
   const x=await body(request);if(typeof x.receipt!=='string'||!x.receipt||x.receipt.length>100000)fail(400,'Invalid receipt.');
   // Conservatively consume the entire reservation, even for partial delivery. A new cache publication restores leftovers.
   await db.batch([db.prepare(`UPDATE transfer_offers SET revision=revision+1,items=(SELECT json_group_array(json_set(stock.value,'$.quantity',MAX(0,json_extract(stock.value,'$.quantity')-COALESCE((SELECT json_extract(w.value,'$.quantity') FROM json_each(?) w WHERE json_extract(w.value,'$.itemId')=json_extract(stock.value,'$.itemId') AND json_extract(w.value,'$.highQuality')=json_extract(stock.value,'$.highQuality')),0)))) FROM json_each(transfer_offers.items) stock) WHERE source_id=? AND EXISTS(SELECT 1 FROM transfer_orders WHERE id=? AND state='active')`).bind(row.items,row.source_id,row.id),
    db.prepare("UPDATE transfer_orders SET state='completed',receipt=?,ended_at=? WHERE id=? AND state='active'").bind(x.receipt,now,row.id),
    db.prepare('DELETE FROM transfer_messages WHERE order_id=?').bind(row.id)]);
   return reply({ok:true});
  }
  if(action==='exchange') {
   if(!granted)fail(403,'Request permission revoked.');if(row.state!=='active')fail(409,'Delivery is not active.');
   const x=await body(request),side=host?'host':'guest',other=host?'guest':'host';
   if(!Number.isSafeInteger(x.after)||x.after<0||!Array.isArray(x.messages)||x.messages.length>32)fail(400,'Invalid delivery exchange.');
   const allowed=host?['heartbeat','device-ready','roster','roster-request','command','query','trade-tally','trade-settle','pause']:['heartbeat','device-ready','roster','roster-request','response','trade-tally-result','trade-settle-result','pause'];
   const statements=[db.prepare('DELETE FROM transfer_messages WHERE order_id=? AND sender=? AND sequence<=?').bind(row.id,other,x.after)];
   for(const m of x.messages){uuid(m.id);if(!allowed.includes(m.kind)||typeof m.payload!=='string'||m.payload.length>100000)fail(400,'Message not allowed.');
    const message={id:m.id,kind:m.kind,correlationId:m.correlationId==null?null:uuid(m.correlationId),payload:m.payload};
    statements.push(db.prepare('INSERT OR IGNORE INTO transfer_messages(order_id,sender,message_id,payload) SELECT ?,?,?,? WHERE (SELECT count(*) FROM transfer_messages WHERE order_id=?)<128').bind(row.id,side,m.id,JSON.stringify(message),row.id));}
   await db.batch(statements);
   for(const m of x.messages)if(!await db.prepare('SELECT 1 FROM transfer_messages WHERE order_id=? AND sender=? AND message_id=?').bind(row.id,side,m.id).first())fail(429,'Delivery mailbox full; retry after the peer catches up.');
   const rows=await db.prepare('SELECT sequence,payload FROM transfer_messages WHERE order_id=? AND sender=? AND sequence>? ORDER BY sequence LIMIT 32').bind(row.id,other,x.after).all();
   return reply({messages:rows.results.map(m=>({sequence:m.sequence,message:JSON.parse(m.payload)}))});
  }
 }
 fail(404,'Not found.');
}
