const https = require('node:https');
const crypto = require('node:crypto');
const port = Number(process.argv[2]);
const expectProxy = process.argv[3] === 'proxy';
const key = Buffer.from('relay-test-key16').toString('base64');
function probe(ws) {
  return new Promise((resolve, reject) => {
    const req = https.request({hostname:'relay-probe.invalid',port,path:ws?'/ws':'/https',rejectUnauthorized:false,
      headers:ws?{Connection:'Upgrade',Upgrade:'websocket','Sec-WebSocket-Version':'13','Sec-WebSocket-Key':key}:{}});
    req.setTimeout(4000,()=>req.destroy(new Error('probe-timeout')));
    req.on('response', res=>{res.resume(); res.on('end',()=>ws?reject(new Error('missing-upgrade')):resolve(res.statusCode));});
    req.on('upgrade',(res,socket)=>{
      const accept=crypto.createHash('sha1').update(key+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
      socket.destroy(); res.statusCode===101 && res.headers['sec-websocket-accept']===accept?resolve(101):reject(new Error('invalid-upgrade'));
    });
    req.on('error',reject); req.end();
  });
}
(async()=>{
  if (!expectProxy) {
    try {await probe(false); throw new Error('unexpected-direct-success');}
    catch(e) {if(e.code!=='ENOTFOUND') throw e; console.log('CONTROL: environment variables alone did not proxy');}
  } else {
    if(await probe(false)!==200 || await probe(true)!==101) throw new Error('bad-response');
    console.log('PASS Node HTTPS 200 and WSS upgrade 101 through Relay');
  }
})().catch(e=>{console.error(e.code||e.message);process.exitCode=1;});
