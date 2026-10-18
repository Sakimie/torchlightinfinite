'use strict';

const path = require('node:path');
const { pathToFileURL, fileURLToPath } = require('node:url');
const { Worker } = require('node:worker_threads');

const IPC_PREFIX = 'torchlight:';
const MAX_IMAGE_LENGTH = 48 * 1024 * 1024;

function isLocalRendererURL(value, rendererPath) {
  if (typeof value !== 'string') return false;
  try {
    const url = new URL(value);
    return url.protocol === 'file:' && !url.hostname && !url.search
      && path.resolve(fileURLToPath(url)) === path.resolve(rendererPath);
  } catch {
    return false;
  }
}

function sourceType(id) {
  if (typeof id !== 'string' || id.length > 200) return null;
  if (id.startsWith('window:')) return 'window';
  if (id.startsWith('screen:')) return 'screen';
  return null;
}

function captureSize(displays, source) {
  const usable = (displays || []).filter((display) => display && display.size);
  const pixels = (display) => ({
    width: Math.max(1, Math.ceil(display.size.width * (display.scaleFactor || 1))),
    height: Math.max(1, Math.ceil(display.size.height * (display.scaleFactor || 1))),
  });
  const matched = source && sourceType(source.id) === 'screen'
    ? usable.find((display) => String(display.id) === String(source.display_id)) : null;
  if (matched) return pixels(matched);
  // A window may span several displays. Ask for a sufficiently large frame,
  // without resizing the returned image or reducing it to a preview thumbnail.
  const sizes = usable.map(pixels);
  return {
    width: Math.min(16384, Math.max(3840, sizes.reduce((sum, size) => sum + size.width, 0))),
    height: Math.min(16384, Math.max(2160, sizes.reduce((sum, size) => sum + size.height, 0))),
  };
}

function friendlyError(error) {
  const message = error && error.message ? String(error.message) : String(error || '');
  if (/[\u3400-\u9fff]/.test(message)) return message.slice(0, 600);
  if (/permission|access denied|notallowed|denied/i.test(message)) {
    return '系统未允许读取此窗口，请检查屏幕录制权限后重试。';
  }
  if (/enoent|cannot find module|module_not_found/i.test(message)) {
    return '应用文件不完整，请重新解压完整压缩包后打开助手。';
  }
  if (/timeout|timed out/i.test(message)) return '本地分析超时，请稍后重试。';
  return '操作失败，请重试；详细原因已记录在应用终端。';
}

function assertDataURL(dataUrl) {
  if (typeof dataUrl !== 'string' || dataUrl.length > MAX_IMAGE_LENGTH
    || !/^data:image\/(?:png|jpe?g|webp);base64,[A-Za-z0-9+/=\r\n]+$/.test(dataUrl)) {
    throw new Error('请提供 PNG、JPEG 或 WebP 截图，图片大小不超过 36 MB。');
  }
}

function assertVisionProfile(profile) {
  if (!profile || typeof profile !== 'object' || Array.isArray(profile)) {
    throw new Error('请导入校准配置内容，不能使用文件路径。');
  }
  let serialized;
  try { serialized = JSON.stringify(profile); } catch { throw new Error('校准配置格式无效。'); }
  if (!serialized || Buffer.byteLength(serialized, 'utf8') > 2 * 1024 * 1024) {
    throw new Error('校准配置过大，请使用小于 2 MB 的配置文件。');
  }
}

class ServiceWorker {
  constructor(options = {}) {
    this.workerPath = options.workerPath || path.join(__dirname, 'worker.js');
    this.timeoutMs = options.timeoutMs || 90000;
    this.worker = null;
    this.pending = new Map();
    this.nextId = 0;
    this.closed = false;
  }

  ensureWorker() {
    if (this.worker) return;
    const worker = new Worker(this.workerPath);
    this.worker = worker;
    worker.on('message', (message) => {
      const request = message && this.pending.get(message.id);
      if (!request) return;
      clearTimeout(request.timer);
      this.pending.delete(message.id);
      if (message.ok) request.resolve(message.result);
      else request.reject(new Error(message.error || '本地分析失败，请检查输入后重试。'));
    });
    const fail = (error) => {
      if (this.worker !== worker) return;
      this.worker = null;
      for (const request of this.pending.values()) {
        clearTimeout(request.timer);
        request.reject(error);
      }
      this.pending.clear();
    };
    worker.on('error', fail);
    worker.on('exit', (code) => fail(new Error(`本地分析进程已退出（${code}），请重新分析。`)));
  }

  call(method, args = [], timeoutMs = this.timeoutMs) {
    if (this.closed) return Promise.reject(new Error('应用正在退出。'));
    if (this.pending.size >= 8) return Promise.reject(new Error('已有多项分析正在进行，请稍候再试。'));
    this.ensureWorker();
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        // Terminate a stuck calculation rather than letting future jobs pile up.
        const worker = this.worker;
        this.worker = null;
        for (const request of this.pending.values()) {
          clearTimeout(request.timer);
          request.reject(new Error(method === 'bootstrap'
            ? '本地分析服务启动超时，请重新打开助手。'
            : '计算超时，请稍后重试。'));
        }
        this.pending.clear();
        if (worker) void worker.terminate();
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      try {
        this.worker.postMessage({ id, method, args });
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  async close() {
    this.closed = true;
    const worker = this.worker;
    this.worker = null;
    for (const request of this.pending.values()) {
      clearTimeout(request.timer);
      request.reject(new Error('应用正在退出。'));
    }
    this.pending.clear();
    if (worker) await worker.terminate();
  }
}

class CaptureSources {
  constructor(desktopCapturer, screen) {
    this.desktopCapturer = desktopCapturer;
    this.screen = screen;
    this.listed = new Map();
  }

  async sources() {
    const sources = await this.desktopCapturer.getSources({
      // Listing sources does not take preview pictures of other windows/screens.
      types: ['window', 'screen'], thumbnailSize: { width: 0, height: 0 }, fetchWindowIcons: false,
    });
    this.listed = new Map(sources.map((source) => [source.id, source]));
    return sources.map((source) => {
      return {
        id: source.id,
        name: source.name,
        type: sourceType(source.id),
        displayId: source.display_id,
        thumbnailUrl: null,
        width: 0,
        height: 0,
      };
    }).sort((a, b) => {
      const priority = (source) => /torchlight|火炬/i.test(source.name) ? 0 : source.type === 'window' ? 1 : 2;
      return priority(a) - priority(b);
    });
  }

  async capture(id) {
    const type = sourceType(id);
    if (!type || !this.listed.has(id)) throw new Error('请先刷新来源列表，并明确选择要截图的游戏窗口或屏幕。');
    const thumbnailSize = captureSize(this.screen.getAllDisplays(), this.listed.get(id));
    const current = await this.desktopCapturer.getSources({ types: [type], thumbnailSize, fetchWindowIcons: false });
    const source = current.find((entry) => entry.id === id);
    if (!source) {
      this.listed.delete(id);
      throw new Error('所选窗口已关闭或发生变化，请重新选择截图来源。');
    }
    if (source.thumbnail.isEmpty()) throw new Error('未取得窗口画面，请将游戏切换到无边框或窗口模式后重试。');
    const { width, height } = source.thumbnail.getSize();
    return { dataUrl: source.thumbnail.toDataURL(), width, height, capturedAt: new Date().toISOString() };
  }
}

async function run() {
  const { app, BrowserWindow, desktopCapturer, screen, ipcMain, globalShortcut } = require('electron');
  const rendererPath = path.join(__dirname, '..', 'renderer', 'index.html');
  const smoke = process.argv.includes('--smoke');
  const service = new ServiceWorker();
  let win = null;
  let compact = false;
  let expandedBounds = null;

  await app.whenReady();
  if (!process.env.TORCHLIGHT_DATA_DIR) process.env.TORCHLIGHT_DATA_DIR = app.getPath('userData');
  if (smoke) console.log('TORCHLIGHT_SMOKE_APP_READY');
  const capture = new CaptureSources(desktopCapturer, screen);
  const workArea = screen.getPrimaryDisplay().workAreaSize;
  if (smoke) console.log('TORCHLIGHT_SMOKE_DISPLAY_READY', JSON.stringify(workArea));
  win = new BrowserWindow({
    width: Math.min(1280, workArea.width),
    height: Math.min(860, workArea.height),
    minWidth: 400,
    minHeight: 440,
    title: '火炬之光 · 监狱助手',
    backgroundColor: '#0b1220',
    show: false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      webviewTag: false,
    },
  });
  if (smoke) console.log('TORCHLIGHT_SMOKE_WINDOW_CREATED');

  const contents = win.webContents;
  contents.setWindowOpenHandler(() => ({ action: 'deny' }));
  contents.on('will-navigate', (event) => event.preventDefault());
  contents.on('will-attach-webview', (event) => event.preventDefault());
  contents.session.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
  contents.session.setPermissionCheckHandler(() => false);
  contents.session.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*', 'ws://*/*', 'wss://*/*'] }, (_details, callback) => callback({ cancel: true }));

  const trusted = (event) => event.sender === contents
    && !!event.senderFrame
    && event.senderFrame === contents.mainFrame
    && isLocalRendererURL(event.senderFrame.url, rendererPath);
  const handle = (name, action) => {
    ipcMain.handle(IPC_PREFIX + name, async (event, ...args) => {
      if (!trusted(event)) throw new Error('此页面无权访问本地助手。');
      try {
        return await action(...args);
      } catch (error) {
        console.error(`[torchlight:${name}]`, error && error.message ? error.message : error);
        throw new Error(friendlyError(error));
      }
    });
  };

  const show = () => {
    if (!win || win.isDestroyed()) return;
    if (win.isMinimized()) win.restore();
    win.show();
    win.focus();
  };
  const setCompact = (enabled) => {
    if (typeof enabled !== 'boolean') throw new Error('窗口模式参数无效。');
    if (compact === enabled) return compact;
    compact = enabled;
    if (compact) {
      expandedBounds = win.getBounds();
      const area = screen.getDisplayMatching(expandedBounds).workArea;
      win.setBounds({
        x: Math.max(area.x, Math.min(expandedBounds.x, area.x + area.width - 420)),
        y: Math.max(area.y, Math.min(expandedBounds.y, area.y + area.height - 600)),
        width: Math.min(420, area.width),
        height: Math.min(600, area.height),
      });
    } else if (expandedBounds) {
      win.setBounds(expandedBounds);
    }
    contents.send(IPC_PREFIX + 'window-state', { compact, alwaysOnTop: win.isAlwaysOnTop() });
    return compact;
  };

  handle('bootstrap', async () => ({
    ...await service.call('bootstrap', [], 30000),
    desktop: { platform: process.platform, alwaysOnTop: win.isAlwaysOnTop(), compact },
  }));
  handle('sources', () => capture.sources());
  handle('capture', (sourceId) => capture.capture(sourceId));
  handle('scan', (dataUrl, previousBoard) => {
    assertDataURL(dataUrl);
    return service.call('scan', [dataUrl, previousBoard]);
  });
  handle('recommend', (board, options) => service.call('recommend', [board, options]));
  handle('demo', () => service.call('demo'));
  handle('report', () => service.call('report'));
  handle('new-board', () => service.call('newBoard'));
  handle('vision-profile', () => service.call('visionProfile'));
  handle('learn-vision', (payload) => {
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) throw new Error('请提供截图和已核对的局面以完成校准。');
    assertDataURL(payload.dataUrl);
    return service.call('learnVision', [{ dataUrl: payload.dataUrl, board: payload.board, calibration: payload.calibration }]);
  });
  handle('import-vision', (profile) => {
    assertVisionProfile(profile);
    return service.call('importVision', [profile]);
  });
  handle('reset-vision', () => service.call('resetVision'));
  handle('always-on-top', (enabled) => {
    if (typeof enabled !== 'boolean') throw new Error('置顶参数无效。');
    win.setAlwaysOnTop(enabled);
    contents.send(IPC_PREFIX + 'window-state', { compact, alwaysOnTop: enabled });
    return win.isAlwaysOnTop();
  });
  handle('compact', setCompact);

  const shortcuts = [
    ['Control+Alt+P', () => {
      // The renderer asks the user to select a source before any capture.
      if (win && !win.isDestroyed()) contents.send(IPC_PREFIX + 'capture-request');
    }],
    ['Control+Alt+O', () => { if (win.isVisible()) win.hide(); else show(); }],
    ['Control+Alt+A', () => { setCompact(false); show(); }],
  ];
  if (!smoke) {
    for (const [accelerator, action] of shortcuts) {
      if (!globalShortcut.register(accelerator, action)) console.warn(`快捷键 ${accelerator} 已被占用，可使用界面按钮。`);
    }
  }
  app.on('before-quit', () => {
    globalShortcut.unregisterAll();
    void service.close();
  });
  app.on('window-all-closed', () => app.quit());

  win.once('ready-to-show', async () => {
    win.show();
    if (smoke) {
      try {
        const result = await contents.executeJavaScript(`(async () => {
          const api = window.torchlight;
          if (!api || typeof api.capture !== 'function' || typeof api.recommend !== 'function' || typeof api.report !== 'function') throw new Error('本地页面接口未正确载入。');
          const bootstrap = await api.bootstrap();
          if (!Array.isArray(bootstrap.tools) || !bootstrap.tools.length) throw new Error('本地规则引擎未完成初始化。');
          const board = await api.demo();
          const recommendation = await api.recommend(board, { rollouts: 4, budgetMs: 300 });
          if (!recommendation.primary || !recommendation.primary.action) throw new Error('示例局面未能生成建议。');
          return { tools: bootstrap.tools.length, action: recommendation.primary.action, samples: recommendation.stats.samples };
        })()`);
        console.log('TORCHLIGHT_SMOKE_READY', JSON.stringify(result));
        setTimeout(() => app.quit(), 300);
      } catch (error) {
        console.error(friendlyError(error));
        app.exit(1);
      }
    }
  });
  if (smoke) {
    const timeout = setTimeout(() => { console.error('应用窗口启动超时。'); app.exit(1); }, 30000);
    timeout.unref();
    contents.on('render-process-gone', (_event, details) => {
      console.error('渲染进程异常退出。', details.reason);
      app.exit(1);
    });
  }
  await win.loadURL(pathToFileURL(rendererPath).href);
}

module.exports = { isLocalRendererURL, sourceType, captureSize, friendlyError, assertDataURL, assertVisionProfile, ServiceWorker, CaptureSources };

// Electron 44 loads the main entry through its own loader, so require.main
// is not necessarily this module. Ordinary Node imports remain side-effect free.
if (require.main === module || (process.versions.electron && process.type === 'browser')) {
  run().catch((error) => {
    console.error('火炬之光助手无法启动：', friendlyError(error));
    process.exitCode = 1;
    try { require('electron').app.exit(1); } catch { /* Plain Node invocation. */ }
  });
}
