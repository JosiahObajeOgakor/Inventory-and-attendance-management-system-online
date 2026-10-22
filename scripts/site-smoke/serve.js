// Serves a built landing page locally and forwards /api/* to the live site, so a new build can be clicked through before it is deployed.
const http = require('http'), https = require('https'), fs = require('fs'), path = require('path');
const root = process.argv[2], port = Number(process.argv[3] || 4300), live = 'chewypetsfeeds.com';
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.mp4': 'video/mp4', '.ico': 'image/x-icon', '.svg': 'image/svg+xml' };
http.createServer((req, res) => {
  if (req.url.startsWith('/api/')) {
    const chunks = []; req.on('data', c => chunks.push(c)); req.on('end', () => {
      const headers = { ...req.headers, host: live }; delete headers['accept-encoding'];
      const up = https.request({ host: live, path: req.url, method: req.method, headers }, r => {
        const body = []; r.on('data', c => body.push(c)); r.on('end', () => {
          let buf = Buffer.concat(body);
          // until the backend with vatRate/payOnline is deployed, add them so the new checkout can be exercised
          if (req.url.startsWith('/api/webchat/stores') && r.statusCode === 200) {
            const s = JSON.parse(buf.toString()); s.forEach(x => { x.vatRate ??= 7.5; x.payOnline ??= true; }); buf = Buffer.from(JSON.stringify(s));
          }
          const h = { ...r.headers }; delete h['content-length']; delete h['transfer-encoding'];
          res.writeHead(r.statusCode, h); res.end(buf);
        });
      });
      up.on('error', e => { res.writeHead(502); res.end(e.message); });
      up.end(Buffer.concat(chunks));
    });
    return;
  }
  let p = decodeURIComponent(req.url.split('?')[0]);
  let file = path.join(root, p);
  if (fs.existsSync(file) && fs.statSync(file).isDirectory()) file = path.join(file, 'index.html');
  if (!fs.existsSync(file)) file = path.join(root, 'index.html');
  res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}).listen(port, () => console.log('serving on ' + port));
