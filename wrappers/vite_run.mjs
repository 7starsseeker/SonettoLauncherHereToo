/**
 * SonettoHere 启动器 —— 前端（Vite dev server）包装器（launcher 自有文件，不参与项目代码）。
 *
 * 为什么要包装：Vite 5 的 dev server 只监听 SIGTERM，而 Windows 上无法从外部向
 * node 进程投递 SIGTERM（Node 在 Windows 上把 SIGTERM 当作直接终止），Ctrl+C 又只会
 * 变成 Vite 没有监听的 SIGINT —— 所以「关控制台窗口」必然是硬退。
 * 这里改用 Vite 官方程序化 API 起服务，并挂上 SIGINT/SIGTERM 与 stdin 控制通道，
 * 收到指令后调用 server.close()（即 Vite 自己 SIGTERM 路径做的事：关文件监听、
 * 通知 HMR 客户端、等待在途请求），实现真正优雅的关闭。
 *
 * 配置来源不变：仍由 Vite 自己从 web/vite.config.ts 读取（端口、代理、__API_TOKEN__ 注入）。
 *
 * 用法（工作目录须为 web/）：
 *     node <本文件>
 * 环境变量：
 *     SONETTO_VITE_MODULE  web/node_modules/vite 的 ESM 入口绝对 file:// URL
 */

import process from 'node:process'

const viteModuleUrl = process.env.SONETTO_VITE_MODULE
if (!viteModuleUrl) {
  console.error('[launcher] 缺少环境变量 SONETTO_VITE_MODULE')
  process.exit(2)
}

let createServer
try {
  ;({ createServer } = await import(viteModuleUrl))
} catch (err) {
  console.error(`[launcher] 无法加载 Vite (${viteModuleUrl}): ${err}`)
  process.exit(2)
}

const server = await createServer()

try {
  await server.listen()
} catch (err) {
  console.error(`[launcher] Vite 启动失败: ${err}`)
  process.exit(1)
}

const url = server.resolvedUrls?.local?.[0] ?? 'http://localhost:5173/'
console.log('[launcher] 前端 dev server 已就绪，控制通道就绪')
console.log(`::SONETTO_URL::${url}`)
server.printUrls()

let closing = false
async function shutdown(reason) {
  if (closing) return
  closing = true
  console.log(`[launcher] ${reason}，开始优雅关闭前端`)
  try {
    await server.close()
    console.log('[launcher] 前端 dev server 已关闭')
  } catch (err) {
    console.error(`[launcher] 关闭前端时出错: ${err}`)
  } finally {
    process.exit(0)
  }
}

// 控制台信号（启动器兜底路径也会用到）
process.on('SIGINT', () => shutdown('收到 SIGINT'))
process.on('SIGTERM', () => shutdown('收到 SIGTERM'))
process.on('SIGBREAK', () => shutdown('收到 SIGBREAK'))

// stdin 控制通道：收到指令或父进程退出（EOF）时优雅关闭
process.stdin.setEncoding('utf8')
process.stdin.on('data', (chunk) => {
  if (String(chunk).trim().toLowerCase() === 'shutdown') shutdown('收到关闭指令')
})
process.stdin.on('end', () => shutdown('控制通道关闭'))
process.stdin.on('error', () => shutdown('控制通道异常'))
process.stdin.resume()
