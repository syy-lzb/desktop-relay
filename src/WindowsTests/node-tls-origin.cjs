const https=require('node:https');
const fs=require('node:fs');
const crypto=require('node:crypto');
const pem=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));
const server=https.createServer(pem,(req,res)=>{res.writeHead(200,{'Content-Length':2});res.end('OK');});
server.on('upgrade',(req,socket)=>{
  const accept=crypto.createHash('sha1').update(req.headers['sec-websocket-key']+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.end('HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: '+accept+'\r\n\r\n');
});
server.listen(0,'127.0.0.1',()=>console.log(server.address().port));
