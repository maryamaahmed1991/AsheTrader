// Run once after migration. Generated credentials and SQL stay under gitignored private/.
// Never print the resulting access code or secret to a CI log.
import {mkdirSync,writeFileSync,existsSync} from 'node:fs';
import {token,digest} from '../src/worker.mjs';
const url=process.argv[2];
if(!url || new URL(url).protocol!=='https:') throw new Error('Pass the deployed HTTPS service root URL.');
mkdirSync('private',{recursive:true});
if(existsSync('private/owner.json')) throw new Error('Owner material already exists. Do not overwrite it.');
const secret=token(),salt=token(),id=crypto.randomUUID(),now=new Date().toISOString();
writeFileSync('private/owner.json',JSON.stringify({Version:1,Url:new URL('/',url).toString(),Token:secret,Role:'owner'}),{flag:'wx',mode:0o600});
writeFileSync('private/bootstrap.sql',`INSERT INTO store VALUES(1,'Ashe''s Store','${salt}');
INSERT INTO credentials VALUES('${id}','${await digest(secret)}','owner','Owner',NULL,'${now}');`,{flag:'wx',mode:0o600});
console.log('Owner material prepared privately. Apply private/bootstrap.sql once; never commit private/.');
