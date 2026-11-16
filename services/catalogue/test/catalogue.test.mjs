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
