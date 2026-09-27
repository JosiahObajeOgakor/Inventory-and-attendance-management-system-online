// Local stand-in for deploy/nginx/chewypetfeeds.conf, so the whole site can be tried on one port:
//   /            -> landing page      (client/landing-ui build)
//   /inventory/  -> inventory app     (client/inventory-ui built with --base-href /inventory/)
//   /api/        -> the API
// Usage: node scripts/site-proxy.js <landing-dir> <inventory-dir> [port=4321] [apiPort=5000]
const http = require('http'), fs = require('fs'), path = require('path');
const landing = path.resolve(process.argv[2]);
const inventory = path.resolve(process.argv[3]);
const port = Number(process.argv[4] || 4321);
const apiPort = Number(process.argv[5] || 5000);
const types = {
  '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.woff': 'font/woff',
  '.ico': 'image/x-icon', '.json': 'application/json', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.webp': 'image/webp', '.mp4': 'video/mp4',
};

function sendFile(req, res, file) {
  const size = fs.statSync(file).size;
  const type = types[path.extname(file).toLowerCase()] || 'application/octet-stream';
  const range = /bytes=(\d*)-(\d*)/.exec(req.headers.range || '');
  if (range && (range[1] || range[2])) {   // video seeking/streaming (Safari won't play mp4 without this)
    const start = range[1] ? Number(range[1]) : size - Number(range[2]);
    const end = range[1] && range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
    if (start >= size || start > end) { res.writeHead(416, { 'Content-Range': `bytes */${size}` }); return res.end(); }
    res.writeHead(206, { 'Content-Type': type, 'Content-Range': `bytes ${start}-${end}/${size}`, 'Accept-Ranges': 'bytes', 'Content-Length': end - start + 1 });
    return fs.createReadStream(file, { start, end }).pipe(res);
  }
  res.writeHead(200, { 'Content-Type': type, 'Content-Length': size, 'Accept-Ranges': 'bytes', 'Cache-Control': 'no-store' });
  fs.createReadStream(file).pipe(res);
}

// A file under root, or root/index.html for app routes (SPA fallback). Never escapes root.
function resolve(root, urlPath) {
  const file = path.join(root, decodeURIComponent(urlPath));
  if (file.startsWith(root) && fs.existsSync(file) && fs.statSync(file).isFile()) return file;
  return path.join(root, 'index.html');
}

http.createServer((req, res) => {
  const urlPath = req.url.split('?')[0];
  if (urlPath.startsWith('/api/')) {
    const p = http.request({ host: 'localhost', port: apiPort, path: req.url, method: req.method, headers: req.headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    p.on('error', () => { res.writeHead(502); res.end('API is not reachable'); });
    return req.pipe(p);
  }
  if (urlPath === '/inventory') { res.writeHead(301, { Location: '/inventory/' }); return res.end(); }
  if (urlPath.startsWith('/inventory/')) return sendFile(req, res, resolve(inventory, urlPath.slice('/inventory'.length)));
  sendFile(req, res, resolve(landing, urlPath));
}).listen(port, () => console.log(`Site on http://localhost:${port}: / -> ${landing}, /inventory/ -> ${inventory}, /api -> :${apiPort}`));
