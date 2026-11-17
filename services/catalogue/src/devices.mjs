// Only connection metadata is hosted here. Inventory snapshots and game commands stay on the paired PCs.
export async function devices(request, env, path, secret, helpers) {
  const {reply, fail, digest, token, body, label}=helpers;
  const hash=await digest(secret);
  const peer=value=>{if(typeof value!=='string'||!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value)||value==='00000000-0000-0000-0000-000000000000')fail(400,'Invalid device identity.');return value;};
  if(path==='/v1/device-links' && request.method==='POST') {
    const owner=await env.DB.prepare("SELECT id FROM credentials WHERE hash=? AND role='owner'").bind(hash).first();
    if(!owner)fail(403,'Owner access required to add a trusted PC.');
    const input=await body(request), host=peer(input.peerId), name=label(input.name);
    const id=crypto.randomUUID(), hostToken=token(), invite=token(), expiresAt=new Date(Date.now()+600000).toISOString();
    // Remove only abandoned, expired invitations; established links never expire silently.
    await env.DB.prepare('DELETE FROM device_links WHERE guest_peer IS NULL AND invite_expires<?').bind(new Date().toISOString()).run();
    const result=await env.DB.prepare('INSERT INTO device_links(id,host_peer,host_name,host_hash,invite_hash,invite_expires) SELECT ?,?,?,?,?,? WHERE (SELECT count(*) FROM device_links)<8')
      .bind(id,host,name,await digest(hostToken),await digest(invite),expiresAt).run();
    if(!result.meta.changes)fail(409,'Device limit reached. Remove an old device link first.');
    return reply({id,token:hostToken,invite,expiresAt},201);
  }
  if(path==='/v1/device-links/redeem' && request.method==='POST') {
    const input=await body(request), guest=peer(input.peerId), name=label(input.name);
    const link=await env.DB.prepare('SELECT id,host_peer,host_name FROM device_links WHERE invite_hash=? AND invite_expires>? AND guest_peer IS NULL')
      .bind(hash,new Date().toISOString()).first();
    if(!link)fail(401,'Pairing invitation expired or already used.');
    if(link.host_peer===guest)fail(409,'Use a different device identity on the second PC.');
    const guestToken=token();
    const result=await env.DB.prepare('UPDATE device_links SET guest_peer=?,guest_name=?,guest_hash=?,invite_hash=NULL WHERE id=? AND invite_hash=? AND guest_peer IS NULL AND invite_expires>?')
      .bind(guest,name,await digest(guestToken),link.id,hash,new Date().toISOString()).run();
    if(!result.meta.changes)fail(409,'Pairing invitation was already used.');
    return reply({id:link.id,token:guestToken,hostPeerId:link.host_peer,hostName:link.host_name},201);
  }
  if(path==='/v1/device-link') {
    const link=await env.DB.prepare('SELECT * FROM device_links WHERE host_hash=? OR guest_hash=?').bind(hash,hash).first();
    if(!link)fail(401,'Device link was revoked or is unavailable.');
    const host=link.host_hash===hash;
    if(request.method==='DELETE') {await env.DB.prepare('DELETE FROM device_links WHERE id=?').bind(link.id).run();return reply({removed:true});}
    if(request.method==='PUT') {
      const input=await body(request), now=new Date().toISOString();
      if(host) {
        const address=input.address || '', parts=address.split('.').map(Number);
        const local=parts.length===4 && parts.every((p,i)=>Number.isInteger(p)&&p>=0&&p<=255&&String(p)===address.split('.')[i])
          && (parts[0]===10 || parts[0]===192&&parts[1]===168 || parts[0]===172&&parts[1]>=16&&parts[1]<=31);
        if(address!=='' && !local || !Number.isInteger(input.port) || input.port<0 || input.port>65535 || (address!=='' && input.port===0))fail(400,'Use a local-network address and port.');
        await env.DB.prepare('UPDATE device_links SET address=?,port=?,host_seen=? WHERE id=?').bind(address,input.port,now,link.id).run();
        link.address=address;link.port=input.port;link.host_seen=now;
      } else {await env.DB.prepare('UPDATE device_links SET guest_seen=? WHERE id=?').bind(now,link.id).run();link.guest_seen=now;}
    } else if(request.method!=='GET')fail(405,'Method not allowed.');
    return reply({id:link.id,hostPeerId:link.host_peer,hostName:link.host_name,guestPeerId:link.guest_peer,guestName:link.guest_name,
      address:link.address,port:link.port,hostSeen:link.host_seen,guestSeen:link.guest_seen});
  }
  fail(404,'Not found.');
}
