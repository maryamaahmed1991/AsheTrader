import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync} from 'node:fs';
import worker,{digest,token} from '../src/worker.mjs';

// Real SQLite executes the production SQL, including JSON overlap and revision predicates.
function d1(db) {
  function prepare(sql){let args=[];return {bind(...values){args=values;return this;},async first(){return db.prepare(sql).get(...args)||null;},async all(){return {results:db.prepare(sql).all(...args)};},async run(){return {meta:{changes:db.prepare(sql).run(...args).changes}};}};}
  return {prepare,async batch(statements){db.exec('BEGIN');try{const result=[];for(const s of statements)result.push(await s.run());db.exec('COMMIT');return result;}catch(e){db.exec('ROLLBACK');throw e;}}};
}
async function fixture(){
  const db=new DatabaseSync(':memory:');db.exec(readFileSync(new URL('../migrations/0001_catalogue.sql',import.meta.url),'utf8'));
  db.exec(readFileSync(new URL('../migrations/0002_devices.sql',import.meta.url),'utf8'));
  db.exec(readFileSync(new URL('../migrations/0003_friend_orders.sql',import.meta.url),'utf8'));
  const owner=token();db.prepare('INSERT INTO store VALUES(1,?,?)').run("Ashe's store",token());
  db.prepare('INSERT INTO credentials VALUES(?,?,?,?,?,?)').run('owner',await digest(owner),'owner','Owner',null,new Date().toISOString());
  const env={DB:d1(db)};
  async function call(path,secret=owner,method='GET',data){const response=await worker.fetch(new Request(`https://catalogue.example${path}`,{method,headers:{authorization:`Bearer ${secret}`,'content-type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)}),env);return {status:response.status,body:await response.json(),headers:response.headers};}
  async function key(role='reader'){return (await call('/v1/keys',owner,'POST',{role,label:'Test'})).body;}
  return {db,owner,call,key,env};
}
const stock=(key='a'.repeat(64),quantity=42,revision=0)=>({revision,owners:[{key,items:quantity?[{itemId:10155,name:'Ceruleum Tank',highQuality:false,quantity}]:[]}]});

test('authentication and role isolation; credentials never stored in plaintext',async()=>{
 const f=await fixture(), reader=await f.key(), publisher=await f.key('publisher');
 assert.equal((await f.call('/v1/catalogue','')).status,401);
 assert.equal((await f.call('/v1/manage',reader.token)).status,403);
 assert.equal((await f.call('/v1/keys',publisher.token,'POST',{role:'reader',label:'x'})).status,403);
 assert.equal((await f.call('/v1/source',reader.token,'PUT',stock())).status,403);
 assert.equal((await f.call('/v1/source',f.owner,'PUT',stock())).status,403);
 assert.ok(!JSON.stringify(f.db.prepare('SELECT * FROM credentials').all()).includes(reader.token));
 assert.equal((await f.call('/v1/catalogue',reader.token)).headers.get('cache-control'),'no-store');
});
test('offline catalogue aggregates separate PCs, with HQ separate and no owner identifiers',async()=>{
 const f=await fixture(), a=await f.key('publisher'),b=await f.key('publisher'),reader=await f.key();
 assert.equal((await f.call('/v1/source',a.token,'PUT',stock())).status,200);
 const second=stock('b'.repeat(64),8);second.owners[0].items.push({itemId:10155,name:'Ceruleum Tank',highQuality:true,quantity:3});
 assert.equal((await f.call('/v1/source',b.token,'PUT',second)).status,200);
 const view=await f.call('/v1/catalogue',reader.token);
 assert.deepEqual(view.body.items.map(x=>x.quantity),[50,3]);
 assert.ok(view.body.items.every(x=>x.oldestPublishedAt));
 assert.ok(!JSON.stringify(view.body).includes('a'.repeat(64)));
});
test('overlapping owners rejected atomically; independent source cannot erase other PC',async()=>{
 const f=await fixture(),a=await f.key('publisher'),b=await f.key('publisher');
 await f.call('/v1/source',a.token,'PUT',stock());
 assert.equal((await f.call('/v1/source',b.token,'PUT',stock())).status,409);
 assert.equal((await f.call('/v1/source',b.token)).body.revision,0);
 assert.equal((await f.call('/v1/catalogue')).body.items[0].quantity,42);
});
test('replacement removes old rows, stale revisions cannot replay and clear permits reassignment',async()=>{
 const f=await fixture(),a=await f.key('publisher'),b=await f.key('publisher');
 await f.call('/v1/source',a.token,'PUT',stock());
 assert.equal((await f.call('/v1/source',a.token,'PUT',stock('a'.repeat(64),9))).status,409);
 assert.equal((await f.call('/v1/source',a.token,'PUT',stock('a'.repeat(64),0,1))).status,200);
 assert.equal((await f.call('/v1/catalogue')).body.items.length,0);
 assert.equal((await f.call('/v1/source',b.token,'PUT',stock())).status,409);
 assert.equal((await f.call('/v1/source',a.token,'DELETE',{revision:2})).status,200);
 assert.equal((await f.call('/v1/source',b.token,'PUT',stock())).status,200);
});
test('revoking reader rejects later reads; removing PC deletes stock and write access',async()=>{
 const f=await fixture(),p=await f.key('publisher'),r=await f.key();
 await f.call('/v1/source',p.token,'PUT',stock());
 assert.equal((await f.call(`/v1/keys/${r.id}`,f.owner,'DELETE')).status,200);
 assert.equal((await f.call('/v1/catalogue',r.token)).status,401);
 await f.call(`/v1/keys/${p.id}`,f.owner,'DELETE');
 assert.equal((await f.call('/v1/catalogue')).body.items.length,0);
 assert.equal((await f.call('/v1/source',p.token,'PUT',stock())).status,401);
});
test('reject malformed and duplicate counts; strip unrequested identity fields',async()=>{
 const f=await fixture(),p=await f.key('publisher');
 for(const value of [-1,1.5,Number.MAX_SAFE_INTEGER]) assert.equal((await f.call('/v1/source',p.token,'PUT',stock('a'.repeat(64),value))).status,400);
 const duplicate=stock();duplicate.owners.push(duplicate.owners[0]);assert.equal((await f.call('/v1/source',p.token,'PUT',duplicate)).status,400);
 const input=stock();input.characterName='PRIVATE';input.owners[0].retainerName='PRIVATE';input.owners[0].items[0].location='PRIVATE';
 await f.call('/v1/source',p.token,'PUT',input);
 assert.ok(!f.db.prepare('SELECT snapshot FROM sources').get().snapshot.includes('PRIVATE'));
});
test('rate limit and unexpected service failures do not leak credentials or SQL',async()=>{
 const f=await fixture();f.env.RATE_LIMITER={async limit(){return {success:false};}};
 assert.equal((await f.call('/v1/catalogue')).status,429);
 delete f.env.RATE_LIMITER;f.db.close();const response=await f.call('/v1/catalogue');assert.equal(response.status,503);assert.ok(!response.body.error.includes('SELECT'));
});

test('trusted PCs have one-use invitations and cannot inherit catalogue permissions',async()=>{
 const f=await fixture(),reader=await f.key(),publisher=await f.key('publisher'),host=crypto.randomUUID(),guest=crypto.randomUUID();
 for(const access of [reader.token,publisher.token])assert.equal((await f.call('/v1/device-links',access,'POST',{peerId:host,name:'Main'})).status,403);
 const created=await f.call('/v1/device-links',f.owner,'POST',{peerId:host,name:'Main'});assert.equal(created.status,201);
 const {token:hostToken,invite}=created.body;
 assert.equal((await f.call('/v1/device-link',invite)).status,401);
 assert.equal((await f.call('/v1/device-link',reader.token)).status,401);
 assert.equal((await f.call('/v1/catalogue',hostToken)).status,401);
 const joined=await f.call('/v1/device-links/redeem',invite,'POST',{peerId:guest,name:'Second'});assert.equal(joined.status,201);
 assert.equal(joined.body.hostPeerId,host);
 assert.equal((await f.call('/v1/device-links/redeem',invite,'POST',{peerId:crypto.randomUUID(),name:'Third'})).status,401);
 const state=await f.call('/v1/device-link',hostToken);assert.equal(state.body.guestPeerId,guest);
 assert.ok(!JSON.stringify(state.body).includes(joined.body.token));
 const raw=JSON.stringify(f.db.prepare('SELECT * FROM device_links').all());
 for(const secret of [invite,hostToken,joined.body.token])assert.ok(!raw.includes(secret));
 assert.equal((await f.call('/v1/source',joined.body.token,'PUT',stock())).status,401);
});

test('device rendezvous restricts endpoint updates to coordinator and private IPv4; revocation disconnects both',async()=>{
 const f=await fixture(),host=crypto.randomUUID(),guest=crypto.randomUUID();
 const created=(await f.call('/v1/device-links',f.owner,'POST',{peerId:host,name:'Main'})).body;
 const joined=(await f.call('/v1/device-links/redeem',created.invite,'POST',{peerId:guest,name:'Second'})).body;
 for(const address of ['8.8.8.8','127.0.0.1','169.254.169.254','10.0.0.999','https://evil.example','192.168.1.01'])
   assert.equal((await f.call('/v1/device-link',created.token,'PUT',{address,port:45874})).status,400);
 assert.equal((await f.call('/v1/device-link',created.token,'PUT',{address:'192.168.1.20',port:45874})).status,200);
 const seen=await f.call('/v1/device-link',joined.token,'PUT',{address:'192.168.1.66',port:1});
 assert.equal(seen.body.address,'192.168.1.20');assert.equal(seen.body.port,45874);assert.ok(seen.body.hostSeen);assert.ok(seen.body.guestSeen);
 await f.call('/v1/device-link',created.token,'DELETE');
 for(const secret of [created.token,joined.token])assert.equal((await f.call('/v1/device-link',secret)).status,401);
});

test('expired device invitation and duplicated PC identity are rejected',async()=>{
 const f=await fixture(),host=crypto.randomUUID();
 const created=(await f.call('/v1/device-links',f.owner,'POST',{peerId:host,name:'Main'})).body;
 assert.equal((await f.call('/v1/device-links/redeem',created.invite,'POST',{peerId:host,name:'Clone'})).status,409);
 f.db.prepare('UPDATE device_links SET invite_expires=?').run('2000-01-01T00:00:00.000Z');
 assert.equal((await f.call('/v1/device-links/redeem',created.invite,'POST',{peerId:crypto.randomUUID(),name:'Late'})).status,401);
});

test('concurrent invite claims yield exactly one trusted worker',async()=>{
 const f=await fixture(),host=crypto.randomUUID();
 const created=(await f.call('/v1/device-links',f.owner,'POST',{peerId:host,name:'Main'})).body;
 const results=await Promise.all([1,2].map(i=>f.call('/v1/device-links/redeem',created.invite,'POST',{peerId:crypto.randomUUID(),name:`Guest ${i}`})));
 assert.equal(results.filter(x=>x.status===201).length,1);
 assert.equal(f.db.prepare('SELECT count(*) AS n FROM device_links WHERE guest_peer IS NOT NULL').get().n,1);
});

async function transferFixture(){
 const f=await fixture(),p=await f.key('publisher'),r=await f.key(),host=crypto.randomUUID(),guest=crypto.randomUUID();
 const items=[{itemId:10155,highQuality:false,name:'Ceruleum Tank',quantity:100}];
 const offer={revision:0,enabled:true,peerId:host,label:'Fuel stock',items};
 assert.equal((await f.call('/v1/transfers/offer',p.token,'PUT',offer)).status,200);
 const grant={readerId:r.id,sourceId:p.id};assert.equal((await f.call('/v1/transfers/grants',f.owner,'PUT',grant)).status,200);
 const request={id:crypto.randomUUID(),sourceId:p.id,guestPeer:guest,recipient:{contentId:'18014498570037335',name:'Friend Alt',world:'Faerie'},items};
 return {...f,p,r,host,guest,items,offer,grant,request};
}
test('ordinary readers cannot request or control publishers; explicit grant exposes only allowed offers',async()=>{
 const f=await transferFixture(),outsider=await f.key();
 assert.deepEqual((await f.call('/v1/transfers/offers',outsider.token)).body,[]);
 assert.equal((await f.call('/v1/transfers/orders',outsider.token,'POST',f.request)).status,409);
 assert.equal((await f.call('/v1/transfers/grants',f.r.token,'PUT',f.grant)).status,403);
 assert.equal((await f.call('/v1/transfers/offer',f.r.token,'PUT',f.offer)).status,403);
 assert.equal((await f.call('/v1/transfers/orders',f.p.token,'POST',f.request)).status,403);
 const offers=(await f.call('/v1/transfers/offers',f.r.token)).body;
 assert.equal(offers.length,1);assert.equal(offers[0].items[0].quantity,100);assert.ok(!JSON.stringify(offers).includes(f.host));
});
test('requests reserve stock atomically, reject excess and keep retries idempotent',async()=>{
 const f=await transferFixture(),other=await f.key();
 await f.call('/v1/transfers/grants',f.owner,'PUT',{...f.grant,readerId:other.id});
 const a={...f.request,items:[{...f.items[0],quantity:60}]},b={...f.request,id:crypto.randomUUID(),guestPeer:crypto.randomUUID(),items:[{...f.items[0],quantity:60}]};
 const results=await Promise.all([f.call('/v1/transfers/orders',f.r.token,'POST',a),f.call('/v1/transfers/orders',other.token,'POST',b)]);
 assert.equal(results.filter(r=>r.status===201).length,1);assert.equal(results.filter(r=>r.status===409).length,1);
 const winner=results[0].status===201?{key:f.r.token,request:a}:{key:other.token,request:b};
 assert.equal((await f.call('/v1/transfers/orders',winner.key,'POST',winner.request)).status,200);
 assert.equal((await f.call('/v1/transfers/orders',winner.key,'POST',{...winner.request,items:f.items})).status,409);
 assert.equal((await f.call('/v1/transfers/offers',f.r.token)).body[0].items[0].quantity,40);
 assert.equal((await f.call('/v1/transfers/offer',f.p.token,'PUT',{...f.offer,revision:1})).status,409);
});
test('unknown items, HQ substitution, invalid counts, duplicate lines and recipient precision are rejected',async()=>{
 const f=await transferFixture();
 for(const items of [[{...f.items[0],quantity:101}],[{...f.items[0],highQuality:true}],[{...f.items[0],itemId:99999}]])assert.equal((await f.call('/v1/transfers/orders',f.r.token,'POST',{...f.request,items})).status,409);
 for(const quantity of [0,-1,1.5,1000000001])assert.equal((await f.call('/v1/transfers/orders',f.r.token,'POST',{...f.request,items:[{...f.items[0],quantity}]})).status,400);
 assert.equal((await f.call('/v1/transfers/orders',f.r.token,'POST',{...f.request,items:[...f.items,...f.items]})).status,400);
 assert.equal((await f.call('/v1/transfers/orders',f.r.token,'POST',{...f.request,recipient:{...f.request.recipient,contentId:18014498570037335}})).status,400);
 const good=await f.call('/v1/transfers/orders',f.r.token,'POST',f.request);assert.equal(good.body.recipient.contentId,'18014498570037335');
});
test('queued cancellation releases reservation; active cancellation requires supplier reconciliation',async()=>{
 const f=await transferFixture();await f.call('/v1/transfers/orders',f.r.token,'POST',f.request);
 const path=`/v1/transfers/orders/${f.request.id}`;
 assert.equal((await f.call(path+'/cancel',f.r.token,'POST',{})).status,200);
 assert.equal((await f.call('/v1/transfers/offers',f.r.token)).body[0].items[0].quantity,100);
 const request={...f.request,id:crypto.randomUUID()};await f.call('/v1/transfers/orders',f.r.token,'POST',request);
 const active=`/v1/transfers/orders/${request.id}`;
 assert.equal((await f.call(active+'/claim',f.r.token,'POST',{})).status,403);
 assert.equal((await f.call(active+'/claim',f.p.token,'POST',{})).status,200);
 assert.equal((await f.call(active+'/cancel',f.r.token,'POST',{})).status,409);
});
test('claim requires recipient presence and grants; per-source active jobs are serialized',async()=>{
 const f=await transferFixture();await f.call('/v1/transfers/orders',f.r.token,'POST',{...f.request,items:[{...f.items[0],quantity:50}]});
 const path=`/v1/transfers/orders/${f.request.id}`;
 f.db.prepare('UPDATE transfer_orders SET guest_seen=?').run('2000-01-01T00:00:00Z');
 assert.equal((await f.call(path+'/claim',f.p.token,'POST',{})).status,409);
 await f.call(path+'/presence',f.r.token,'POST',{});assert.equal((await f.call(path+'/claim',f.p.token,'POST',{})).status,200);
 const other=await f.key();await f.call('/v1/transfers/grants',f.owner,'PUT',{...f.grant,readerId:other.id});
 const second={...f.request,id:crypto.randomUUID(),guestPeer:crypto.randomUUID(),items:[{...f.items[0],quantity:50}]};
 await f.call('/v1/transfers/orders',other.token,'POST',second);
 assert.equal((await f.call(`/v1/transfers/orders/${second.id}/claim`,f.p.token,'POST',{})).status,409);
});
test('delivery exchange rejects unrelated readers, guest commands and private inventory sync; revocation stops messages',async()=>{
 const f=await transferFixture(),other=await f.key();await f.call('/v1/transfers/orders',f.r.token,'POST',f.request);
 const path=`/v1/transfers/orders/${f.request.id}`;await f.call(path+'/claim',f.p.token,'POST',{});
 const msg={id:crypto.randomUUID(),kind:'command',payload:'{}'},input={after:0,messages:[msg]};
 assert.equal((await f.call(path+'/exchange',other.token,'POST',input)).status,403);
 assert.equal((await f.call(path+'/exchange',f.r.token,'POST',input)).status,400);
 assert.equal((await f.call(path+'/exchange',f.p.token,'POST',{...input,messages:[{...msg,kind:'inventory-sync'}]})).status,400);
 assert.equal((await f.call(path+'/exchange',f.p.token,'POST',input)).status,200);
 assert.equal((await f.call(path+'/exchange',f.p.token,'POST',input)).status,200);
 const received=await f.call(path+'/exchange',f.r.token,'POST',{after:0,messages:[]});assert.equal(received.body.messages.length,1);
 await f.call('/v1/transfers/grants',f.owner,'DELETE',f.grant);
 assert.equal((await f.call(path+'/exchange',f.r.token,'POST',{after:0,messages:[]})).status,403);
 assert.equal((await f.call(path+'/exchange',f.p.token,'POST',input)).status,403);
 assert.equal(f.db.prepare('SELECT state FROM transfer_orders').get().state,'active');
});
test('settling consumes reservation once, persists receipt, and permits fresh stock after completion',async()=>{
 const f=await transferFixture();await f.call('/v1/transfers/orders',f.r.token,'POST',f.request);
 const path=`/v1/transfers/orders/${f.request.id}`;await f.call(path+'/claim',f.p.token,'POST',{});
 const receipt=JSON.stringify({items:[{itemId:10155,delivered:50,shortage:50}]});
 assert.equal((await f.call(path+'/finish',f.r.token,'POST',{receipt})).status,403);
 assert.equal((await f.call(path+'/finish',f.p.token,'POST',{receipt})).status,200);
 assert.equal((await f.call(path+'/finish',f.p.token,'POST',{receipt})).status,200);
 assert.equal(JSON.parse((await f.call(path,f.r.token)).body.receipt).items[0].delivered,50);
 const offer=(await f.call('/v1/transfers/offer',f.p.token)).body;assert.equal(offer.revision,2);assert.equal(JSON.parse(offer.items)[0].quantity,0);
 assert.equal((await f.call('/v1/transfers/offer',f.p.token,'PUT',{...f.offer,revision:2,items:[{...f.items[0],quantity:50}]})).status,200);
 assert.equal((await f.call('/v1/transfers/offers',f.r.token)).body[0].items[0].quantity,50);
});

test('revoking a reader never releases active delivery stock for a second request',async()=>{
 const f=await transferFixture();await f.call('/v1/transfers/orders',f.r.token,'POST',f.request);
 const path=`/v1/transfers/orders/${f.request.id}`;await f.call(path+'/claim',f.p.token,'POST',{});
 assert.equal((await f.call(`/v1/keys/${f.r.id}`,f.owner,'DELETE')).status,200);
 assert.equal(f.db.prepare('SELECT state FROM transfer_orders WHERE id=?').get(f.request.id).state,'active');
 assert.equal((await f.call(path+'/exchange',f.r.token,'POST',{after:0,messages:[]})).status,401);
 const other=await f.key();await f.call('/v1/transfers/grants',f.owner,'PUT',{...f.grant,readerId:other.id});
 assert.equal((await f.call('/v1/transfers/offers',other.token)).body[0].items[0].quantity,0);
 assert.equal((await f.call(path+'/finish',f.p.token,'POST',{receipt:'{"outcome":"Ended"}'})).status,200);
});

test('an empty refresh removes requestable stock rather than retaining an obsolete positive count',async()=>{
 const f=await transferFixture();
 assert.equal((await f.call('/v1/transfers/offer',f.p.token,'PUT',{...f.offer,revision:1,items:[]})).status,200);
 assert.deepEqual((await f.call('/v1/transfers/offers',f.r.token)).body[0].items,[]);
 assert.equal((await f.call('/v1/transfers/orders',f.r.token,'POST',f.request)).status,409);
});
