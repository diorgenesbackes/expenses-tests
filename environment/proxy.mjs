import https from 'node:https'
import http from 'node:http'
import { readFileSync } from 'node:fs'
import { resolve, extname, sep } from 'node:path'

const root = resolve(process.env.LAB_UI_ROOT)
const backends = process.env.LAB_BACKENDS.split(',').map(Number)
const tls = { key: readFileSync(process.env.LAB_TLS_KEY), cert: readFileSync(process.env.LAB_TLS_CERT) }
let next = 0
const server = https.createServer(tls, (request, response) => {
  let pathname
  try { pathname = decodeURIComponent(new URL(request.url, 'https://localhost').pathname) }
  catch { response.writeHead(400); response.end(); return }
  if (pathname.startsWith('/api/')) {
    const requested = request.headers['x-lab-target']
    const target = requested === '1' || requested === '2' ? Number(requested) - 1 : next++ % backends.length
    const headers = { ...request.headers, 'x-forwarded-proto': 'https', 'x-forwarded-host': request.headers.host }
    delete headers['x-lab-target']
    delete headers['x-forwarded-for']
    const upstream = http.request({ host: '127.0.0.1', port: backends[target], method: request.method,
      path: request.url.slice(4), headers }, result => {
      response.writeHead(result.statusCode, result.headers)
      result.pipe(response)
    })
    upstream.on('error', () => { if (!response.headersSent) response.writeHead(502); response.end() })
    request.on('aborted', () => upstream.destroy())
    request.pipe(upstream)
    return
  }
  const file = resolve(root, '.' + (['/', '/login', '/cadastro'].includes(pathname) ? '/index.html' : pathname))
  if (!file.startsWith(root + sep)) { response.writeHead(404); response.end(); return }
  try {
    const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' }
    const content = readFileSync(file)
    response.writeHead(200, { 'Content-Type': types[extname(file)] || 'application/octet-stream' })
    response.end(content)
  } catch { response.writeHead(404); response.end() }
})
server.listen(Number(process.env.LAB_HTTPS_PORT), '127.0.0.1')
process.on('SIGTERM', () => server.close())
