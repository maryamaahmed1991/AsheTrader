// Tests only artificial stock, with finally cleanup. No local game files are read.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const access=JSON.parse(readFileSync('private/owner.json','utf8'));
const keys=[];
async function call(path,secret=access.Token,method='GET',data){
 const response=await fetch(new URL(path,access.Url),{method,redirect:'error',headers:{authorization:`Bearer ${secret}`,'content-type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});
 return {status:response.status,body:await response.json()};
}
async function create(role){const r=await call('/v1/keys',access.Token,'POST',{role,label:'Temporary sample test'});assert.equal(r.status,201);keys.push(r.body);return r.body;}
try {
 assert.equal((await call('/v1/catalogue','')).status,401);
 const a=await create('publisher'), b=await create('publisher'), friend=await create('reader');
 const snapshot=(key,quantity,revision=0)=>({revision,owners:[{key:key.repeat(64),items:[{itemId:10155,name:'SAMPLE Ceruleum Tank',highQuality:false,quantity}]}]});
 assert.equal((await call('/v1/source',a.token,'PUT',snapshot('a',42))).status,200);
 assert.equal((await call('/v1/source',b.token,'PUT',snapshot('b',8))).status,200);
 assert.equal((await call('/v1/catalogue',friend.token)).body.items[0].quantity,50);
 assert.equal((await call('/v1/manage',friend.token)).status,403);
 assert.equal((await call('/v1/source',friend.token,'PUT',snapshot('c',1))).status,403);
 assert.equal((await call('/v1/source',a.token,'PUT',snapshot('a',1))).status,409);
 assert.equal((await call('/v1/source',b.token,'PUT',snapshot('a',1,1))).status,409);
 assert.equal((await call('/v1/catalogue',friend.token)).body.items[0].quantity,50);
 assert.equal((await call(`/v1/keys/${friend.id}`,access.Token,'DELETE')).status,200);
 assert.equal((await call('/v1/catalogue',friend.token)).status,401);
 console.log('Hosted tests passed: anonymous denial, two-PC aggregation, reader isolation, stale-write rejection, overlap rejection and invitation revocation.');
} finally {
 for(const key of keys) {const response=await call(`/v1/keys/${key.id}`,access.Token,'DELETE');if(![200,404].includes(response.status))throw new Error('Sample cleanup failed. Inspect owner access list.');}
 const remaining=await call('/v1/catalogue');assert.equal(remaining.status,200);assert.equal(remaining.body.items.length,0);
 console.log('Temporary credentials and sample stock removed. Catalogue is empty.');
}
