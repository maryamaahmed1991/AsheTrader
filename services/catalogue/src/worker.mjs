import {devices} from './devices.mjs';
// Private catalogue and owner-authorized device rendezvous. No public registration.
const headers = { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' };
const reply = (data, status=200) => new Response(JSON.stringify(data), {status, headers});
const fail = (status, message) => { throw Object.assign(new Error(message), {status}); };
export async function digest(value) {
  return [...new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value)))].map(x=>x.toString(16).padStart(2,'0')).join('');
}
export function token() { return [...crypto.getRandomValues(new Uint8Array(32))].map(x=>x.toString(16).padStart(2,'0')).join(''); }
function label(value) {
  if (typeof value !== 'string' || !value.trim() || value.length > 80 || /[\x00-\x1f\x7f]/u.test(value)) fail(400, 'Use a label of 1–80 printable characters.');
  return value.trim();
}
async function body(request) {
  if (!request.headers.get('content-type')?.startsWith('application/json')) fail(415,'JSON required.');
  if (Number(request.headers.get('content-length') || 0) > 512000) fail(413,'Snapshot too large.');
  const reader = request.body?.getReader(); if (!reader) fail(400,'JSON body required.');
  let size=0; const chunks=[];
  while (true) { const {done,value}=await reader.read(); if(done) break; size+=value.length; if(size>512000) {await reader.cancel(); fail(413,'Snapshot too large.');} chunks.push(value); }
  const bytes=new Uint8Array(size); let offset=0; for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}
  try { return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes)); } catch { fail(400,'Invalid JSON.'); }
}
export function validateSnapshot(input) {
  if (!input || !Array.isArray(input.owners) || input.owners.length > 500 || !Number.isSafeInteger(input.revision) || input.revision < 0) fail(400,'Invalid snapshot or revision.');
  const seen=new Set(); let count=0;
  const owners=input.owners.map(owner=>{
    if (!owner || !/^[a-f0-9]{64}$/.test(owner.key) || seen.has(owner.key) || !Array.isArray(owner.items)) fail(400,'Invalid or duplicate inventory owner.');
    seen.add(owner.key); const itemsSeen=new Set();
    const items=owner.items.map(item=>{
      if (++count>5000 || !Number.isInteger(item.itemId) || item.itemId<1 || item.itemId>10000000 || typeof item.highQuality!=='boolean' || !Number.isSafeInteger(item.quantity) || item.quantity<1 || item.quantity>1000000000) fail(400,'Invalid stock entry or snapshot exceeds 5,000 entries.');
      const key=`${item.itemId}:${item.highQuality}`; if(itemsSeen.has(key)) fail(400,'Duplicate stock entry.'); itemsSeen.add(key);
      return {itemId:item.itemId, name:label(item.name), highQuality:item.highQuality, quantity:item.quantity};
    });
    return {key:owner.key,items};
  });
  // Explicit reconstruction strips unknown fields, including accidental character/retainer labels.
  return {owners};
}
export default {
  async fetch(request, env) {
    try {
      const url=new URL(request.url); const path=url.pathname;
      if(path==='/health' && request.method==='GET') return reply({service:'Ashe private catalogue',version:1});
      if(env.RATE_LIMITER && !(await env.RATE_LIMITER.limit({key:request.headers.get('CF-Connecting-IP') || 'unknown'})).success) return reply({error:'Too many requests. Try again shortly.'},429);
      const authorization=request.headers.get('authorization') || '';
      if(!/^Bearer [a-f0-9]{64}$/.test(authorization)) fail(401,'Access denied.');
      if(path.startsWith('/v1/device-link')) return await devices(request,env,path,authorization.slice(7),{reply,fail,digest,token,body,label});
      const credential=await env.DB.prepare('SELECT id, role, source_id FROM credentials WHERE hash=?').bind(await digest(authorization.slice(7))).first();
      if(!credential) fail(401,'Access denied.');
      const owner=()=>{if(credential.role!=='owner') fail(403,'Owner access required.');};
      if(path==='/v1/catalogue' && request.method==='GET') {
        const store=await env.DB.prepare('SELECT name FROM store WHERE id=1').first();
        const {results}=await env.DB.prepare('SELECT snapshot,published_at FROM sources ORDER BY id').all();
        const totals=new Map();
        for(const source of results) for(const inventory of JSON.parse(source.snapshot).owners) for(const item of inventory.items){
          const key=`${item.itemId}:${item.highQuality}`; const entry=totals.get(key);
          if(entry){entry.quantity+=item.quantity; if(source.published_at<entry.oldestPublishedAt) entry.oldestPublishedAt=source.published_at;}
          else totals.set(key,{...item,oldestPublishedAt:source.published_at});
        }
        return reply({name:store?.name || 'Private store', retrievedAt:new Date().toISOString(), sources:results.map(s=>({publishedAt:s.published_at})), items:[...totals.values()].sort((a,b)=>a.name.localeCompare(b.name)||a.itemId-b.itemId||Number(a.highQuality)-Number(b.highQuality))});
      }
      if(path==='/v1/manage' && request.method==='GET') {
        owner(); const store=await env.DB.prepare('SELECT name,salt FROM store WHERE id=1').first();
        const keys=await env.DB.prepare("SELECT id,role,label,source_id AS sourceId,created_at AS createdAt FROM credentials WHERE role!='owner' ORDER BY created_at").all();
        const sources=await env.DB.prepare('SELECT id,label,revision,published_at AS publishedAt FROM sources').all();
        return reply({...store,keys:keys.results,sources:sources.results});
      }
      if(path==='/v1/keys' && request.method==='POST') {
        owner(); const input=await body(request); const name=label(input.label);
        if(!['reader','publisher'].includes(input.role)) fail(400,'Invalid role.');
        const secret=token(), hash=await digest(secret), id=crypto.randomUUID(), now=new Date().toISOString();
        // Limit insert in SQL as well as validation, avoiding concurrent quota bypass.
        if(input.role==='reader') {
          const result=await env.DB.prepare("INSERT INTO credentials SELECT ?,?,'reader',?,NULL,? WHERE (SELECT count(*) FROM credentials WHERE role='reader')<100").bind(id,hash,name,now).run();
          if(!result.meta.changes) fail(409,'Invitation limit reached. Revoke an old invitation first.');
        } else {
          const result=await env.DB.batch([
            env.DB.prepare('INSERT INTO sources(id,label) SELECT ?,? WHERE (SELECT count(*) FROM sources)<8').bind(id,name),
            env.DB.prepare("INSERT INTO credentials SELECT ?,?,'publisher',?,?,? WHERE EXISTS(SELECT 1 FROM sources WHERE id=?)").bind(id,hash,name,id,now,id)
          ]);
          if(!result[0].meta.changes) fail(409,'Publisher limit reached. Remove an old PC first.');
        }
        return reply({id,token:secret,role:input.role},201);
      }
      const keyMatch=path.match(/^\/v1\/keys\/([a-f0-9-]{36})$/);
      if(keyMatch && request.method==='DELETE') {
        owner(); const key=await env.DB.prepare("SELECT source_id FROM credentials WHERE id=? AND role!='owner'").bind(keyMatch[1]).first();
        if(!key) fail(404,'Invitation or publisher not found.');
        await env.DB.batch([env.DB.prepare("DELETE FROM credentials WHERE id=? AND role!='owner'").bind(keyMatch[1]),env.DB.prepare('DELETE FROM sources WHERE id=?').bind(key.source_id || '')]);
        return reply({removed:true});
      }
      if(path==='/v1/source' && (request.method==='GET' || request.method==='PUT' || request.method==='DELETE')) {
        if(credential.role!=='publisher') fail(403,'Publishing access required.');
        if(request.method==='GET') {
          const source=await env.DB.prepare('SELECT revision,published_at AS publishedAt FROM sources WHERE id=?').bind(credential.source_id).first();
          const store=await env.DB.prepare('SELECT salt FROM store WHERE id=1').first();
          return reply({...source,salt:store.salt});
        }
        const input=await body(request); const snapshot=validateSnapshot(request.method==='DELETE'?{revision:input.revision,owners:[]}:input);
        const json=JSON.stringify(snapshot), now=new Date().toISOString();
        // Single conditional statement: atomic revision check, overlap check and replacement.
        const result=await env.DB.prepare(`UPDATE sources SET snapshot=?, revision=revision+1, published_at=? WHERE id=? AND revision=? AND NOT EXISTS (
          SELECT 1 FROM sources other, json_each(other.snapshot,'$.owners') existing
          WHERE other.id != ? AND json_extract(existing.value,'$.key') IN
            (SELECT json_extract(value,'$.key') FROM json_each(?,'$.owners')))
          AND EXISTS(SELECT 1 FROM credentials WHERE id=? AND role='publisher')`)
          .bind(json,now,credential.source_id,input.revision,credential.source_id,json,credential.id).run();
        if(!result.meta.changes) fail(409,'Stock changed, access was revoked, or these inventories are already published by another PC. Refresh; use separate character selections on each PC.');
        return reply({revision:input.revision+1,publishedAt:now});
      }
      fail(404,'Not found.');
    } catch(error) {
      // No request contents, credentials, SQL parameters or exception details in logs/responses.
      return reply({error:error.status ? error.message : 'Catalogue service unavailable. Try again later.'}, error.status || 503);
    }
  }
};
