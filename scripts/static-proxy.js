// Tiny stand-in for NGINX, used by scripts/dev.ps1 when `ng serve` can't run (e.g. a '#' in the folder path):
// serves the built Angular app, falls back to index.html for SPA routes, and proxies /api/* to the API.
const http = require('http'), fs = require('fs'), path = require('path');
const root = path.resolve(process.argv[2]);
const port = Number(process.argv[3] || 4200);
const apiPort = Number(process.argv[4] || 5000);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.ico': 'image/x-icon', '.json': 'application/json' };

http.createServer((req, res) => {
  if (req.url.startsWith('/api/')) {
    const p = http.request({ host: 'localhost', port: apiPort, path: req.url, method: req.method, headers: req.headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    p.on('error', () => { res.writeHead(502); res.end('API is not reachable'); });
    req.pipe(p);
    return;
  }
  let file = path.join(root, decodeURIComponent(req.url.split('?')[0]));
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) file = path.join(root, 'index.html');
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}).listen(port, () => console.log(`Serving ${root} on http://localhost:${port} (API -> :${apiPort})`));
